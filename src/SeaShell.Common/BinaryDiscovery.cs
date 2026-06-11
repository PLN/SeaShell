using System;
using System.IO;

namespace SeaShell;

/// <summary>
/// Locate the daemon and elevator binaries for an installed version. Reads the
/// <see cref="MarkerFile.DaemonPath"/> / <see cref="MarkerFile.ElevatorPath"/>
/// markers written by the installer, with a fall-forward to the newest installed
/// version &gt;= the requested one (covers hosts embedded at an older version
/// whose user has since upgraded).
/// </summary>
public static class BinaryDiscovery
{
	public static string? FindDaemon(string version, Action<string>? log = null) =>
		FindBinary(version, SeaShellPaths.DataDir, MarkerFile.DaemonPath, "daemon", log);

	public static string? FindElevator(string version, Action<string>? log = null) =>
		FindBinary(version, SeaShellPaths.DataDir, MarkerFile.ElevatorPath, "elevator", log);

	/// <summary>Find the daemon under an explicit data dir (target-identity installs).</summary>
	public static string? FindDaemon(string version, string dataDir, Action<string>? log = null) =>
		FindBinary(version, dataDir, MarkerFile.DaemonPath, "daemon", log);

	/// <summary>Find the elevator under an explicit data dir (target-identity installs).</summary>
	public static string? FindElevator(string version, string dataDir, Action<string>? log = null) =>
		FindBinary(version, dataDir, MarkerFile.ElevatorPath, "elevator", log);

	private static string? FindBinary(string version, string dataDir, string marker, string label, Action<string>? log)
	{
		var versionDir = SeaShellPaths.VersionDirIn(dataDir, version);
		var path = MarkerFile.Read(versionDir, marker);
		if (path != null && File.Exists(path))
		{
			log?.Invoke($"{label}: {path}");
			return path;
		}
		log?.Invoke($"{label}: marker at {versionDir} = {path ?? "<missing>"} (exists: {path != null && File.Exists(path)})");

		foreach (var (ver, dir) in VersionDiscovery.EnumerateVersions(dataDir))
		{
			if (System.Version.TryParse(version, out var requested) && ver < requested)
				continue;

			path = MarkerFile.Read(dir, marker);
			if (path != null && File.Exists(path))
			{
				log?.Invoke($"{label}: version {version} not found, using {ver} at {path}");
				return path;
			}
		}

		return null;
	}
}
