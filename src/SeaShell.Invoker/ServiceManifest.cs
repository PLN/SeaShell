using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SeaShell.Protocol;

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

	// ── Lookup ─────────────────────────────────────────────────────────
	// Daemon and elevator binaries are staged by the bootstrapper during
	// 'seashell install'. The Invoker just looks up the manifest.

	/// <summary>
	/// Get the staged daemon directory for the given version.
	/// Falls back to the latest version >= requested if exact match not found
	/// (covers Host embedding: app compiled at v140, user upgraded to v143).
	/// Returns null if SeaShell is not installed.
	/// </summary>
	public static string? GetOrStageDaemon(string version, Action<string>? log = null)
	{
		return LookupComponent(version, "daemon", log);
	}

	/// <summary>
	/// Get the staged elevator directory for the given version.
	/// </summary>
	public static string? GetOrStageElevator(string version, Action<string>? log = null)
	{
		return LookupComponent(version, "elevator", log);
	}

	/// <summary>
	/// Get candidate daemon addresses for compatible versions (&gt; requested).
	/// Returns addresses from the manifest, ordered by version descending
	/// (newest first). Excludes the exact requested version (caller probes that separately).
	/// Uses ComponentEntry.Address when available, otherwise constructs from version key.
	/// </summary>
	public static string[] GetCompatibleDaemonAddresses(string requestedVersion)
	{
		var manifest = Read();
		var identity = TransportEndpoint.CurrentUserIdentity;
		var candidates = new List<(string version, string address)>();

		foreach (var kvp in manifest.Installations)
		{
			if (string.Compare(kvp.Key, requestedVersion, StringComparison.Ordinal) <= 0)
				continue; // same or older — skip

			var daemon = kvp.Value.Daemon;
			if (daemon == null)
				continue;

			var address = daemon.Address
				?? TransportEndpoint.GetDaemonAddress(identity, kvp.Key);
			candidates.Add((kvp.Key, address));
		}

		// Newest first — probe latest compatible version first
		candidates.Sort((a, b) =>
		{
			var av = Version.TryParse(a.version, out var va) ? va : new Version(0, 0);
			var bv = Version.TryParse(b.version, out var vb) ? vb : new Version(0, 0);
			return bv.CompareTo(av);
		});
		return candidates.Select(c => c.address).ToArray();
	}

	private static string? LookupComponent(string version, string componentName, Action<string>? log)
	{
		var manifest = Read();

		// Exact version match
		if (manifest.Installations.TryGetValue(version, out var entry))
		{
			var existing = componentName == "daemon" ? entry.Daemon : entry.Elevator;
			if (existing?.Path != null && Directory.Exists(existing.Path))
			{
				log?.Invoke($"{componentName}: using staged {existing.Path}");
				return existing.Path;
			}
		}

		// Fallback: find the latest version >= requested
		// (Host compiled at older version, SeaShell upgraded since)
		string? bestVersion = null;
		string? bestPath = null;
		foreach (var kvp in manifest.Installations)
		{
			if (string.Compare(kvp.Key, version, StringComparison.Ordinal) < 0)
				continue; // older than requested

			var comp = componentName == "daemon" ? kvp.Value.Daemon : kvp.Value.Elevator;
			if (comp?.Path == null || !Directory.Exists(comp.Path))
				continue;

			if (bestVersion == null || string.Compare(kvp.Key, bestVersion, StringComparison.Ordinal) > 0)
			{
				bestVersion = kvp.Key;
				bestPath = comp.Path;
			}
		}

		if (bestPath != null)
		{
			log?.Invoke($"{componentName}: version {version} not found, using {bestVersion} at {bestPath}");
			return bestPath;
		}

		log?.Invoke($"{componentName}: not installed (run 'seashell install')");
		return null;
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

}
