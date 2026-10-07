using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Serilog;
using VB = Microsoft.CodeAnalysis.VisualBasic;

namespace SeaShell.Engine;

/// <summary>
/// Roslyn compilation pipeline: script sources → in-memory byte[] → temp artifacts on disk.
/// Generates .dll + .runtimeconfig.json (+ .deps.json when NuGet refs are present).
/// </summary>
public sealed class ScriptCompiler
{
	private static readonly ILogger _log = Log.ForContext<ScriptCompiler>();

	private readonly string _cacheDir;
	private readonly IncludeResolver _includeResolver;
	private readonly string _scriptAssemblyPath;
	private readonly string _engineDir;

	public readonly NuGetResolver NuGetResolver;

	// Track last resolved source list per script (for watch mode file list)
	private readonly ConcurrentDictionary<string, string[]> _lastSourcesByScript = new(StringComparer.OrdinalIgnoreCase);

	// One lock object per output dir (see Compile)
	private readonly ConcurrentDictionary<string, object> _compileLocks = new(CacheTracker.PathComparer);

	/// <summary>Creation and hit times of this cache dir's entries; drives <see cref="SweepCache"/>.</summary>
	public readonly CacheTracker Tracker;

	public ScriptCompiler(string? cacheDir = null) : this(cacheDir, clock: null) { }

