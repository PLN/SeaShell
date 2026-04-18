using System;
using System.IO;

namespace SeaShell;

/// <summary>
/// Read/write marker files that track per-version installation state.
/// Each marker lives in the version directory (e.g. <c>v0.5.0.222/daemon.path</c>).
/// </summary>
public static class MarkerFile
{
	// ── Marker names ───────────────────────────────────────────────────

	/// <summary>Full path to the daemon binary. Presence = version is installed.</summary>
	public const string DaemonPath = "daemon.path";

	/// <summary>Task Scheduler path/name for the daemon task.</summary>
	public const string DaemonTask = "daemon.task";

	/// <summary>Daemon process ID (written by daemon on startup).</summary>
	public const string DaemonPid = "daemon.pid";

	/// <summary>Daemon named pipe / unix socket path (written by daemon on startup).</summary>
	public const string DaemonPipe = "daemon.pipe";

	/// <summary>Full path to the elevator binary.</summary>
	public const string ElevatorPath = "elevator.path";

	/// <summary>Task Scheduler path/name for the elevator task.</summary>
	public const string ElevatorTask = "elevator.task";

	/// <summary>Elevator process ID (written by elevator on startup).</summary>
	public const string ElevatorPid = "elevator.pid";

	// ── Operations ─────────────────────────────────────────────────────

	/// <summary>Full path to a marker file.</summary>
	public static string GetPath(string versionDir, string markerName) =>
		Path.Combine(versionDir, markerName);

	/// <summary>Read a marker's content. Returns null if the file does not exist.</summary>
	public static string? Read(string versionDir, string markerName)
	{
		var path = GetPath(versionDir, markerName);
		try
		{
			return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Write content to a marker file. Creates the directory if needed.</summary>
	public static void Write(string versionDir, string markerName, string content)
	{
		Directory.CreateDirectory(versionDir);
		File.WriteAllText(GetPath(versionDir, markerName), content);
	}

	/// <summary>Delete a marker file. No-op if already absent.</summary>
	public static void Delete(string versionDir, string markerName)
	{
		try { File.Delete(GetPath(versionDir, markerName)); }
		catch { }
	}

	/// <summary>Check whether a marker file exists.</summary>
	public static bool Exists(string versionDir, string markerName) =>
		File.Exists(GetPath(versionDir, markerName));
}
