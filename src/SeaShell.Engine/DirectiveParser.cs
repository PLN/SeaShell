using System;
using System.Collections.Generic;
using System.IO;

namespace SeaShell.Engine;

/// <summary>
/// Parses //sea_ and //css_ directives from the top of a script file.
/// Reads the source into memory (no file locks held after return).
/// </summary>
public enum ScriptLanguage
{
	CSharp,
	VisualBasic,
}

public static class ScriptLanguageDetector
{
	public static ScriptLanguage FromExtension(string path) =>
		Path.GetExtension(path).ToLowerInvariant() switch
		{
			".vb" => ScriptLanguage.VisualBasic,
			_ => ScriptLanguage.CSharp,
		};
}

public static class DirectiveParser
{
	public sealed record DirectiveSet
	{
		public List<string> Includes { get; init; } = new();
		public List<string> IncludeDirs { get; init; } = new();
		public List<NuGetRef> NuGets { get; init; } = new();
		public List<string> References { get; init; } = new();
		public bool WebApp { get; set; }
		public bool Elevate { get; set; }
		public bool Watch { get; set; }
	}

	public sealed record NuGetRef(string PackageName, string? Version);

	/// <summary>
	/// Parse directives from source text. Scans from the top, stops at the first
	/// line that isn't a directive, comment, using, blank, or attribute.
	/// </summary>
	public static DirectiveSet Parse(string source, ScriptLanguage lang = ScriptLanguage.CSharp)
	{
		var result = new DirectiveSet();

		foreach (var line in source.AsSpan().EnumerateLines())
		{
			var trimmed = line.Trim();

			// Skip blank lines
			if (trimmed.IsEmpty) continue;

			// C# directives: //sea_ or //css_
			// VB directives:  'sea_ or 'css_
			var isCSharpDirective = trimmed.StartsWith("//sea_") || trimmed.StartsWith("//css_");
			var isVbDirective = trimmed.StartsWith("'sea_") || trimmed.StartsWith("'css_");

			// Skip regular comments (but not directives)
			if (lang == ScriptLanguage.CSharp)
			{
				if (trimmed.StartsWith("//") && !isCSharpDirective)
					continue;
			}
			else
			{
				if (trimmed.StartsWith("'") && !isVbDirective)
					continue;
				if (trimmed.StartsWith("REM ", StringComparison.OrdinalIgnoreCase))
					continue;
			}

			// Skip using/imports statements
			if (trimmed.StartsWith("using ") || trimmed.StartsWith("global using "))
				continue;
			if (trimmed.StartsWith("Imports ", StringComparison.OrdinalIgnoreCase))
				continue;

			// Skip attributes (C#: [assembly: ...], VB: <Assembly: ...>)
			if (trimmed.StartsWith("[") || trimmed.StartsWith("<"))
				continue;

			// Parse directives
			if (isCSharpDirective || isVbDirective)
			{
				ParseDirective(trimmed.ToString(), result, isVbDirective);
				continue;
			}

			// First real code line — stop scanning
			break;
		}

		return result;
	}

	private static void ParseDirective(string line, DirectiveSet result, bool isVb = false)
	{
		// Strip the prefix:  //sea_ //css_ = 6 chars,  'sea_ 'css_ = 5 chars
		var prefixLen = isVb ? 5 : 6;
		var rest = line[prefixLen..].Trim();

		// Split into directive name and argument
		var spaceIdx = rest.IndexOf(' ');
		var directive = spaceIdx >= 0 ? rest[..spaceIdx] : rest;
		var arg = spaceIdx >= 0 ? rest[(spaceIdx + 1)..].Trim() : "";

		// Expand environment variables in arguments that are paths
		arg = (directive is "incdir" or "ref") ? ExpandPath(arg) : arg;

		switch (directive)
		{
			case "inc":
				if (!string.IsNullOrEmpty(arg))
					result.Includes.Add(arg);
				break;

			case "incdir":
				if (!string.IsNullOrEmpty(arg))
					result.IncludeDirs.Add(arg);
				break;

			case "nuget":
				if (!string.IsNullOrEmpty(arg))
				{
					// Support both "Package 1.0.0" and "Package, 1.0.0" (CS-Script style)
					var parts = arg.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
					var version = parts.Length > 1 ? parts[1] : null;
					result.NuGets.Add(new NuGetRef(parts[0], version));
				}
				break;

			case "ref":
				if (!string.IsNullOrEmpty(arg))
					result.References.Add(arg);
				break;

			case "webapp":
				result.WebApp = true;
				break;

			case "elevate":
				result.Elevate = true;
				break;

			case "watch":
				result.Watch = true;
				break;
		}
	}

	/// <summary>
	/// Cross-platform environment variable expansion for paths.
	/// Handles: %VAR% (Windows), $VAR, ${VAR} (Unix), ~/  (home dir).
	/// All styles work on all platforms — write once, run anywhere.
	/// </summary>
	internal static string ExpandPath(string path)
	{
		if (string.IsNullOrEmpty(path)) return path;

		// ~ → home directory
		if (path.StartsWith("~/") || path.StartsWith("~\\"))
			path = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				path[2..]);

		// %VAR% → expand (works cross-platform via Environment.GetEnvironmentVariable)
		path = System.Text.RegularExpressions.Regex.Replace(path, @"%([^%]+)%", m =>
			Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);

		// ${VAR} → expand
		path = System.Text.RegularExpressions.Regex.Replace(path, @"\$\{([^}]+)\}", m =>
			Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);

		// $VAR → expand (word characters only, avoid matching $( or $$)
		path = System.Text.RegularExpressions.Regex.Replace(path, @"\$([A-Za-z_][A-Za-z0-9_]*)", m =>
			Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);

		// Normalize separators for current platform
		path = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

		return path;
	}
}