	internal ScriptCompiler(string? cacheDir, Func<DateTime>? clock)
	{
		var version = typeof(ScriptCompiler).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";
		// One spelling for every path derived from it: the per-dir compile locks are keyed
		// by path, so a relative or "/"-separated variant would get a lock of its own.
		_cacheDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDir ?? SeaShellPaths.CacheDir(version)));
		Directory.CreateDirectory(_cacheDir);
		Tracker = new CacheTracker(_cacheDir, clock);

		// Secure the data root on Linux — owner-only access
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			try
			{
				File.SetUnixFileMode(SeaShellPaths.DataDir,
					UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
			catch { }
		}

		// Locate SeaShell runtime DLLs — prefer the Engine assembly's own directory
		// (correct for both daemon and NuGet-hosted ScriptHost scenarios).
		// Falls back to AppContext.BaseDirectory for compatibility.
		_engineDir = Path.GetDirectoryName(typeof(ScriptCompiler).Assembly.Location)
			?? AppContext.BaseDirectory;
		_scriptAssemblyPath = Path.Combine(_engineDir, "SeaShell.Script.dll");
		_includeResolver = new IncludeResolver();
		NuGetResolver = new NuGetResolver();
	}

	public sealed record CompileResult
	{
		public bool Success { get; init; }
		public bool Elevate { get; init; }
		public bool Watch { get; init; }
		public bool Restart { get; init; }
		public byte MutexScope { get; init; }
		public bool MutexAttach { get; init; }
		public string? AssemblyPath { get; init; }
		public string? RuntimeConfigPath { get; init; }
		public string? DepsJsonPath { get; init; }
		public string? ManifestPath { get; init; }
		public string? Error { get; init; }
		/// <summary>Path to SeaShell.Script.dll for DOTNET_STARTUP_HOOKS (binaries without SeaShell ref).</summary>
		public string? StartupHookPath { get; init; }
		/// <summary>When true, run the AssemblyPath directly instead of via dotnet exec (Tier 2/3 .exe).</summary>
		public bool DirectExe { get; init; }
	}

	public CompileResult Compile(string scriptPath, string[] args)
	{
		scriptPath = Path.GetFullPath(scriptPath);

		// ── Binary pass-through (.dll / .exe / extensionless ELF) ──────
		var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
		if (ext is ".dll" or ".exe")
			return CompileBinary(scriptPath);
		// Extensionless files that are not scripts — treat as binary (Linux ELF single-file)
		if (ext == "" && File.Exists(scriptPath))
			return CompileBinary(scriptPath);

		var scriptName = Path.GetFileNameWithoutExtension(scriptPath);
		var language = ScriptLanguageDetector.FromExtension(scriptPath);

		_log.Debug("Compiling {ScriptName} ({Language})", scriptName, language);

		// ── Resolve includes ────────────────────────────────────────────
		IncludeResolver.ResolvedScript resolved;
		try
		{
			resolved = _includeResolver.Resolve(scriptPath);
			_lastSourcesByScript[scriptPath] = resolved.Sources.Select(s => s.Path).ToArray();
			_log.Debug("Resolved {Count} source files, {NuGetCount} NuGet refs, WebApp={WebApp}",
				resolved.Sources.Count, resolved.Directives.NuGets.Count, resolved.Directives.WebApp);
		}
		catch (Exception ex)
		{
			_log.Error(ex, "Include resolution failed for {ScriptName}", scriptName);
			return new CompileResult { Error = ex.Message };
		}

		var unsupportedNuGetDirective = FindUnsupportedNuGetDirective(resolved.Sources);
		if (unsupportedNuGetDirective != null)
			return new CompileResult { Error = unsupportedNuGetDirective };

		// ── Check cache ─────────────────────────────────────────────────
		// Hash includes source content, engine fingerprint, and resolved direct
		// NuGet versions. Transitive deps are not included (immutable by convention).
		// Version resolution is lightweight: explicit versions are free, versionless
		// directives do a single directory listing in the NuGet cache (~1-2ms each).
		var nugetVersions = resolved.Directives.NuGets
			.Select(n => $"{n.PackageName.ToLowerInvariant()}@{NuGetResolver.ResolveDirectVersion(n.PackageName, n.Version) ?? "?"}")
			.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
			.ToList();
		var hash = CompilationCache.ComputeHash(resolved.Sources, nugetVersions);
		var outputDir = Path.Combine(_cacheDir, $"{scriptName}_{hash[..8]}");
		var dllPath = Path.Combine(outputDir, $"{scriptName}.dll");
		var runtimeConfigPath = Path.Combine(outputDir, $"{scriptName}.runtimeconfig.json");

		// Same script and inputs → same output dir. Serialize work on it: a concurrent
		// request must not take a half-staged dir for a cache hit or race its copies.
		// Different scripts still compile in parallel.
		lock (_compileLocks.GetOrAdd(outputDir, _ => new object()))
			return CompileInto(scriptPath, scriptName, language, resolved, hash, outputDir, dllPath, runtimeConfigPath);
	}

	/// <summary>
	/// Delete all cached output dirs for a script. Takes the same per-dir lock as
	/// <see cref="Compile"/>, so a clear cannot interleave with a compile that is
	/// still staging or publishing that dir.
	/// </summary>
	public void ClearCache(string scriptPath)
	{
		var scriptName = Path.GetFileNameWithoutExtension(scriptPath);
		string[] dirs;
		try { dirs = Directory.GetDirectories(_cacheDir, $"{scriptName}_*"); }
		catch { return; }
		foreach (var dir in dirs)
		{
			lock (_compileLocks.GetOrAdd(dir, _ => new object()))
			{
				// The script that asked for the clear usually still runs from this dir. If
				// that blocks the move, fall back to deleting what can be deleted (marker
				// first): the recompile that follows repairs the dir in place.
				if (!TryMoveToTrash(dir))
					CompilationCache.ClearDir(dir);
				Tracker.RecordRemoved(Path.GetFileName(dir), explicitClear: true);
			}
		}
		EmptyTrash();
	}

	/// <summary>
	/// Delete expired entries (see <see cref="CacheTracker"/>). Each delete holds the
	/// per-dir compile lock and re-checks expiry under it, so a hit or keep-alive that
	/// lands meanwhile wins. An entry still in use — files loaded by a running process
	/// that sent no keep-alive (its invoker was killed, an older CLI, a direct launch) —
	/// counts as a hit instead. Returns the number of dirs removed.
	/// </summary>
	public int SweepCache()
	{
		var removed = 0;
		var candidates = Tracker.SweepCandidates();
		if (candidates.Count > 0)
		{
			var inUse = CacheDirsInUse.Find(_cacheDir);
			foreach (var key in candidates)
			{
				var dir = Path.Combine(_cacheDir, key);
				lock (_compileLocks.GetOrAdd(dir, _ => new object()))
				{
					if (!Tracker.ShouldDelete(key)) continue;

					// Linux: a process maps files from the dir. Windows: the move fails while
					// any file in the dir is open. Either way the entry is in use.
					if (inUse.Contains(key) || !TryMoveToTrash(dir))
					{
						Tracker.Touch(key);
						_log.Debug("Cache entry {Entry} expired but in use; kept", key);
						continue;
					}

					Tracker.RecordRemoved(key, explicitClear: false);
					removed++;
					_log.Debug("Cache entry expired: {Entry}", key);
				}
			}
		}
		EmptyTrash();
		return removed;
	}

	/// <summary>
	/// Keep-alive for a running script's output dir. False when the dir is not one of
	/// this cache's entries, or no longer exists. Holds the per-dir lock, so it either
	/// lands before the sweep's re-check (and wins) or reports the dir as gone.
	/// </summary>
	public bool TouchCache(string outputDir)
	{
		var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir));
		if (!CacheTracker.PathComparer.Equals(Path.GetDirectoryName(full), _cacheDir))
			return false;
		var key = Path.GetFileName(full);
		lock (_compileLocks.GetOrAdd(Path.Combine(_cacheDir, key), _ => new object()))
			return Tracker.Touch(key);
	}

	private string TrashDir => Path.Combine(_cacheDir, CacheTracker.TrashDirName);

	/// <summary>
	/// Take an entry out of the cache in one step: a rename either moves the whole dir or
	/// nothing, unlike a recursive delete that a locked file stops halfway. On Windows the
	/// rename fails while any file in the dir is open.
	/// </summary>
	private bool TryMoveToTrash(string dir)
	{
		try
		{
			Directory.CreateDirectory(TrashDir);
			Directory.Move(dir, Path.Combine(TrashDir, $"{Path.GetFileName(dir)}.{Guid.NewGuid():N}"));
			return true;
		}
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return false; }
	}

	/// <summary>Delete trashed dirs; anything still locked is retried next sweep.</summary>
	private void EmptyTrash()
	{
		try
		{
			foreach (var dir in Directory.GetDirectories(TrashDir))
			{
				try { Directory.Delete(dir, recursive: true); }
				catch { }
			}
		}
		catch (DirectoryNotFoundException) { }
	}

	private CompileResult CompileInto(
		string scriptPath, string scriptName, ScriptLanguage language,
		IncludeResolver.ResolvedScript resolved, string hash,
		string outputDir, string dllPath, string runtimeConfigPath)
	{
		// Written last: dll + runtimeconfig alone do not make a usable output dir
		// (staging, manifest and apphost follow), e.g. after a crash mid-compile.
		var completePath = Path.Combine(outputDir, $"{scriptName}.complete");
		var dirWasAbsent = !Directory.Exists(outputDir);

		if (File.Exists(completePath)
			&& File.Exists(dllPath) && File.Exists(runtimeConfigPath)
			&& ArtifactWriter.FindDepsJson(outputDir, scriptName) != null
			&& ArtifactWriter.FindManifest(outputDir, scriptName) != null)
		{
			_log.Debug("Cache hit for {ScriptName} ({Hash})", scriptName, hash[..8]);
			Tracker.RecordHit(Path.GetFileName(outputDir), scriptPath);
			var cachedAppHost = AppHostWriter.FindExisting(outputDir, scriptName);
			return new CompileResult
			{
				Success = true,
				Elevate = resolved.Directives.Elevate,
				Watch = resolved.Directives.Watch,
				Restart = resolved.Directives.Restart,
			MutexScope = resolved.Directives.MutexScope,
			MutexAttach = resolved.Directives.MutexAttach,
				AssemblyPath = cachedAppHost ?? dllPath,
				DirectExe = cachedAppHost != null,
				RuntimeConfigPath = runtimeConfigPath,
				DepsJsonPath = ArtifactWriter.FindDepsJson(outputDir, scriptName),
				ManifestPath = ArtifactWriter.FindManifest(outputDir, scriptName),
			};
		}

		// ── Resolve NuGet packages (cache miss only) ────────────────────
		var resolvedPackages = ResolveNuGetPackages(resolved.Directives.NuGets, out var nugetError);
		if (resolvedPackages == null)
			return nugetError!; // error already logged

		// ── Parse syntax trees ──────────────────────────────────────────
		var trees = new List<SyntaxTree>();

		if (language == ScriptLanguage.CSharp)
		{
			var parseOpts = CSharpParseOptions.Default
				.WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest)
				.WithKind(SourceCodeKind.Regular);

			foreach (var (path, source) in resolved.Sources)
			{
				var tree = CSharpSyntaxTree.ParseText(source, parseOpts, path: path);
				var split = SourceSplitter.SplitTopLevelAndTypes(tree, parseOpts, path);
				trees.AddRange(split);
			}

			// Implicit usings + AssemblyDescription
			var consoleAlias = resolved.Directives.Elevate
				&& RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
				? "\n\t\t\t\tglobal using Console = SeaShell.SeaConsole;"
				: "";
			var metaSource = $$"""
				global using System;
				global using System.Collections.Generic;
				global using System.IO;
				global using System.Linq;
				global using System.Net.Http;
				global using System.Threading;
				global using System.Threading.Tasks;
				global using SeaShell;{{consoleAlias}}
				[assembly: System.Reflection.AssemblyDescription(@"{{scriptPath.Replace("\"", "\"\"")}}")]
				// Force Sea initialization — the script assembly is always loaded first,
				// so this module initializer is guaranteed to run before Main.
				static class _SeaShellBoot
				{
					[System.Runtime.CompilerServices.ModuleInitializer]
					internal static void Init() => Sea.EnsureInitialized();
				}
				""";
			trees.Add(CSharpSyntaxTree.ParseText(metaSource, parseOpts, path: "_SeaShellMeta.cs"));
		}
		else // VisualBasic
		{
			var parseOpts = VB.VisualBasicParseOptions.Default
				.WithLanguageVersion(Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Latest);

			foreach (var (path, source) in resolved.Sources)
				trees.Add(VB.VisualBasicSyntaxTree.ParseText(source, parseOpts, path: path));

			// AssemblyDescription for Mother.cs compat + boot initializer
			var escapedPath = scriptPath.Replace("\"", "\"\"");
			var metaSource = $"""
				<Assembly: System.Reflection.AssemblyDescription("{escapedPath}")>
				Friend Module _SeaShellBoot
					<System.Runtime.CompilerServices.ModuleInitializer>
					Friend Sub Init()
						Sea.EnsureInitialized()
					End Sub
				End Module
				""";
			trees.Add(VB.VisualBasicSyntaxTree.ParseText(metaSource, parseOpts, path: "_SeaShellMeta.vb"));
		}

		// ── Collect metadata references ─────────────────────────────────
		var refs = CollectReferences(resolved.Directives, resolvedPackages, _scriptAssemblyPath);

		// ── Compile ─────────────────────────────────────────────────────
		var assemblyName = $"{scriptName}_{hash[..8]}";
		Compilation compilation;

		if (language == ScriptLanguage.CSharp)
		{
			compilation = CSharpCompilation.Create(
				assemblyName: assemblyName,
				syntaxTrees: trees,
				references: refs,
				options: new CSharpCompilationOptions(
					OutputKind.ConsoleApplication,
					allowUnsafe: true,
					nullableContextOptions: NullableContextOptions.Enable,
					optimizationLevel: OptimizationLevel.Debug));
		}
		else
		{
			// VB gets implicit imports via compilation options (no global using equivalent)
			compilation = VB.VisualBasicCompilation.Create(
				assemblyName: assemblyName,
				syntaxTrees: trees,
				references: refs,
				options: new VB.VisualBasicCompilationOptions(
					OutputKind.ConsoleApplication,
					globalImports: new[]
					{
						VB.GlobalImport.Parse("System"),
						VB.GlobalImport.Parse("System.Collections.Generic"),
						VB.GlobalImport.Parse("System.IO"),
						VB.GlobalImport.Parse("System.Linq"),
						VB.GlobalImport.Parse("System.Threading.Tasks"),
						VB.GlobalImport.Parse("Microsoft.VisualBasic"),
						VB.GlobalImport.Parse("SeaShell"),
					},
					optionStrict: VB.OptionStrict.Off,
					optionInfer: true,
					optimizationLevel: OptimizationLevel.Debug));
		}

		_log.Debug("Compiling {AssemblyName} ({Trees} syntax trees, {Refs} references)",
			assemblyName, trees.Count, refs.Count);

		using var ms = new MemoryStream();
		var emitResult = compilation.Emit(ms);

		if (!emitResult.Success)
		{
			var errors = emitResult.Diagnostics
				.Where(d => d.Severity == DiagnosticSeverity.Error)
				.Select(d => d.ToString());
			var errorText = string.Join(Environment.NewLine, errors);
			_log.Warning("Compilation failed for {ScriptName}: {ErrorCount} errors", scriptName,
				emitResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));
			return new CompileResult { Error = errorText };
		}

		_log.Information("Compiled {ScriptName} ({Size} bytes)", scriptName, ms.Length);

		// ── Write artifacts ─────────────────────────────────────────────
		// In watch mode, a previous compilation's output dir may still be in use
		// by the running script process. Skip files that already exist (hash
		// guarantees they're identical) or are locked.
		Directory.CreateDirectory(outputDir);

		WriteIfMissing(dllPath, ms.ToArray());

		if (!File.Exists(runtimeConfigPath))
			ArtifactWriter.WriteRuntimeConfig(runtimeConfigPath, resolved.Directives.WebApp);

		var depsPath = Path.Combine(outputDir, $"{scriptName}.deps.json");
		if (!File.Exists(depsPath))
			DepsJsonWriter.Write(depsPath, assemblyName, resolvedPackages, _engineDir);

		// Everything below is staged flat into one directory, so assets from
		// different sources can claim the same filename. First staged wins.
		var staged = new Dictionary<string, string>(CacheTracker.PathComparer);

		// Copy SeaShell runtime DLLs to output dir (startup hook + transitive deps)
		foreach (var dllName in new[] { "SeaShell.Script.dll", "SeaShell.Common.dll", "MessagePack.dll", "MessagePack.Annotations.dll" })
			StageFlat(Path.Combine(_engineDir, dllName), outputDir, staged);

		// Copy NuGet package DLLs to output dir — makes the output self-contained.
		// No runtime dependency on the NuGet cache (which may be cleared, or belong
		// to a different user account). Like dotnet publish, everything is local.
		foreach (var pkg in resolvedPackages)
		{
			// Managed DLLs — copy flat (bare filename)
			foreach (var asset in pkg.RuntimeAssets)
				StageFlat(asset.FullPath, outputDir, staged);
			// Native DLLs — copy flat like the managed ones. The deps.json declares
			// every library as type:"project", and for project libraries the host
			// resolves assets against the app directory root — a nested
			// runtimes/{rid}/native/ copy never makes it into
			// NATIVE_DLL_SEARCH_DIRECTORIES, so P/Invoke fails (DllNotFoundException)
			// even though the file is on disk.
			foreach (var asset in pkg.NativeAssets)
				StageFlat(asset.FullPath, outputDir, staged);
		}

		// Write manifest — all the metadata the Sea class needs at runtime
		var manifestPath = Path.Combine(outputDir, $"{scriptName}.sea.json");
		ArtifactWriter.WriteManifest(manifestPath, scriptPath, resolved, resolvedPackages);

		// ── Generate apphost (best-effort) ──────────────────────────────
		var appHostPath = AppHostWriter.Generate(
			outputDir, $"{scriptName}.dll", resolved.Directives.Window);

		File.WriteAllBytes(completePath, Array.Empty<byte>());
		Tracker.RecordCreated(Path.GetFileName(outputDir), scriptPath, dirWasAbsent);

		return new CompileResult
		{
			Success = true,
			Elevate = resolved.Directives.Elevate,
			Watch = resolved.Directives.Watch,
			Restart = resolved.Directives.Restart,
			MutexScope = resolved.Directives.MutexScope,
			MutexAttach = resolved.Directives.MutexAttach,
			AssemblyPath = appHostPath ?? dllPath,
			DirectExe = appHostPath != null,
			RuntimeConfigPath = runtimeConfigPath,
			DepsJsonPath = depsPath,
			ManifestPath = manifestPath,
		};
	}

	/// <summary>
	/// Copy <paramref name="source"/> into <paramref name="outputDir"/> under its bare
	/// filename. When an earlier source already claimed that filename the first one
	/// stays; a differing duplicate is logged instead of being dropped silently.
	/// </summary>
	internal static void StageFlat(string source, string outputDir, Dictionary<string, string> staged)
	{
		if (!File.Exists(source)) return;

		var fileName = Path.GetFileName(source);
		if (staged.TryGetValue(fileName, out var winner))
		{
			if (!FilesEqual(winner, source))
				_log.Warning("Staging collision on {FileName}: kept {Winner}, ignored differing {Source}",
					fileName, winner, source);
			return;
		}

		staged[fileName] = source;
		var dest = Path.Combine(outputDir, fileName);
		if (File.Exists(dest)) return;
		try { File.Copy(source, dest); }
		catch (IOException) when (File.Exists(dest)) { } // another process staged it meanwhile
	}

	private static bool FilesEqual(string a, string b)
	{
		if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
		return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
	}

	private static void WriteIfMissing(string path, byte[] data)
	{
		if (File.Exists(path)) return; // hash guarantees identical content
		try { File.WriteAllBytes(path, data); }
		catch (IOException) { } // file may be locked by a running script process
	}

	// ── References ──────────────────────────────────────────────────────

	private static List<MetadataReference> CollectReferences(
		DirectiveParser.DirectiveSet directives,
		List<NuGetResolver.ResolvedPackage> packages,
		string scriptAssemblyPath)
	{
		var refs = new List<MetadataReference>();

		// Use reference assemblies from dotnet/packs/ — these are designed for compilation,
		// contain only managed DLLs, and are what `dotnet build` actually uses.
		var refDir = ArtifactWriter.FindRefAssemblyDir("Microsoft.NETCore.App.Ref");
		if (refDir != null)
		{
			foreach (var dll in Directory.GetFiles(refDir, "*.dll"))
				refs.Add(MetadataReference.CreateFromFile(dll));
		}
		else
		{
			// Fallback: runtime dir, filtering to managed DLLs only
			var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
			foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
			{
				try
				{
					var name = Path.GetFileNameWithoutExtension(dll);
					if (ArtifactWriter.IsKnownNative(name)) continue;
					refs.Add(MetadataReference.CreateFromFile(dll));
				}
				catch { }
			}
		}

		// ASP.NET Core reference assemblies (if //sea_webapp)
		if (directives.WebApp)
		{
			var aspRefDir = ArtifactWriter.FindRefAssemblyDir("Microsoft.AspNetCore.App.Ref");
			if (aspRefDir != null)
			{
				foreach (var dll in Directory.GetFiles(aspRefDir, "*.dll"))
					refs.Add(MetadataReference.CreateFromFile(dll));
			}
		}

		// Explicit //sea_ref paths
		foreach (var refPath in directives.References)
		{
			if (File.Exists(refPath))
				refs.Add(MetadataReference.CreateFromFile(refPath));
		}

		// NuGet packages — load compile DLLs via read-to-bytes (no file locks held)
		var nugetAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var pkg in packages)
		{
			foreach (var dll in pkg.CompileDlls)
			{
				nugetAssemblyNames.Add(Path.GetFileName(dll));
				var bytes = File.ReadAllBytes(dll);
				refs.Add(MetadataReference.CreateFromImage(bytes, filePath: dll));
			}
		}

		// SeaShell runtime libraries — Sea static class + IPC messaging + MessagePack.
		// Skip any that are already provided by the NuGet dependency graph (avoids CS1704
		// when a package transitively depends on SeaShell.Script/Ipc/etc.).
		var engineDir = Path.GetDirectoryName(scriptAssemblyPath) ?? AppContext.BaseDirectory;
		foreach (var name in new[] {
			scriptAssemblyPath,
			Path.Combine(engineDir, "SeaShell.Common.dll"),
			Path.Combine(engineDir, "MessagePack.dll"),
			Path.Combine(engineDir, "MessagePack.Annotations.dll"),
		})
		{
			if (File.Exists(name) && !nugetAssemblyNames.Contains(Path.GetFileName(name)))
			{
				var bytes = File.ReadAllBytes(name);
				refs.Add(MetadataReference.CreateFromImage(bytes, filePath: name));
			}
		}

		return refs;
	}

	/// <summary>Get the source file paths from the last compilation of a script.</summary>
	public string[] GetLastResolvedSources(string scriptPath)
	{
		return _lastSourcesByScript.TryGetValue(scriptPath, out var sources)
			? sources
			: new[] { scriptPath };
	}

	// ── NuGet resolution (extracted for use before cache check) ────────

	private List<NuGetResolver.ResolvedPackage>? ResolveNuGetPackages(
		List<DirectiveParser.NuGetRef> nugets, out CompileResult? error)
	{
		error = null;
		foreach (var nuget in nugets)
		{
			_log.Debug("Resolving NuGet {Package} {Version}", nuget.PackageName, nuget.Version ?? "latest");

			if (NuGetResolver.Resolve(nuget.PackageName, nuget.Version) == null)
			{
				_log.Information("Downloading {Package} (not in cache)", nuget.PackageName);
				var dl = NuGetDownloader.Download(nuget.PackageName, nuget.Version);
				if (!dl.Success)
				{
					_log.Error("Download failed for {Package}: {Error}", nuget.PackageName, dl.Error);
					error = new CompileResult { Error = dl.Error };
					return null;
				}

				NuGetResolver.InvalidateCache();
			}
		}

		var resolvedPackages = NuGetResolver.ResolveWithDependencies(
			nugets.Select(n => (n.PackageName, n.Version)));

		foreach (var nuget in nugets)
		{
			var expectedVersion = NuGetResolver.ResolveDirectVersion(nuget.PackageName, nuget.Version);
			if (!resolvedPackages.Any(p =>
				string.Equals(p.Name, nuget.PackageName, StringComparison.OrdinalIgnoreCase)
				&& (expectedVersion == null
					|| string.Equals(p.Version, expectedVersion, StringComparison.OrdinalIgnoreCase))))
			{
				_log.Error("Package {Package} downloaded but could not be resolved", nuget.PackageName);
				error = new CompileResult
				{
					Error = $"Package '{nuget.PackageName}' was downloaded but could not be resolved."
				};
				return null;
			}
		}

		_log.Debug("Resolved {Count} direct, {Total} total NuGet packages", nugets.Count, resolvedPackages.Count);
		return resolvedPackages;
	}

	private static string? FindUnsupportedNuGetDirective(
		IEnumerable<(string Path, string Source)> sources)
	{
		foreach (var (_, source) in sources)
		{
			foreach (var line in source.AsSpan().EnumerateLines())
			{
				var trimmed = line.Trim();
				if (trimmed.StartsWith("#:package", StringComparison.OrdinalIgnoreCase))
					return "SeaShell does not support '#:package' directives. Use '//sea_nuget <Package> [version]' instead.";
				if (trimmed.StartsWith("#r", StringComparison.OrdinalIgnoreCase)
					&& trimmed.ToString().Contains("nuget:", StringComparison.OrdinalIgnoreCase))
					return "SeaShell does not support '#r \"nuget: ...\"' directives. Use '//sea_nuget <Package> [version]' instead.";
			}
		}
		return null;
	}

	// ── Binary pass-through ────────────────────────────────────────────

	private CompileResult CompileBinary(string binaryPath)
	{
		var name = Path.GetFileNameWithoutExtension(binaryPath);
		var binDir = Path.GetDirectoryName(binaryPath)!;
		var fileExt = Path.GetExtension(binaryPath).ToLowerInvariant();
		var isExeOrBare = fileExt is ".exe" or ""; // .exe on Windows, extensionless on Linux
		var directExe = false;

		// .exe or extensionless → check for companion .dll (Tier 1 apphost)
		if (isExeOrBare)
		{
			var dllPath = Path.Combine(binDir, name + ".dll");
			if (File.Exists(dllPath))
			{
				binaryPath = dllPath; // Tier 1: redirect to managed .dll
			}
			else
			{
				directExe = true; // Tier 2/3: single-file or Linux ELF, run directly
			}
		}

		// Inspect PE metadata (skip for Tier 2/3 — metadata is bundled, unreadable)
		AssemblyInspector.AssemblyInfo? info = null;
		if (!directExe)
		{
			info = AssemblyInspector.Inspect(binaryPath);
			if (!info.IsManaged)
				return new CompileResult { Error = $"Not a managed .NET assembly: {binaryPath}" };
		}

		// Read companion .sea.json for directives
		var seaJsonPath = Path.Combine(binDir, name + ".sea.json");
		var (watch, elevate, restart, mutexScope, mutexAttach) = ReadCompanionSeaJson(seaJsonPath);

		// Compute cache hash
		var hash = CompilationCache.ComputeBinaryHash(binaryPath);
		var outputDir = Path.Combine(_cacheDir, $"{name}_{hash[..8]}");
		// Staging writes into the dir that the sweep and explicit clears delete: take the
		// same per-dir lock (see Compile).
		lock (_compileLocks.GetOrAdd(outputDir, _ => new object()))
		{
			var staged = Directory.Exists(outputDir);
			Directory.CreateDirectory(outputDir);
			void Track()
			{
				if (staged) Tracker.RecordHit(Path.GetFileName(outputDir), binaryPath);
				else Tracker.RecordCreated(Path.GetFileName(outputDir), binaryPath, dirWasAbsent: true);
			}

			_log.Debug("Binary staging for {Name} in {OutputDir}", name, outputDir);

			// Copy SeaShell runtime DLLs to staging
			foreach (var dll in new[] { "SeaShell.Script.dll", "SeaShell.Common.dll",
			                             "MessagePack.dll", "MessagePack.Annotations.dll" })
			{
				var src = Path.Combine(_engineDir, dll);
				var dest = Path.Combine(outputDir, dll);
				if (File.Exists(src)) WriteIfMissing(dest, File.ReadAllBytes(src));
			}

			// Manifest
			var manifestPath = Path.Combine(outputDir, $"{name}.sea.json");
			ArtifactWriter.WriteBinaryManifest(manifestPath, binaryPath);

			// Track for watch mode
			var watchFiles = new List<string> { binaryPath };
			if (File.Exists(seaJsonPath)) watchFiles.Add(seaJsonPath);

			// StartupHookPath: set when a .dll binary doesn't reference SeaShell.Script.
			// For directExe (Tier 2/3 single-file), we don't set startup hooks because:
			// - Tier 3 (self-contained): bundled runtime can't resolve hook deps from staging
			// - If the binary bundles SeaShell.Script, it already has the module initializer
			// - If it doesn't, graceful degradation (pipe connect timeout, runs without Sea context)
			string? startupHookPath = (!directExe && info is { HasSeaShellRef: false })
				? Path.Combine(outputDir, "SeaShell.Script.dll")
				: null;

			if (directExe)
			{
				// Tier 2/3: run .exe directly
				_lastSourcesByScript[binaryPath] = watchFiles.ToArray();
				Track();

				return new CompileResult
				{
					Success = true,
					Watch = watch,
					Elevate = elevate,
					Restart = restart,
				MutexScope = mutexScope,
				MutexAttach = mutexAttach,
					AssemblyPath = binaryPath,
					ManifestPath = manifestPath,
					StartupHookPath = startupHookPath,
					DirectExe = true,
				};
			}

			// Tier 1 / .dll: stage for dotnet exec
			var stagedDll = Path.Combine(outputDir, $"{name}.dll");
			WriteIfMissing(stagedDll, File.ReadAllBytes(binaryPath));

			var stagedRtc = Path.Combine(outputDir, $"{name}.runtimeconfig.json");
			var companionRtc = Path.Combine(binDir, $"{name}.runtimeconfig.json");
			if (File.Exists(companionRtc))
				ArtifactWriter.MergeRuntimeConfig(companionRtc, stagedRtc, _engineDir);
			else
				ArtifactWriter.WriteRuntimeConfig(stagedRtc, info!.HasAspNetRef);

			var stagedDeps = Path.Combine(outputDir, $"{name}.deps.json");
			var companionDeps = Path.Combine(binDir, $"{name}.deps.json");
			if (File.Exists(companionDeps))
				DepsJsonWriter.Merge(companionDeps, stagedDeps, _engineDir);
			else
				DepsJsonWriter.Write(stagedDeps, name, new List<NuGetResolver.ResolvedPackage>(), _engineDir);

			if (File.Exists(companionRtc)) watchFiles.Add(companionRtc);
			if (File.Exists(companionDeps)) watchFiles.Add(companionDeps);
			_lastSourcesByScript[binaryPath] = watchFiles.ToArray();
			Track();

			return new CompileResult
			{
				Success = true,
				Watch = watch,
				Elevate = elevate,
				Restart = restart,
				MutexScope = mutexScope,
				MutexAttach = mutexAttach,
				AssemblyPath = stagedDll,
				RuntimeConfigPath = stagedRtc,
				DepsJsonPath = stagedDeps,
				ManifestPath = manifestPath,
				StartupHookPath = startupHookPath,
			};
		}
	}

	private static (bool watch, bool elevate, bool restart, byte mutexScope, bool mutexAttach) ReadCompanionSeaJson(string path)
	{
		if (!File.Exists(path)) return (false, false, false, 0, false);
		try
		{
			var json = File.ReadAllText(path);
			var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
			var data = System.Text.Json.JsonSerializer.Deserialize<CompanionSeaJson>(json, opts);
			return (data?.Watch ?? false, data?.Elevate ?? false, data?.Restart ?? false,
				data?.MutexScope ?? 0, data?.MutexAttach ?? false);
		}
		catch { return (false, false, false, 0, false); }
	}

	private sealed class CompanionSeaJson
	{
		public bool Watch { get; set; }
		public bool Elevate { get; set; }
		public bool Restart { get; set; }
		public byte MutexScope { get; set; }
		public bool MutexAttach { get; set; }
	}
}
