using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SeaShell.Engine;

/// <summary>
/// Recursively resolves //sea_inc / //css_inc directives.
/// Each include is read into memory, its own directives are parsed,
/// and transitive includes are followed. Deduplicates by absolute path.
///
/// Returns all sources in dependency order (includes first, main script last)
/// and an aggregated DirectiveSet.
/// </summary>
public sealed class IncludeResolver
{
	private readonly List<string> _searchPaths;
	private readonly List<string> _scriptIncludeDirs = new(); // from //sea_incdir, per-resolve

	public IncludeResolver(IEnumerable<string>? extraSearchPaths = null)
	{
		_searchPaths = new List<string>();

		// SEASHELL_INC environment variable
		var envInc = Environment.GetEnvironmentVariable("SEASHELL_INC");
		if (!string.IsNullOrEmpty(envInc) && Directory.Exists(envInc))
			_searchPaths.Add(envInc);

		// Platform include dir
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			var progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
			var seashellInc = Path.Combine(progData, "seashell", "inc");
			if (Directory.Exists(seashellInc))
				_searchPaths.Add(seashellInc);

			// CS-Script compatibility fallback
			var csscriptInc = Path.Combine(progData, "cs-script", "inc");
			if (Directory.Exists(csscriptInc))
				_searchPaths.Add(csscriptInc);
		}
		else
		{
			var localShare = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".local", "share", "seashell", "inc");
			if (Directory.Exists(localShare))
				_searchPaths.Add(localShare);

			// /usr/local/share/ — admin-installed software
			if (Directory.Exists("/usr/local/share/seashell/inc"))
				_searchPaths.Add("/usr/local/share/seashell/inc");
			if (Directory.Exists("/usr/local/share/cs-script/inc"))
				_searchPaths.Add("/usr/local/share/cs-script/inc");

			// /usr/share/ — system/distro-managed (FHS standard)
			if (Directory.Exists("/usr/share/seashell/inc"))
				_searchPaths.Add("/usr/share/seashell/inc");
			if (Directory.Exists("/usr/share/cs-script/inc"))
				_searchPaths.Add("/usr/share/cs-script/inc");
		}

		if (extraSearchPaths != null)
			_searchPaths.AddRange(extraSearchPaths);
	}

	public sealed record ResolvedScript
	{
		/// <summary>All sources in dependency order: includes first, main script last.</summary>
		public required List<(string Path, string Source)> Sources { get; init; }

		/// <summary>Aggregated directives from all sources.</summary>
		public required DirectiveParser.DirectiveSet Directives { get; init; }
	}

	public ResolvedScript Resolve(string mainScriptPath)
	{
		mainScriptPath = Path.GetFullPath(mainScriptPath);
		var mainDir = Path.GetDirectoryName(mainScriptPath)!;

		_scriptIncludeDirs.Clear(); // fresh per resolve

		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var orderedSources = new List<(string Path, string Source)>();
		var aggregated = new DirectiveParser.DirectiveSet();

		// Resolve the main script and all its transitive includes
		ResolveRecursive(mainScriptPath, mainDir, visited, orderedSources, aggregated);

		return new ResolvedScript
		{
			Sources = orderedSources,
			Directives = aggregated,
		};
	}

	private void ResolveRecursive(
		string filePath,
		string scriptDir,
		HashSet<string> visited,
		List<(string Path, string Source)> orderedSources,
		DirectiveParser.DirectiveSet aggregated)
	{
		filePath = Path.GetFullPath(filePath);
		if (!visited.Add(filePath))
			return; // already processed (dedup / cycle break)

		// Read source — close file handle immediately
		var source = StripShebang(File.ReadAllText(filePath));
		var lang = ScriptLanguageDetector.FromExtension(filePath);
		var directives = DirectiveParser.Parse(source, lang);

		// Collect script-local include dirs (//sea_incdir) — these supplement
		// the global search paths for this script and its includes.
		foreach (var dir in directives.IncludeDirs)
		{
			var expanded = Path.GetFullPath(dir);
			if (Directory.Exists(expanded) && !_searchPaths.Contains(expanded))
				_scriptIncludeDirs.Add(expanded);
		}

		// Process includes first (depth-first → dependencies before dependents)
		foreach (var inc in directives.Includes)
		{
			var resolved = FindInclude(inc, scriptDir);
			if (resolved == null)
				throw new FileNotFoundException($"Include not found: {inc} (from {filePath})", inc);

			var incDir = Path.GetDirectoryName(resolved)!;
			ResolveRecursive(resolved, incDir, visited, orderedSources, aggregated);
		}

		// Add this source (after its includes)
		orderedSources.Add((filePath, source));

		// Merge directives into aggregated set
		foreach (var n in directives.NuGets)
		{
			if (!aggregated.NuGets.Exists(x =>
				x.PackageName.Equals(n.PackageName, StringComparison.OrdinalIgnoreCase)))
				aggregated.NuGets.Add(n);
		}
		foreach (var r in directives.References)
		{
			if (!aggregated.References.Contains(r))
				aggregated.References.Add(r);
		}
		foreach (var d in directives.IncludeDirs)
		{
			if (!aggregated.IncludeDirs.Contains(d))
				aggregated.IncludeDirs.Add(d);
		}
		if (directives.WebApp) aggregated.WebApp = true;
		if (directives.Elevate) aggregated.Elevate = true;
		if (directives.Watch) aggregated.Watch = true;
		if (directives.Restart) aggregated.Restart = true;
		if (directives.MutexScope > aggregated.MutexScope) aggregated.MutexScope = directives.MutexScope;
		if (directives.MutexAttach) aggregated.MutexAttach = true;
		if (directives.Window) aggregated.Window = true;
		if (directives.Console) aggregated.Console = true;
	}

	/// <summary>
	/// Strip Unix shebang (#!/usr/bin/env sea) from the first line.
	/// Replaced with a blank line to preserve line numbers in diagnostics.
	/// </summary>
	private static string StripShebang(string source)
	{
		if (!source.StartsWith("#!")) return source;
		var newline = source.IndexOfAny(new[] { '\n', '\r' });
		if (newline < 0) return ""; // entire file is just a shebang
		return source[newline..];
	}

	private string? FindInclude(string name, string scriptDir)
	{
		// 1. Script's own directory
		var candidate = Path.Combine(scriptDir, name);
		if (File.Exists(candidate)) return Path.GetFullPath(candidate);

		// 2. Script-declared include dirs (//sea_incdir)
		foreach (var dir in _scriptIncludeDirs)
		{
			candidate = Path.Combine(dir, name);
			if (File.Exists(candidate)) return Path.GetFullPath(candidate);
		}

		// 3. Global search paths (SEASHELL_INC, platform dirs, CS-Script fallback)
		foreach (var dir in _searchPaths)
		{
			candidate = Path.Combine(dir, name);
			if (File.Exists(candidate)) return Path.GetFullPath(candidate);
		}

		return null;
	}
}
