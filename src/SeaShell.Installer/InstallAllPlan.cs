using System;
using System.Collections.Generic;

namespace SeaShell.Installer;

/// <summary>Which identity a composite <c>install --all</c> pass runs against.</summary>
public enum InstallPassScope
{
	/// <summary>The invoking user's profile (binaries, PATH, tasks, associations).</summary>
	User,

	/// <summary>Windows LocalSystem (boot-triggered daemon, no user-profile state).</summary>
	System,
}

/// <summary>
/// The plan for one <c>install --all</c> invocation: whether to relaunch
/// elevated instead of installing, and — once elevated — which targets to
/// install, in which order.
/// </summary>
public sealed record InstallAllPlan
{
	public required bool RelaunchElevated { get; init; }

	/// <summary>
	/// True when the elevated account is not the invoking user, so the
	/// user-profile pass would write into the wrong profile and is skipped.
	/// </summary>
	public required bool SkipUserParts { get; init; }

	/// <summary>Targets to install, in order. Empty when relaunching elevated.</summary>
	public required IReadOnlyList<InstallPassScope> Passes { get; init; }
}

/// <summary>
/// Decides the shape of a composite install behind a single UAC prompt. The
/// retargeting itself (current user → LocalSystem) is driven from this plan so
/// the ordering and the over-the-shoulder-UAC skip are testable without
/// touching the machine.
/// </summary>
public static class InstallAllPlanner
{
	/// <summary>
	/// Whether <c>--all</c> means the composite user+SYSTEM install or an ordinary single pass.
	/// The composite exists to fold a user install and a LocalSystem install into
	/// one UAC prompt, so it is a Windows-only idea: on Linux <c>--system</c> is
	/// just &quot;run as root&quot;, and one ordinary pass already covers it.
	/// </summary>
	public static bool IsCompositeInstall(bool allMode, bool isWindows) => allMode && isWindows;

	/// <summary>
	/// Over-the-shoulder UAC: when the elevated child runs as a different
	/// account than the invoker, the user-profile half of the install would
	/// land in that other account's profile. The invoker SID is passed down on
	/// the relaunch command line and compared with the elevated identity's.
	/// No invoker SID (plain elevated <c>install --all</c>) means the invoker
	/// is the elevated account.
	/// </summary>
	public static bool ShouldSkipUserParts(string? invokerToken, string elevatedToken) =>
		invokerToken != null
		&& !string.Equals(invokerToken, elevatedToken, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Build the pass plan. A non-elevated caller relaunches itself elevated
	/// and does no work; an elevated caller installs the user profile first
	/// (so the daemon/elevator chain is up before the SYSTEM daemon replaces
	/// it in Task Scheduler) and LocalSystem last.
	/// </summary>
	public static InstallAllPlan Plan(bool isElevated, string? invokerToken, string elevatedToken)
	{
		if (!isElevated)
			return new InstallAllPlan
			{
				RelaunchElevated = true,
				SkipUserParts = false,
				Passes = Array.Empty<InstallPassScope>(),
			};

		var skipUserParts = ShouldSkipUserParts(invokerToken, elevatedToken);
		return new InstallAllPlan
		{
			RelaunchElevated = false,
			SkipUserParts = skipUserParts,
			Passes = skipUserParts
				? new[] { InstallPassScope.System }
				: new[] { InstallPassScope.User, InstallPassScope.System },
		};
	}

	/// <summary>
	/// After a successful user pass, bring the daemon + elevator up. A pass
	/// that installed nothing because a newer version is already active must
	/// not start this bootstrapper's binaries over the running newer install.
	/// </summary>
	public static bool ShouldStartUserServiceChain(bool userPassSkippedNewerVersion) =>
		!userPassSkippedNewerVersion;
}