using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeaShell.Invoker;

/// <summary>
/// Model and I/O for seashell.json — the local installation state manifest.
/// Keyed by 3-part Invoker assembly version for side-by-side support.
/// Lives in <see cref="SeaShellPaths.DataDir"/>.
/// </summary>
public static class ServiceManifest
{
	private static readonly string ManifestPath = Path.Combine(SeaShellPaths.DataDir, "seashell.json");
	private static readonly string LockPath = ManifestPath + ".lock";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	/// <summary>
	/// Acquire a file lock for exclusive manifest access. Dispose the returned
	/// stream when done. Uses a sidecar .lock file so the JSON file itself
	/// can be freely read/written within the lock scope.
	/// </summary>
	private static FileStream AcquireLock()
	{
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ManifestPath)!);
		return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
	}

	// ── Model ──────────────────────────────────────────────────────────

	public sealed class Manifest
	{
		public Dictionary<string, InstallationEntry> Installations { get; set; } = new();
	}

	public sealed class InstallationEntry
	{
		public string? InstalledAt { get; set; }
		public string? Rid { get; set; }
		public string? ServicePackage { get; set; }
		public ComponentEntry? Daemon { get; set; }
		public ComponentEntry? Elevator { get; set; }
	}

	public sealed class ComponentEntry
	{
		public string? Hash { get; set; }
		public string? Path { get; set; }
		public string? Address { get; set; }
		public string? ScheduledTask { get; set; }
		public string? SystemdUnit { get; set; }
		public string? RunitService { get; set; }
		public string? OpenrcService { get; set; }
	}

	// ── Read / Write ───────────────────────────────────────────────────

	public static Manifest Read()
	{
		try
		{
			if (!File.Exists(ManifestPath))
				return new Manifest();

			var json = File.ReadAllText(ManifestPath);
			return JsonSerializer.Deserialize<Manifest>(json, JsonOptions) ?? new Manifest();
		}
		catch (Exception ex) when (ex is JsonException or FileNotFoundException or DirectoryNotFoundException)
		{
			return new Manifest();
		}
	}

	public static void Write(Manifest manifest)
	{
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ManifestPath)!);
		var json = JsonSerializer.Serialize(manifest, JsonOptions);
		File.WriteAllText(ManifestPath, json);
	}

	// ── Lookup ─────────────────────────────────────────────────────────

	/// <summary>
	/// Get the installation entry for the given version.
	/// Returns null if not staged yet.
	/// </summary>
	public static InstallationEntry? GetInstallation(string version)
	{
		var manifest = Read();
		return manifest.Installations.TryGetValue(version, out var entry) ? entry : null;
	}

	// ── Staging ────────────────────────────────────────────────────────

	/// <summary>
	/// Get or stage the daemon binary for the given version.
	/// Returns the staged directory path, or null if the service package is not available.
	/// </summary>
	public static string? GetOrStageDaemon(string version, Action<string>? log = null)
	{
		return GetOrStageComponent(version, "daemon", "seashell-daemon", log);
	}

	/// <summary>
	/// Get or stage the elevator binary for the given version.
	/// Returns the staged directory path, or null if the service package is not available.
	/// </summary>
	public static string? GetOrStageElevator(string version, Action<string>? log = null)
	{
		return GetOrStageComponent(version, "elevator", "seashell-elevator", log);
	}

	private static string? GetOrStageComponent(
		string version, string componentName, string assemblyName,
		Action<string>? log)
	{
		using var _ = AcquireLock();

		var manifest = Read();

		// Check existing entry
		if (manifest.Installations.TryGetValue(version, out var entry))
		{
			var existing = componentName == "daemon" ? entry.Daemon : entry.Elevator;
			if (existing?.Path != null && Directory.Exists(existing.Path))
			{
				log?.Invoke($"{componentName}: using staged {existing.Path}");
				return existing.Path;
			}
		}

		// Need to stage — find source
		var (sourceDir, sharedDir, servicePackagePath) = FindComponentSource(componentName, assemblyName);
		if (sourceDir == null)
		{
			log?.Invoke($"{componentName}: source not found");
			return null;
		}

		// Stage binary (platform-specific)
		var (stagedDir, hash) = DaemonManager.StageBinary(sourceDir, componentName);

		// On Linux, the apphost extracted from a nupkg loses its execute bit.
		// Set it explicitly so the daemon/elevator can be started.
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			var apphost = System.IO.Path.Combine(stagedDir, assemblyName);
			if (File.Exists(apphost))
			{
				try
				{
					File.SetUnixFileMode(apphost,
						File.GetUnixFileMode(apphost) | UnixFileMode.UserExecute);
				}
				catch { }
			}
		}

		// Also stage shared DLLs from runtimes/any/ (Service package layout)
		if (sharedDir != null && Directory.Exists(sharedDir))
		{
			foreach (var file in Directory.GetFiles(sharedDir))
			{
				var dest = System.IO.Path.Combine(stagedDir, System.IO.Path.GetFileName(file));
				if (!File.Exists(dest))
					try { File.Copy(file, dest); } catch { }
			}
		}

		log?.Invoke($"{componentName}: staged to {stagedDir}");

		// Update manifest (lock held — no concurrent write possible)
		manifest = Read();
		if (!manifest.Installations.TryGetValue(version, out entry))
		{
			entry = new InstallationEntry
			{
				InstalledAt = DateTime.Now.ToString("yyyy.MMdd.HHmm"),
				Rid = CurrentRid,
				ServicePackage = servicePackagePath,
			};
			manifest.Installations[version] = entry;
		}

		var component = new ComponentEntry
		{
			Hash = hash,
			Path = stagedDir,
			Address = componentName == "daemon"
				? Protocol.TransportEndpoint.GetDaemonAddress(
					Protocol.TransportEndpoint.CurrentUserIdentity, version)
				: null,
		};

		if (componentName == "daemon")
			entry.Daemon = component;
		else
			entry.Elevator = component;

		Write(manifest);
		return stagedDir;
	}

	/// <summary>
	/// Update a component entry in the manifest (e.g. to record a scheduled task name).
	/// </summary>
	public static void UpdateComponent(string version, string componentName, Action<ComponentEntry> update)
	{
		using var _ = AcquireLock();

		var manifest = Read();
		if (!manifest.Installations.TryGetValue(version, out var entry))
			return;

		var component = componentName == "daemon" ? entry.Daemon : entry.Elevator;
		if (component == null)
			return;

		update(component);
		Write(manifest);
	}

	// ── Source discovery ───────────────────────────────────────────────

	private static string CurrentRid =>
		DaemonManager.IsMuslRuntime ? "linux-musl-x64"
		: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64"
		: "linux-x64";

	/// <summary>
	/// Find the source directory for a component binary.
	/// Returns (ridDir, sharedDir, servicePackagePath). sharedDir is non-null
	/// when source is a NuGet Service package (shared DLLs in runtimes/any/).
	/// </summary>
	private static (string? sourceDir, string? sharedDir, string? servicePackagePath) FindComponentSource(
		string componentName, string assemblyName)
	{
		var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";

		// Priority 1: next to calling binary (CLI tool install — all DLLs
		// are in the same flat directory, no shared dir needed)
		var baseDir = AppContext.BaseDirectory;
		if (File.Exists(System.IO.Path.Combine(baseDir, assemblyName + ".dll"))
			|| File.Exists(System.IO.Path.Combine(baseDir, assemblyName + ext)))
		{
			return (baseDir, null, null);
		}

		// Priority 2: SeaShell.Service NuGet package in the NuGet cache
		var (nugetDir, sharedDir, packagePath) = FindServicePackageInNuGetCache(assemblyName);
		if (nugetDir != null)
			return (nugetDir, sharedDir, packagePath);

		return (null, null, null);
	}

	/// <summary>
	/// Find the SeaShell.Service package in the NuGet cache and return the
	/// RID-specific directory, the shared directory (runtimes/any/), and the package path.
	/// </summary>
	private static (string? sourceDir, string? sharedDir, string? packagePath) FindServicePackageInNuGetCache(
		string assemblyName)
	{
		var nugetCache = System.IO.Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
		var serviceDir = System.IO.Path.Combine(nugetCache, "seashell.service");
		if (!Directory.Exists(serviceDir))
			return (null, null, null);

		// Find the latest installed version
		var latestVersion = System.Linq.Enumerable.FirstOrDefault(
			System.Linq.Enumerable.OrderByDescending(
				Directory.GetDirectories(serviceDir),
				d => System.IO.Path.GetFileName(d)));
		if (latestVersion == null)
			return (null, null, null);

		var rid = CurrentRid;
		var ridDir = System.IO.Path.Combine(latestVersion, "runtimes", rid);
		var anyDir = System.IO.Path.Combine(latestVersion, "runtimes", "any");
		var sharedDir = Directory.Exists(anyDir) ? anyDir : null;

		if (Directory.Exists(ridDir) && Directory.GetFiles(ridDir, assemblyName + "*").Length > 0)
			return (ridDir, sharedDir, latestVersion);

		// Fallback: managed-only (no platform-specific binary)
		if (sharedDir != null && Directory.GetFiles(anyDir, assemblyName + "*").Length > 0)
			return (anyDir, null, latestVersion);

		return (null, null, null);
	}
}
