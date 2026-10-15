using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaShell.Installer;

/// <summary>What a probe of an old version's daemon found.</summary>
public enum DaemonProbeState
{
	/// <summary>No pipe marker — nothing was listening, nothing to stop.</summary>
	NoPipeMarker,

	/// <summary>Pipe marker present but no answer: a hung or already-gone daemon.</summary>
	Unreachable,

	/// <summary>Daemon answered and is idle.</summary>
	Idle,

	/// <summary>Daemon answered and is running scripts.</summary>
	Busy,
}

/// <summary>How far version cleanup may go for one old version directory.</summary>
public enum VersionCleanupAction
{
	/// <summary>No live daemon: remove tasks and the version directory.</summary>
	Remove,

	/// <summary>Idle daemon: ask it to stop, then remove.</summary>
	StopThenRemove,

	/// <summary>Unresponsive daemon with a live pid marker: kill it, then remove.</summary>
	KillStaleThenRemove,

	/// <summary>Scripts are running: leave the version alone, retry on a later install.</summary>
	SkipInUse,
}

/// <summary>
/// Which installed versions an install pass may clean up, and what may be done
/// to each. Deleting a running daemon's directory out from under an executing
/// script is the failure mode this guards.
/// </summary>
public static class VersionCleanupPlan
{
	/// <summary>
	/// Versions strictly older than the one being installed, oldest first.
	/// The current version and anything newer are never candidates; an
	/// unparseable installer version disables cleanup entirely.
	/// </summary>
	public static IReadOnlyList<(Version Version, string Directory)> SelectOldestFirst(
		IEnumerable<(Version Version, string Directory)> installed,
		Version? installerVersion)
	{
		if (installerVersion == null) return Array.Empty<(Version, string)>();

		var older = installed.Where(v => v.Version < installerVersion).ToList();
		older.Sort((a, b) => a.Version.CompareTo(b.Version));
		return older;
	}

	/// <summary>Decide what may be done to one candidate version from its probe state.</summary>
	public static VersionCleanupAction Decide(DaemonProbeState state) => state switch
	{
		DaemonProbeState.Busy => VersionCleanupAction.SkipInUse,
		DaemonProbeState.Idle => VersionCleanupAction.StopThenRemove,
		DaemonProbeState.Unreachable => VersionCleanupAction.KillStaleThenRemove,
		_ => VersionCleanupAction.Remove,
	};
}