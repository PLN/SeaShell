using System;
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

	public readonly NuGetResolver NuGetResolver;

	// Track last resolved source list per script (for watch mode file list)
	private readonly Dictionary<string, string[]> _lastSourcesByScript = new(StringComparer.OrdinalIgnoreCase);

	public ScriptCompiler()
	{
		// Secure the temp root on Linux — owner-only access prevents other local
		// users from reading compiled scripts or poisoning the cache.
		var tempRoot = Path.Combine(Path.GetTempPath(), "seashell");
		Directory.CreateDirectory(tempRoot);
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			try
			{
				File.SetUnixFileMode(tempRoot,
					UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
			catch { }
		}

		_cacheDir = Path.Combine(tempRoot, "cache");
		Directory.CreateDirectory(_cacheDir);

		// Locate SeaShell.Script.dll — shipped alongside the daemon
		_scriptAssemblyPath = Path.Combine(AppContext.BaseDirectory, "SeaShell.Script.dll");
		_includeResolver = new IncludeResolver();
		NuGetResolver = new NuGetResolver();
	}

	public sealed record CompileResult
	{
		public bool Success { get; init; }
		public bool Elevate { get; init; }
		public bool Watch { get; init; }
		public string? AssemblyPath { get; init; }
		public string? RuntimeConfigPath { get; init; }
		public string? DepsJsonPath { get; init; }
		public string? ManifestPath { get; init; }
		public string? Error { get; init; }
	}

	public CompileResult Compile(string scriptPath, string[] args)
	{
		scriptPath = Path.GetFullPath(scriptPath);
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

		// ── Resolve NuGet packages (before cache check so versions are in the hash) ──
		var resolvedPackages = ResolveNuGetPackages(resolved.Directives.NuGets);
		if (resolvedPackages == null)
			return _lastNuGetError!; // error already logged

		// ── Check cache (includes source hashes + NuGet versions + engine fingerprint) ──
		var hash = CompilationCache.ComputeHash(resolved.Sources, resolvedPackages);
		var outputDir = Path.Combine(_cacheDir, $"{scriptName}_{hash[..8]}");
		var dllPath = Path.Combine(outputDir, $"{scriptName}.dll");
		var runtimeConfigPath = Path.Combine(outputDir, $"{scriptName}.runtimeconfig.json");

		if (File.Exists(dllPath) && File.Exists(runtimeConfigPath))
		{
			_log.Debug("Cache hit for {ScriptName} ({Hash})", scriptName, hash[..8]);
			return new CompileResult
			{
				Success = true,
				Elevate = resolved.Directives.Elevate,
				Watch = resolved.Directives.Watch,
				AssemblyPath = dllPath,
				RuntimeConfigPath = runtimeConfigPath,
				DepsJsonPath = ArtifactWriter.FindDepsJson(outputDir, scriptName),
				ManifestPath = ArtifactWriter.FindManifest(outputDir, scriptName),
			};
		}

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
			var metaSource = $$"""
				global using System;
				global using System.Collections.Generic;
				global using System.IO;
				global using System.Linq;
				global using System.Net.Http;
				global using System.Threading;
				global using System.Threading.Tasks;
				global using SeaShell;
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
			DepsJsonWriter.Write(depsPath, assemblyName, resolvedPackages);

		// Copy SeaShell runtime DLLs to output dir (for runtime loading)
		foreach (var dllName in new[] { "SeaShell.Script.dll", "SeaShell.Ipc.dll", "MessagePack.dll", "MessagePack.Annotations.dll" })
		{
			var src = Path.Combine(AppContext.BaseDirectory, dllName);
			var dest = Path.Combine(outputDir, dllName);
			if (File.Exists(src) && !File.Exists(dest))
				File.Copy(src, dest);
		}

		// Write manifest — all the metadata the Sea class needs at runtime
		var manifestPath = Path.Combine(outputDir, $"{scriptName}.sea.json");
		ArtifactWriter.WriteManifest(manifestPath, scriptPath, resolved, resolvedPackages);

		return new CompileResult
		{
			Success = true,
			Elevate = resolved.Directives.Elevate,
			Watch = resolved.Directives.Watch,
			AssemblyPath = dllPath,
			RuntimeConfigPath = runtimeConfigPath,
			DepsJsonPath = depsPath,
			ManifestPath = manifestPath,
		};
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
		foreach (var pkg in packages)
		{
			foreach (var dll in pkg.CompileDlls)
			{
				var bytes = File.ReadAllBytes(dll);
				refs.Add(MetadataReference.CreateFromImage(bytes, filePath: dll));
			}
		}

		// SeaShell runtime libraries — Sea static class + IPC messaging + MessagePack
		foreach (var name in new[] {
			scriptAssemblyPath,
			Path.Combine(AppContext.BaseDirectory, "SeaShell.Ipc.dll"),
			Path.Combine(AppContext.BaseDirectory, "MessagePack.dll"),
			Path.Combine(AppContext.BaseDirectory, "MessagePack.Annotations.dll"),
		})
		{
			if (File.Exists(name))
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

	private CompileResult? _lastNuGetError;

	private List<NuGetResolver.ResolvedPackage>? ResolveNuGetPackages(
		List<DirectiveParser.NuGetRef> nugets)
	{
		var resolvedPackages = new List<NuGetResolver.ResolvedPackage>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var nuget in nugets)
		{
			_log.Debug("Resolving NuGet {Package} {Version}", nuget.PackageName, nuget.Version ?? "latest");
			var tree = NuGetResolver.ResolveWithDependencies(nuget.PackageName, nuget.Version);

			if (tree.Count == 0 && NuGetResolver.Resolve(nuget.PackageName, nuget.Version) == null)
			{
				_log.Information("Downloading {Package} (not in cache)", nuget.PackageName);
				var dl = NuGetDownloader.Download(nuget.PackageName, nuget.Version);
				if (!dl.Success)
				{
					_log.Error("Download failed for {Package}: {Error}", nuget.PackageName, dl.Error);
					_lastNuGetError = new CompileResult { Error = dl.Error };
					return null;
				}

				NuGetResolver.InvalidateCache();
				tree = NuGetResolver.ResolveWithDependencies(nuget.PackageName, nuget.Version);

				if (tree.Count == 0)
				{
					_log.Error("Package {Package} downloaded but could not be resolved", nuget.PackageName);
					_lastNuGetError = new CompileResult
					{
						Error = $"Package '{nuget.PackageName}' was downloaded but could not be resolved."
					};
					return null;
				}
			}

			foreach (var pkg in tree)
			{
				if (seen.Add(pkg.Name))
					resolvedPackages.Add(pkg);
			}
		}

		_log.Debug("Resolved {Count} direct, {Total} total NuGet packages", nugets.Count, resolvedPackages.Count);
		return resolvedPackages;
	}
}
