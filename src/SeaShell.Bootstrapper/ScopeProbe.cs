using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SeaShell;
using SeaShell.Installer;
using SeaShell.Protocol;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Read-only probes behind scope detection, <c>--if-needed</c> and the
/// dashboard's system row. Every probe separates "absent" from "could not
/// look" (<see cref="PartPresence.Unknown"/>).
/// </summary>
internal static class ScopeProbe
{
	/// <summary>Linux root install location.</summary>
	public const string LinuxRootDataDir = "/var/lib/seashell";

	private const string SystemDaemonTaskPrefix = "SeaShell Daemon (SYSTEM)";

	/// <summary>
	/// Detect the elevator and system parts of an install that has no
	/// <c>install.scope</c> (made by 0.6.2 or earlier).
	/// </summary>
	public static ScopeDetection Detect(bool isWindows) =>
		isWindows
			? new ScopeDetection(DetectElevator(), DetectWindowsSystem())
			: new ScopeDetection(PartPresence.Absent, DetectLinuxRoot());

	// Elevator: the elevator.task marker of the user's current version, and a
	// registration that can actually be queried.
	private static PartPresence DetectElevator()
	{
		var dataDir = SeaShellIdentity.Current.DataDir;
		var current = VersionDiscovery.GetCurrentVersion(dataDir);
		if (current == null) return PartPresence.Absent;
		var versionDir = SeaShellPaths.VersionDirIn(dataDir, current.ToString());
		try
		{
			if (!File.Exists(MarkerFile.GetPath(versionDir, MarkerFile.ElevatorTask))) return PartPresence.Absent;
		}
		catch { return PartPresence.Unknown; }
		var taskPath = MarkerFile.Read(versionDir, MarkerFile.ElevatorTask);
		return string.IsNullOrEmpty(taskPath) ? PartPresence.Unknown : TaskQuery.Probe(taskPath);
	}

	// SYSTEM: a live SYSTEM daemon endpoint or a visible SYSTEM daemon task
	// proves it. Absence is only certain when SYSTEM's data dir can be read
	// (elevated) or does not exist at all — a non-elevated listing of an
	// existing one is denied, which is Unknown.
	private static PartPresence DetectWindowsSystem()
	{
		if (SystemEndpointVersion() != null) return PartPresence.Present;
		if (SystemTaskVersion(out _)) return PartPresence.Present;

		var sysDataDir = SeaShellIdentity.LocalSystem.DataDir;
		try
		{
			Directory.GetFileSystemEntries(sysDataDir);
		}
		catch (DirectoryNotFoundException) { return PartPresence.Absent; }
		catch { return PartPresence.Unknown; }

		return VersionDiscovery.GetCurrentVersion(sysDataDir) != null ? PartPresence.Present : PartPresence.Absent;
	}

	// Linux root: /var/lib/seashell/current.
	private static PartPresence DetectLinuxRoot()
	{
		try
		{
			var entries = Directory.GetFileSystemEntries(LinuxRootDataDir);
			return entries.Any(e => Path.GetFileName(e) == "current") ? PartPresence.Present : PartPresence.Absent;
		}
		catch (DirectoryNotFoundException) { return PartPresence.Absent; }
		catch (UnauthorizedAccessException)
		{
			// Listing denied; a lookup of the entry itself may still work.
			return Directory.Exists(SeaShellPaths.CurrentDirIn(LinuxRootDataDir)) ? PartPresence.Present : PartPresence.Unknown;
		}
		catch { return PartPresence.Unknown; }
	}

	/// <summary>Version of a live SYSTEM daemon endpoint (any version), or null.</summary>
	public static Version? SystemEndpointVersion()
	{
		if (!OperatingSystem.IsWindows()) return null;
		var sys = SeaShellIdentity.LocalSystem;
		foreach (var ep in TransportEndpoint.EnumerateAllSeashellEndpoints())
		{
			if (ep.IsElevator || !sys.Matches(ep.Identity)) continue;
			return Version.TryParse(ep.Version, out var v) ? v : new Version(0, 0);
		}
		return null;
	}

