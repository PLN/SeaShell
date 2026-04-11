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
		// Note: there is deliberately no Address field here. The daemon's pipe/socket
		// address is a *runtime* property (determined by the daemon's own
		// Environment.UserName at startup, which on .NET 10 under LocalSystem is
		// "{MachineName}$", not "SYSTEM"). Storing an install-time prediction
		// here was unsound and has been removed — consumers that need to know
		// which daemons are currently live should call
		// TransportEndpoint.EnumerateDaemonEndpoints at query time.
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
	/// Get candidate daemon addresses for compatible versions (strictly newer than
	/// <paramref name="requestedVersion"/>), ordered by version descending (newest first).
	/// Excludes the exact requested version (caller probes that separately).
	/// <para>
	/// Unlike the previous design which read predicted addresses from the manifest,
	/// this enumerates pipes/sockets currently live on the host. The manifest is not
	/// consulted here — addresses are determined purely at runtime so an installer
	/// running under one principal (e.g. an elevated interactive user) cannot
	/// misrepresent the address of a daemon that later runs under a different
	/// principal (e.g. LocalSystem, whose <see cref="Environment.UserName"/>
	/// returns <c>{MachineName}$</c> on .NET 10).
	/// </para>
	/// <para>
	/// The returned addresses are already known to exist at call time (they were
	/// on <c>\\.\pipe\</c> or in <c>$XDG_RUNTIME_DIR</c>), but may still fail to
	/// accept a connection due to TOCTOU (daemon died between enumerate and probe)
	/// or ACLs. Callers still need to probe each address.
	/// </para>
	/// </summary>
	public static string[] GetCompatibleDaemonAddresses(string requestedVersion)
	{
		var identity = TransportEndpoint.CurrentUserIdentity;
		var endpoints = TransportEndpoint.EnumerateDaemonEndpoints(identity);

		// Already sorted newest-first by EnumerateDaemonEndpoints. Filter to
		// strictly-newer-than-requested (ordinal compare matches the old behavior).
		return endpoints
			.Where(e => string.Compare(e.Version, requestedVersion, StringComparison.Ordinal) > 0)
			.Select(e => e.Address)
			.ToArray();
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
