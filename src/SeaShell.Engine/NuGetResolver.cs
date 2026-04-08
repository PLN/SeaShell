using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Serilog;

namespace SeaShell.Engine;

/// <summary>
/// Resolves NuGet packages from the global cache (~/.nuget/packages/).
/// Handles runtime-specific managed DLLs and native DLLs — the key improvement
/// over CS-Script's broken probing that NugetRuntime.cs works around.
/// </summary>
public sealed class NuGetResolver
{
	private static readonly ILogger _log = Log.ForContext<NuGetResolver>();

	private static readonly string[] TfmProbeOrder =
	{
		"net10.0", "net9.0", "net8.0", "net7.0", "net6.0",
		"netstandard2.1", "netstandard2.0",
		"netcoreapp3.1",
	};

	private readonly string _cacheRoot;
	private readonly string _rid;

	// Per-package resolution cache: avoids re-reading .nuspec and re-probing TFM dirs
	private readonly Dictionary<string, CachedEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

	private sealed record CachedEntry(
		ResolvedPackage Package,
		List<NuspecDep> Dependencies,
		DateTime VersionDirTimestamp);

	public NuGetResolver()
	{
		_cacheRoot = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".nuget", "packages");

		_rid = GetRuntimeIdentifier();
	}

	/// <summary>Invalidate all cached entries. Called after downloading new packages.</summary>
	public void InvalidateCache() => _cache.Clear();

	/// <summary>Invalidate a specific package's cached entry.</summary>
	public void InvalidateCache(string packageName) => _cache.Remove(packageName);

	public sealed record ResolvedPackage
	{
		public required string Name { get; init; }
		public required string Version { get; init; }
		public required string PackagePath { get; init; }  // relative path in cache (e.g., "serilog/4.3.1")

		/// <summary>Managed DLLs for compilation (MetadataReference).</summary>
		public required List<string> CompileDlls { get; init; }

		/// <summary>Managed DLLs for runtime — may differ from compile (runtime-specific).</summary>
		public required List<ResolvedAsset> RuntimeAssets { get; init; }

		/// <summary>Native DLLs for runtime.</summary>
		public required List<ResolvedAsset> NativeAssets { get; init; }
	}

	public sealed record ResolvedAsset
	{
		/// <summary>Absolute path to the DLL.</summary>
		public required string FullPath { get; init; }

		/// <summary>Path relative to the package root (for .deps.json).</summary>
		public required string RelativePath { get; init; }
	}

	/// <summary>
	/// Resolve a package and all its transitive dependencies via .nuspec parsing.
	/// Returns all packages in dependency order (deps first).
	/// Skips packages already provided by the .NET runtime framework.
	/// </summary>
	public List<ResolvedPackage> ResolveWithDependencies(string packageName, string? version)
	{
		var resolved = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
		var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // cycle detection

		ResolveRecursive(packageName, version, resolved, visiting);

		_log.Debug("Resolved {Package}: {Count} packages in dependency tree", packageName, resolved.Count);
		return resolved.Values.ToList();
	}

	private void ResolveRecursive(
		string packageName,
		string? version,
		Dictionary<string, ResolvedPackage> resolved,
		HashSet<string> visiting)
	{
		// Already in the output set for this resolution pass
		if (resolved.ContainsKey(packageName)) return;

		// Skip packages provided by the .NET runtime framework
		if (IsFrameworkProvided(packageName)) return;

		// Cycle detection
		if (!visiting.Add(packageName)) return;

		// Check per-package cache — reuse if the package dir hasn't changed
		ResolvedPackage? pkg;
		List<NuspecDep> deps;

		var cacheKey = $"{packageName.ToLowerInvariant()}:{version ?? "latest"}";
		if (_cache.TryGetValue(cacheKey, out var cached) && IsCacheValid(cached))
		{
			pkg = cached.Package;
			deps = cached.Dependencies;
		}
		else
		{
			pkg = Resolve(packageName, version);
			if (pkg == null)
			{
				visiting.Remove(packageName);
				return;
			}
			deps = ParseNuspecDependencies(pkg);

			// Store in cache with the version dir's timestamp
			var versionDir = Path.Combine(_cacheRoot, pkg.PackagePath);
			var timestamp = Directory.Exists(versionDir)
				? Directory.GetLastWriteTimeUtc(versionDir)
				: DateTime.MinValue;
			_cache[cacheKey] = new CachedEntry(pkg, deps, timestamp);
		}

		// Resolve transitive deps first (depth-first)
		foreach (var dep in deps)
		{
			ResolveRecursive(dep.Id, dep.MinVersion, resolved, visiting);
		}

		resolved[packageName] = pkg;
		visiting.Remove(packageName);
	}

	private bool IsCacheValid(CachedEntry cached)
	{
		var versionDir = Path.Combine(_cacheRoot, cached.Package.PackagePath);
		if (!Directory.Exists(versionDir)) return false;
		return Directory.GetLastWriteTimeUtc(versionDir) == cached.VersionDirTimestamp;
	}

	/// <summary>
	/// Check if a package is already provided by the .NET runtime and doesn't need
	/// to be resolved from the NuGet cache. These ship in-box.
	/// </summary>
	private static bool IsFrameworkProvided(string packageName)
	{
		// Packages that ship as part of the .NET runtime shared framework.
		// These exist in the NuGet cache but shouldn't be loaded from there —
		// the runtime's own copies take precedence and are newer.
		var lower = packageName.ToLowerInvariant();

		// Core runtime packages
		if (lower is "microsoft.netcore.app.ref" or "microsoft.netcore.app.runtime"
			or "microsoft.aspnetcore.app.ref") return true;

		// System.* packages that are part of the runtime (but NOT all System.* packages)
		// These specific ones are commonly pulled as transitive deps but already in-box.
		return lower is
			"system.buffers" or
			"system.memory" or
			"system.numerics.vectors" or
			"system.runtime.compilerservices.unsafe" or
			"system.threading.tasks.extensions" or
			"system.text.encodings.web" or
			"system.text.json" or
			"system.io.pipelines" or
			"system.diagnostics.diagnosticsource" or
			"microsoft.extensions.logging.abstractions" or
			"microsoft.extensions.logging" or
			"microsoft.extensions.dependencyinjection.abstractions" or
			"microsoft.extensions.dependencyinjection" or
			"microsoft.extensions.options" or
			"microsoft.extensions.primitives";
	}

	private sealed record NuspecDep(string Id, string? MinVersion);

	/// <summary>
	/// Parse the .nuspec file for a resolved package to get its dependencies
	/// for the best matching TFM.
	/// </summary>
	private List<NuspecDep> ParseNuspecDependencies(ResolvedPackage pkg)
	{
		var versionDir = Path.Combine(_cacheRoot, pkg.PackagePath);
		var nuspecFiles = Directory.GetFiles(versionDir, "*.nuspec");
		if (nuspecFiles.Length == 0) return new List<NuspecDep>();

		try
		{
			var doc = XDocument.Load(nuspecFiles[0]);
			var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
			var deps = doc.Descendants(ns + "dependencies").FirstOrDefault();
			if (deps == null) return new List<NuspecDep>();

			// Try to find a TFM-specific group
			var groups = deps.Elements(ns + "group").ToList();
			if (groups.Count > 0)
			{
				var bestGroup = PickBestTfmGroup(groups, ns);
				if (bestGroup != null)
					return ParseDependencyElements(bestGroup, ns);
			}

			// Fallback: ungrouped dependencies (no TFM targeting)
			return ParseDependencyElements(deps, ns);
		}
		catch
		{
			return new List<NuspecDep>();
		}
	}

	private static List<NuspecDep> ParseDependencyElements(XElement container, XNamespace ns)
	{
		return container.Elements(ns + "dependency")
			.Select(el => new NuspecDep(
				el.Attribute("id")?.Value ?? "",
				ExtractMinVersion(el.Attribute("version")?.Value)))
			.Where(d => !string.IsNullOrEmpty(d.Id))
			.ToList();
	}

	/// <summary>
	/// Extract a usable minimum version from a NuGet version range.
	/// "[1.0.0, )" → "1.0.0", "1.0.0" → "1.0.0", etc.
	/// </summary>
	private static string? ExtractMinVersion(string? versionRange)
	{
		if (string.IsNullOrEmpty(versionRange)) return null;
		// Strip range brackets: [, (, ), ]
		var clean = versionRange.TrimStart('[', '(').Split(',')[0].Trim().TrimEnd(']', ')');
		return string.IsNullOrEmpty(clean) ? null : clean;
	}

	/// <summary>
	/// Pick the best matching dependency group from .nuspec TFM groups.
	/// Same probe order as DLL resolution.
	/// </summary>
	private static XElement? PickBestTfmGroup(List<XElement> groups, XNamespace ns)
	{
		foreach (var tfm in TfmProbeOrder)
		{
			foreach (var group in groups)
			{
				var target = group.Attribute("targetFramework")?.Value;
				if (target == null) continue;

				// Normalize: ".NETStandard2.0" → "netstandard2.0", "net8.0" stays "net8.0"
				var normalized = NormalizeTfm(target);
				if (normalized == tfm) return group;
			}
		}

		// Fallback: group without targetFramework, or first group
		return groups.FirstOrDefault(g => g.Attribute("targetFramework") == null)
			?? groups.FirstOrDefault();
	}

	private static string NormalizeTfm(string tfm)
	{
		// Handle long-form names from nuspec
		if (tfm.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase))
		{
			var ver = tfm.Replace(".NETStandard", "").TrimStart(',', ' ', 'v', 'V', '=');
			return $"netstandard{ver}";
		}
		if (tfm.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
		{
			var ver = tfm.Replace(".NETCoreApp", "").TrimStart(',', ' ', 'v', 'V', '=');
			return $"netcoreapp{ver}";
		}
		if (tfm.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase))
		{
			return ""; // Skip .NET Framework groups
		}
		return tfm; // Already short-form like "net8.0"
	}

	public ResolvedPackage? Resolve(string packageName, string? version)
	{
		var packageDir = Path.Combine(_cacheRoot, packageName.ToLowerInvariant());
		if (!Directory.Exists(packageDir))
			return null;

		// Pick version
		var versionDir = version != null
			? Path.Combine(packageDir, version)
			: PickLatestVersion(packageDir);

		if (versionDir == null || !Directory.Exists(versionDir))
			return null;

		var actualVersion = Path.GetFileName(versionDir);
		var packagePath = $"{packageName.ToLowerInvariant()}/{actualVersion}";

		// ── Resolve managed DLLs ────────────────────────────────────────

		// 1. Try runtime-specific: runtimes/{rid}/lib/{tfm}/
		var runtimeManaged = ProbeRuntimeSpecific(versionDir, "lib");

		// 2. Fallback to generic: lib/{tfm}/
		var genericManaged = ProbeGenericLib(versionDir);

		// For compilation: prefer generic (broader API surface), fall back to runtime-specific
		var compileDlls = genericManaged.Count > 0
			? genericManaged.Select(a => a.FullPath).ToList()
			: runtimeManaged.Select(a => a.FullPath).ToList();

		// For runtime: prefer runtime-specific (correct platform implementation)
		var runtimeAssets = runtimeManaged.Count > 0
			? runtimeManaged
			: genericManaged;

		// ── Resolve native DLLs ─────────────────────────────────────────
		var nativeAssets = ProbeNative(versionDir);

		return new ResolvedPackage
		{
			Name = packageName,
			Version = actualVersion,
			PackagePath = packagePath,
			CompileDlls = compileDlls,
			RuntimeAssets = runtimeAssets,
			NativeAssets = nativeAssets,
		};
	}

	/// <summary>Probe runtimes/{rid}/lib/{tfm}/ for managed DLLs.</summary>
	private List<ResolvedAsset> ProbeRuntimeSpecific(string versionDir, string subDir)
	{
		// Try exact RID first, then fall back to broader RIDs
		foreach (var rid in GetRidFallbacks())
		{
			foreach (var tfm in TfmProbeOrder)
			{
				var dir = Path.Combine(versionDir, "runtimes", rid, subDir, tfm);
				if (!Directory.Exists(dir)) continue;

				var dlls = Directory.GetFiles(dir, "*.dll")
					.Where(f => !IsResourceDll(f))
					.Select(f => new ResolvedAsset
					{
						FullPath = f,
						RelativePath = Path.GetRelativePath(versionDir, f).Replace('\\', '/'),
					})
					.ToList();

				if (dlls.Count > 0) return dlls;
			}
		}

		return new List<ResolvedAsset>();
	}

	/// <summary>Probe lib/{tfm}/ for generic managed DLLs.</summary>
	private List<ResolvedAsset> ProbeGenericLib(string versionDir)
	{
		foreach (var tfm in TfmProbeOrder)
		{
			var dir = Path.Combine(versionDir, "lib", tfm);
			if (!Directory.Exists(dir)) continue;

			var dlls = Directory.GetFiles(dir, "*.dll")
				.Where(f => !IsResourceDll(f))
				.Select(f => new ResolvedAsset
				{
					FullPath = f,
					RelativePath = Path.GetRelativePath(versionDir, f).Replace('\\', '/'),
				})
				.ToList();

			if (dlls.Count > 0) return dlls;
		}

		return new List<ResolvedAsset>();
	}

	/// <summary>Probe runtimes/{rid}/native/ for native DLLs.</summary>
	private List<ResolvedAsset> ProbeNative(string versionDir)
	{
		foreach (var rid in GetRidFallbacks())
		{
			var dir = Path.Combine(versionDir, "runtimes", rid, "native");
			if (!Directory.Exists(dir)) continue;

			var dlls = Directory.GetFiles(dir)
				.Where(f =>
				{
					var ext = Path.GetExtension(f).ToLowerInvariant();
					return ext is ".dll" or ".so" or ".dylib";
				})
				.Select(f => new ResolvedAsset
				{
					FullPath = f,
					RelativePath = Path.GetRelativePath(versionDir, f).Replace('\\', '/'),
				})
				.ToList();

			if (dlls.Count > 0) return dlls;
		}

		return new List<ResolvedAsset>();
	}

	/// <summary>
	/// Resolve the concrete version for a direct NuGet reference.
	/// For explicit versions, returns as-is. For versionless, picks the latest
	/// from the NuGet cache via directory listing. Lightweight — no .nuspec
	/// parsing, no transitive resolution.
	/// </summary>
	public string? ResolveDirectVersion(string packageName, string? explicitVersion)
	{
		if (explicitVersion != null) return explicitVersion;
		var packageDir = Path.Combine(_cacheRoot, packageName.ToLowerInvariant());
		if (!Directory.Exists(packageDir)) return null;
		var latest = PickLatestVersion(packageDir);
		return latest != null ? Path.GetFileName(latest) : null;
	}

	private string? PickLatestVersion(string packageDir)
	{
		return Directory.GetDirectories(packageDir)
			.Where(d => Version.TryParse(Path.GetFileName(d), out _))
			.OrderByDescending(d => Version.Parse(Path.GetFileName(d)!))
			.FirstOrDefault();
	}

	/// <summary>RID fallback chain, e.g., linux-musl-x64 → linux-x64 → linux → any.</summary>
	private IEnumerable<string> GetRidFallbacks()
	{
		yield return _rid;                                         // e.g., linux-musl-x64
		if (_rid.Contains("-musl-"))
		{
			yield return _rid.Replace("-musl", "");                // e.g., linux-x64
		}
		var dashIdx = _rid.IndexOf('-');
		if (dashIdx > 0) yield return _rid[..dashIdx];             // e.g., linux
		yield return "any";
	}

	private static string GetRuntimeIdentifier()
	{
		var arch = RuntimeInformation.OSArchitecture switch
		{
			Architecture.X64 => "x64",
			Architecture.X86 => "x86",
			Architecture.Arm64 => "arm64",
			Architecture.Arm => "arm",
			_ => "x64",
		};

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			if (RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
				|| File.Exists("/etc/alpine-release"))
				return $"linux-musl-{arch}";
			return $"linux-{arch}";
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
		return $"linux-{arch}";
	}

	/// <summary>Skip satellite resource DLLs in culture subdirectories.</summary>
	private static bool IsResourceDll(string path)
	{
		var dir = Path.GetDirectoryName(path);
		var dirName = Path.GetFileName(dir);
		// Resource DLLs sit in culture-name dirs like "en", "de", "zh-Hans"
		return dirName != null && dirName.Length <= 10 && dirName.Contains('-') == false
			? false  // short dir name without dash — could be a TFM, not a culture
			: Path.GetFileName(path).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase);
	}
}
