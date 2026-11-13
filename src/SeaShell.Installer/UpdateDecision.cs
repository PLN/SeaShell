using System;
using System.Collections.Generic;

namespace SeaShell.Installer;

/// <summary>
/// What is installed on one target, as far as the caller could see. A fact
/// that could not be read is <see cref="PartPresence.Unknown"/> (or a null
/// version) and never counts as satisfied.
/// </summary>
public sealed record TargetFacts
{
	/// <summary>Version the target's <c>current</c> junction points to; null when none or unreadable.</summary>
	public required Version? ActiveVersion { get; init; }

	/// <summary>The active version's <c>daemon.path</c> marker, and the binary it names exists.</summary>
	public required PartPresence Daemon { get; init; }

	/// <summary>
	/// Every file of the archive this bootstrapper carries is in the active
	/// version's bin directory, at its size (<see cref="UpdateDecision.BinariesComplete"/>).
	/// Unknown when the directory cannot be read or there is nothing to compare with.
	/// </summary>
	public required PartPresence Binaries { get; init; }

	/// <summary>The daemon task of the active version is registered (Windows).</summary>
	public required PartPresence DaemonTask { get; init; }

	/// <summary>The elevator task of the active version is registered (Windows user target).</summary>
	public required PartPresence ElevatorTask { get; init; }

	/// <summary>
	/// The Windows SYSTEM target as a non-elevated process sees it: SYSTEM's
	/// data dir is unreadable and its daemon task is usually not listed, but a
	/// live SYSTEM daemon endpoint is, and its name carries the version it
	/// runs. A daemon running from a version is the evidence that version's
	/// binaries and task are in place; a visible task alone proves neither
	/// that the binaries are complete nor which version <c>current</c> is on,
	/// so without a live daemon the binaries stay Unknown.
	/// </summary>
	/// <param name="liveDaemonVersion">Version of a live SYSTEM daemon endpoint; null when none.</param>
	/// <param name="visibleTaskVersion">Version in the name of a visible SYSTEM daemon task; null when none.</param>
	public static TargetFacts SystemSeenWithoutElevation(Version? liveDaemonVersion, Version? visibleTaskVersion) => new()
	{
		ActiveVersion = liveDaemonVersion ?? visibleTaskVersion,
		Daemon = liveDaemonVersion != null || visibleTaskVersion != null ? PartPresence.Present : PartPresence.Unknown,
		Binaries = liveDaemonVersion != null ? PartPresence.Present : PartPresence.Unknown,
		DaemonTask = liveDaemonVersion != null || visibleTaskVersion != null ? PartPresence.Present : PartPresence.Unknown,
		ElevatorTask = PartPresence.Absent,
	};
}

/// <summary>The <c>install --if-needed</c> decision for one target.</summary>
public enum UpdateTargetAction
{
	/// <summary>Install (or repair) this target.</summary>
	Install,

	/// <summary>The target already has this version and every requested part.</summary>
	Skip,
}

/// <summary>
/// <c>install --if-needed</c> decides per target, under that target's install
/// lock: a target that already has this version and every part in scope is
/// left alone; anything else — including a missing part at the same version —
/// is installed. A newer active version is handled by <see cref="InstallGate"/>
/// as for a plain install.
/// </summary>
public static class UpdateDecision
{
	/// <param name="target">Which target the facts describe.</param>
	/// <param name="parts">Parts in scope for this target (the user target: user, and elevator if wanted).</param>
	/// <param name="facts">What was found on the target.</param>
	/// <param name="installerVersion">Version this bootstrapper installs.</param>
	/// <param name="isWindows">Task registrations exist on Windows only.</param>
	public static UpdateTargetAction Decide(
		InstallPassScope target, ScopeParts parts, TargetFacts facts, string installerVersion, bool isWindows)
	{
		if (!Version.TryParse(installerVersion, out var mine)) return UpdateTargetAction.Install;
		if (facts.ActiveVersion != mine) return UpdateTargetAction.Install;
		if (facts.Daemon != PartPresence.Present) return UpdateTargetAction.Install;
		if (facts.Binaries != PartPresence.Present) return UpdateTargetAction.Install;

		if (isWindows)
		{
			if (facts.DaemonTask != PartPresence.Present) return UpdateTargetAction.Install;
			if (target == InstallPassScope.User
				&& (parts & ScopeParts.Elevator) != 0
				&& facts.ElevatorTask != PartPresence.Present)
				return UpdateTargetAction.Install;
		}

		return UpdateTargetAction.Skip;
	}

	/// <summary>
	/// Whether a target's bin directory holds every file of the archive at its
	/// size. A marker alone proves nothing: it survives a deleted binary.
	/// </summary>
	/// <param name="expected">Archive entries (relative path, uncompressed length); null when there is no archive to compare with.</param>
	/// <param name="actualLength">Length of the file at a relative path; null when it is missing. May throw when the directory cannot be read.</param>
	public static PartPresence BinariesComplete(
		IReadOnlyCollection<(string Path, long Length)>? expected, Func<string, long?> actualLength)
	{
		if (expected == null || expected.Count == 0) return PartPresence.Unknown;
		try
		{
			foreach (var (path, length) in expected)
			{
				if (actualLength(path) != length) return PartPresence.Absent;
			}
			return PartPresence.Present;
		}
		catch { return PartPresence.Unknown; }
	}

	/// <summary>
	/// The outcome for one target as far as it can be seen before acting —
	/// in particular before asking for UAC. <see cref="TargetPrecheck.NeedsWork"/>
	/// whenever anything is uncertain; the pass itself then decides again
	/// under the target's lock.
	/// </summary>
	/// <param name="ifNeeded">Update mode: a target already at this version with every part is satisfied.</param>
	public static TargetPrecheck Precheck(
		InstallPassScope target, ScopeParts parts, TargetFacts facts, string installerVersion, bool isWindows, bool ifNeeded)
	{
		if (InstallGate.Evaluate(facts.ActiveVersion, installerVersion).Outcome == InstallGateOutcome.SkipNewerVersionActive)
			return TargetPrecheck.SkippedNewer;
		if (ifNeeded && Decide(target, parts, facts, installerVersion, isWindows) == UpdateTargetAction.Skip)
			return TargetPrecheck.Satisfied;
		return TargetPrecheck.NeedsWork;
	}
}

/// <summary>See <see cref="UpdateDecision.Precheck"/>.</summary>
public enum TargetPrecheck
{
	/// <summary>Something may have to be installed: run the pass (elevated, if the target needs it).</summary>
	NeedsWork,

	/// <summary><c>--if-needed</c>: already at this version with every part.</summary>
	Satisfied,

	/// <summary>A newer version is active; the pass would skip it anyway.</summary>
	SkippedNewer,
}
