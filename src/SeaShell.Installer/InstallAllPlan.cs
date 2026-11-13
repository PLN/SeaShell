using System;
using System.Collections.Generic;

namespace SeaShell.Installer;

/// <summary>Which identity an install pass runs against.</summary>
public enum InstallPassScope
{
	/// <summary>The invoking user's profile (binaries, PATH, tasks, associations).</summary>
	User,

	/// <summary>Windows LocalSystem (boot-triggered daemon, no user-profile state).</summary>
	System,
}

/// <summary>
/// The Windows plan for one install invocation: whether to relaunch elevated
/// instead of installing, and — in this process — which targets to install,
/// in which order, and whether the user pass registers the elevator.
/// </summary>
public sealed record InstallAllPlan
{
	public required bool RelaunchElevated { get; init; }

	/// <summary>
	/// True when the elevated account is not the invoking user, so the
	/// user-profile pass would write into the wrong profile and is skipped.
	/// The non-elevated invoker runs it afterwards.
	/// </summary>
	public required bool SkipUserParts { get; init; }

	/// <summary>Targets to install in this process, in order. Empty when relaunching elevated.</summary>
	public required IReadOnlyList<InstallPassScope> Passes { get; init; }

	/// <summary>The user pass registers the elevator task (needs an elevated token).</summary>
	public required bool RegisterElevator { get; init; }
}

/// <summary>
/// The Linux plan for one install invocation. There is no relaunch of the
/// whole run: the user pass runs here, and the root pass is a second run of
/// the same bootstrapper through <c>sudo</c>.
/// </summary>
public sealed record LinuxInstallPlan
{
	/// <summary>Install the invoking user's own install in this process.</summary>
	public required bool UserPass { get; init; }

	/// <summary>Re-run this bootstrapper through sudo with <c>install --pass system</c>.</summary>
	public required bool SudoSystemPass { get; init; }

	/// <summary>
	/// Invoked as root: the ambient (root) install is the only pass, and the
	/// invoking user's install is not touched.
	/// </summary>
	public required bool RootPassOnly { get; init; }
}

/// <summary>
/// Decides the shape of an install from its effective scope. The retargeting
/// itself (current user → LocalSystem) is driven from this plan so ordering,
/// elevation and the over-the-shoulder-UAC skip are testable without
/// touching the machine.
/// </summary>
public static class InstallAllPlanner
{
	/// <summary>
	/// Over-the-shoulder UAC: when the elevated child runs as a different
	/// account than the invoker, the user-profile half of the install would
	/// land in that other account's profile. The invoker SID is passed down on
	/// the relaunch command line and compared with the elevated identity's.
	/// No invoker SID (a plain elevated invocation) means the invoker is the
	/// elevated account.
	/// </summary>
	public static bool ShouldSkipUserParts(string? invokerToken, string elevatedToken) =>
		invokerToken != null
		&& !string.Equals(invokerToken, elevatedToken, StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether installing these parts needs elevation on Windows.</summary>
	public static bool NeedsElevation(ScopeParts parts) =>
		(parts & ScopeTokens.NeedingElevation) != 0;

	/// <summary>
	/// Build the Windows plan. A non-elevated caller whose scope needs
	/// elevation relaunches itself elevated and does no work; otherwise the
	/// user profile is installed first (so the daemon/elevator chain is up
	/// before the SYSTEM daemon replaces it in Task Scheduler) and LocalSystem
	/// last. Parts that could not be detected never cause a UAC prompt by
	/// themselves: they are resolved by the elevated helper when elevation is
	/// needed anyway, and otherwise left alone (0.6.2 behaviour).
	/// </summary>
	/// <param name="parts">Effective scope parts.</param>
	/// <param name="opportunisticElevator">
	/// No scope flag and no <c>--pass</c> was given: an elevated user pass
	/// registers the elevator as 0.6.2 did, and the scope then records it.
	/// </param>
	public static InstallAllPlan Plan(
		ScopeParts parts,
		bool isElevated,
		string? invokerToken,
		string elevatedToken,
		bool opportunisticElevator)
	{
		var userPass = (parts & (ScopeParts.User | ScopeParts.Elevator)) != 0;

		if (!isElevated)
		{
			if (NeedsElevation(parts))
				return new InstallAllPlan
				{
					RelaunchElevated = true,
					SkipUserParts = false,
					Passes = Array.Empty<InstallPassScope>(),
					RegisterElevator = false,
				};

			return new InstallAllPlan
			{
				RelaunchElevated = false,
				SkipUserParts = false,
				Passes = userPass ? new[] { InstallPassScope.User } : Array.Empty<InstallPassScope>(),
				RegisterElevator = false,
			};
		}

		var skipUserParts = ShouldSkipUserParts(invokerToken, elevatedToken);
		var passes = new List<InstallPassScope>();
		if (userPass && !skipUserParts) passes.Add(InstallPassScope.User);
		if ((parts & ScopeParts.System) != 0) passes.Add(InstallPassScope.System);

		return new InstallAllPlan
		{
			RelaunchElevated = false,
			SkipUserParts = skipUserParts && userPass,
			Passes = passes,
			RegisterElevator = userPass && !skipUserParts
				&& ((parts & ScopeParts.Elevator) != 0 || opportunisticElevator),
		};
	}

	/// <summary>
	/// After the elevated helper returns: when it skipped the user parts
	/// (different account), the original non-elevated process runs the user
	/// pass itself — without an elevator, which needs the invoker's own
	/// elevated token.
	/// </summary>
	public static bool RunUserPassAfterRelaunch(ScopeParts parts, InstallRunSummary helperResult) =>
		helperResult.UserSkipped && (parts & (ScopeParts.User | ScopeParts.Elevator)) != 0;

	/// <summary>Build the Linux plan.</summary>
	public static LinuxInstallPlan PlanLinux(ScopeParts parts, bool isRoot)
	{
		if (isRoot)
			return new LinuxInstallPlan { UserPass = false, SudoSystemPass = false, RootPassOnly = true };

		return new LinuxInstallPlan
		{
			UserPass = (parts & ScopeParts.User) != 0,
			SudoSystemPass = (parts & ScopeParts.System) != 0,
			RootPassOnly = false,
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
