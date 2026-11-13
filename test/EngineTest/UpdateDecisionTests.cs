using System;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class UpdateDecisionTests
{
	private const string Mine = "0.6.3.300";
	private static readonly Version MineVersion = new(0, 6, 3, 300);

	private static TargetFacts Facts(Version? active, PartPresence daemonTask = PartPresence.Present, PartPresence elevatorTask = PartPresence.Present) => new()
	{
		ActiveVersion = active,
		Daemon = active != null ? PartPresence.Present : PartPresence.Absent,
		Binaries = active != null ? PartPresence.Present : PartPresence.Absent,
		DaemonTask = daemonTask,
		ElevatorTask = elevatorTask,
	};

	[Fact]
	public void SameVersionWithEveryPart_IsSkipped()
	{
		Assert.Equal(UpdateTargetAction.Skip,
			UpdateDecision.Decide(InstallPassScope.User, ScopeParts.Elevator, Facts(MineVersion), Mine, isWindows: true));
	}

	[Fact]
	public void OlderActiveVersion_IsInstalled()
	{
		Assert.Equal(UpdateTargetAction.Install,
			UpdateDecision.Decide(InstallPassScope.User, ScopeParts.None, Facts(new Version(0, 6, 2, 283)), Mine, isWindows: true));
	}

	[Fact]
	public void SameVersionButARequestedPartIsMissing_IsInstalled()
	{
		// e.g. `seashell update --all` after a user-only install of the same version.
		Assert.Equal(UpdateTargetAction.Install,
			UpdateDecision.Decide(InstallPassScope.User, ScopeParts.Elevator, Facts(MineVersion, elevatorTask: PartPresence.Absent), Mine, isWindows: true));
		Assert.Equal(UpdateTargetAction.Skip,
			UpdateDecision.Decide(InstallPassScope.User, ScopeParts.None, Facts(MineVersion, elevatorTask: PartPresence.Absent), Mine, isWindows: true));
	}

	[Fact]
	public void SomethingThatCouldNotBeSeen_IsNeverSatisfied()
	{
		// The non-elevated view of SYSTEM: nothing visible.
		var unseen = new TargetFacts
		{
			ActiveVersion = null,
			Daemon = PartPresence.Unknown,
			Binaries = PartPresence.Unknown,
			DaemonTask = PartPresence.Unknown,
			ElevatorTask = PartPresence.Absent,
		};
		Assert.Equal(UpdateTargetAction.Install,
			UpdateDecision.Decide(InstallPassScope.System, ScopeParts.None, unseen, Mine, isWindows: true));
		Assert.Equal(UpdateTargetAction.Install,
			UpdateDecision.Decide(InstallPassScope.System, ScopeParts.None, Facts(MineVersion, daemonTask: PartPresence.Unknown), Mine, isWindows: true));
	}

	[Fact]
	public void Linux_HasNoTasks_VersionAndBinariesDecide()
	{
		var linux = Facts(MineVersion, daemonTask: PartPresence.Absent, elevatorTask: PartPresence.Absent);

		Assert.Equal(UpdateTargetAction.Skip,
			UpdateDecision.Decide(InstallPassScope.System, ScopeParts.None, linux, Mine, isWindows: false));
	}

	[Fact]
	public void StaleMarker_WithMissingBinaries_IsRepaired()
	{
		// Review finding: daemon.path + task present but the binary deleted was "Up to date".
		var broken = Facts(MineVersion) with { Binaries = PartPresence.Absent };
		Assert.Equal(UpdateTargetAction.Install,
			UpdateDecision.Decide(InstallPassScope.User, ScopeParts.None, broken, Mine, isWindows: true));
		Assert.Equal(TargetPrecheck.NeedsWork,
			UpdateDecision.Precheck(InstallPassScope.User, ScopeParts.None, broken, Mine, true, ifNeeded: true));
	}

	[Fact]
	public void BinariesComplete_RequiresEveryArchiveFileAtItsSize()
	{
		var manifest = new[] { ("sea.exe", 10L), ("seashell-daemon.exe", 20L) };
		var files = new System.Collections.Generic.Dictionary<string, long> { ["sea.exe"] = 10, ["seashell-daemon.exe"] = 20 };
		long? Length(string p) => files.TryGetValue(p, out var l) ? l : null;

		Assert.Equal(PartPresence.Present, UpdateDecision.BinariesComplete(manifest, Length));
		files["seashell-daemon.exe"] = 19;
		Assert.Equal(PartPresence.Absent, UpdateDecision.BinariesComplete(manifest, Length));
		files.Remove("seashell-daemon.exe");
		Assert.Equal(PartPresence.Absent, UpdateDecision.BinariesComplete(manifest, Length));
		Assert.Equal(PartPresence.Unknown, UpdateDecision.BinariesComplete(manifest, _ => throw new UnauthorizedAccessException()));
		Assert.Equal(PartPresence.Unknown, UpdateDecision.BinariesComplete(null, Length));
	}

	[Fact]
	public void SystemWithoutElevation_LiveDaemonAtThisVersion_IsSatisfiedWithoutAPrompt()
	{
		// E2E scenario 2: SYSTEM task hidden from the non-elevated user, its daemon endpoint visible.
		var system = TargetFacts.SystemSeenWithoutElevation(liveDaemonVersion: MineVersion, visibleTaskVersion: null);
		Assert.Equal(TargetPrecheck.Satisfied,
			UpdateDecision.Precheck(InstallPassScope.System, ScopeParts.None, system, Mine, true, ifNeeded: true));

		// A visible task alone says nothing about the binaries: the elevated helper decides.
		var taskOnly = TargetFacts.SystemSeenWithoutElevation(liveDaemonVersion: null, visibleTaskVersion: MineVersion);
		Assert.Equal(TargetPrecheck.NeedsWork,
			UpdateDecision.Precheck(InstallPassScope.System, ScopeParts.None, taskOnly, Mine, true, ifNeeded: true));
		Assert.Equal(TargetPrecheck.NeedsWork,
			UpdateDecision.Precheck(InstallPassScope.System, ScopeParts.None, TargetFacts.SystemSeenWithoutElevation(null, null), Mine, true, ifNeeded: true));
	}

	[Fact]
	public void PinnedOlderVersion_IsSkippedForEveryNewerTarget_WithoutAPrompt()
	{
		// E2E scenario 5: `update --version <older>` elevated only to print "newer version already active".
		var newer = new Version(0, 6, 3, 301);
		Assert.Equal(TargetPrecheck.SkippedNewer,
			UpdateDecision.Precheck(InstallPassScope.User, ScopeParts.Elevator, Facts(newer), Mine, true, ifNeeded: true));
		Assert.Equal(TargetPrecheck.SkippedNewer,
			UpdateDecision.Precheck(InstallPassScope.System, ScopeParts.None, TargetFacts.SystemSeenWithoutElevation(newer, null), Mine, true, ifNeeded: true));
		// A plain install (not update mode) of the same version is a repair: it needs work.
		Assert.Equal(TargetPrecheck.NeedsWork,
			UpdateDecision.Precheck(InstallPassScope.User, ScopeParts.None, Facts(MineVersion), Mine, true, ifNeeded: false));
	}
}