	/// <summary>
	/// A SYSTEM daemon task is visible in <c>\SeaShell\</c>. Its name carries
	/// the version it was registered for (null for a pre-0.6.0 name).
	/// </summary>
	public static bool SystemTaskVersion(out Version? version)
	{
		version = null;
		var path = TaskQuery.EnumerateFolder()
			.FirstOrDefault(p => SeaShellTaskNames.NameOf(p).StartsWith(SystemDaemonTaskPrefix, StringComparison.OrdinalIgnoreCase));
		if (path == null) return false;
		version = TaskQuery.ParseVersionSuffix(path);
		return true;
	}

	/// <summary>
	/// What is installed on a target whose data dir this process can read
	/// (the user target, an elevated view of SYSTEM, the Linux root install
	/// from root or with a world-readable layout). <paramref name="expectedFiles"/>
	/// is the carried archive's manifest (<see cref="EmbeddedArchive.Manifest"/>):
	/// the binaries are only "present" when every one of those files is.
	/// </summary>
	public static TargetFacts ReadFacts(string dataDir, bool isWindows, IReadOnlyCollection<(string Path, long Length)>? expectedFiles)
	{
		var active = VersionDiscovery.GetCurrentVersion(dataDir);
		if (active == null)
			return new TargetFacts
			{
				ActiveVersion = null,
				Daemon = PartPresence.Absent,
				Binaries = PartPresence.Absent,
				DaemonTask = PartPresence.Absent,
				ElevatorTask = PartPresence.Absent,
			};

		var versionDir = SeaShellPaths.VersionDirIn(dataDir, active.ToString());
		var binDir = SeaShellPaths.BinDirIn(dataDir, active.ToString());
		return new TargetFacts
		{
			ActiveVersion = active,
			Daemon = MarkedBinaryExists(versionDir, MarkerFile.DaemonPath),
			Binaries = UpdateDecision.BinariesComplete(expectedFiles, relative =>
			{
				var info = new FileInfo(Path.Combine(binDir, relative));
				return info.Exists ? info.Length : null;
			}),
			DaemonTask = isWindows ? ProbeMarkedTask(versionDir, MarkerFile.DaemonTask) : PartPresence.Absent,
			ElevatorTask = isWindows ? ProbeMarkedTask(versionDir, MarkerFile.ElevatorTask) : PartPresence.Absent,
		};
	}

	/// <summary>
	/// The SYSTEM target as seen without elevation. SYSTEM's data dir cannot
	/// be read, and a task registered by an elevated process is not listed for
	/// a standard token, so the evidence is a live SYSTEM daemon endpoint
	/// (its name carries the version it runs) and, when visible, the daemon
	/// task (whose name does too). A daemon running from a version proves
	/// that version's binaries and its daemon task are there as far as
	/// anyone can tell without elevation; without a live daemon the binaries
	/// stay Unknown and the elevated helper decides.
	/// </summary>
	public static TargetFacts SystemFactsWithoutElevation()
	{
		var endpoint = SystemEndpointVersion();
		SystemTaskVersion(out var taskVersion);
		return TargetFacts.SystemSeenWithoutElevation(
			endpoint != null && endpoint != new Version(0, 0) ? endpoint : null, taskVersion);
	}

	// The marker names a binary; a stale marker whose binary is gone is not proof.
	private static PartPresence MarkedBinaryExists(string versionDir, string marker)
	{
		try
		{
			var path = MarkerFile.Read(versionDir, marker);
			return !string.IsNullOrEmpty(path) && File.Exists(path) ? PartPresence.Present : PartPresence.Absent;
		}
		catch { return PartPresence.Unknown; }
	}

	private static PartPresence ProbeMarkedTask(string versionDir, string marker)
	{
		var taskPath = MarkerFile.Read(versionDir, marker);
		return string.IsNullOrEmpty(taskPath) ? PartPresence.Absent : TaskQuery.Probe(taskPath);
	}
}
