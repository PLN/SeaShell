using System;

namespace SeaShell.Installer;

/// <summary>Whether an install pass may touch the target at all.</summary>
public enum InstallGateOutcome
{
	/// <summary>No newer version is active — proceed with the install.</summary>
	Proceed,

	/// <summary>
	/// A newer version is active on this target. The pass must stop before any
	/// destructive step: stopping the newer daemon and re-registering this
	/// bootstrapper's task would leave <c>current</c> pointing at the newer
	/// version while booting the older one.
	/// </summary>
	SkipNewerVersionActive,
}

/// <summary>Result of the stale-bootstrapper check.</summary>
public readonly record struct InstallGateDecision(InstallGateOutcome Outcome, Version? ActiveVersion);

/// <summary>
/// The gate that runs after the target lock is acquired and before anything
/// on disk, in Task Scheduler, or in a running process is touched.
/// </summary>
public static class InstallGate
{
	/// <summary>
	/// Compare the bootstrapper's own version against the version the target's
	/// <c>current</c> junction points to.
	/// An unparseable bootstrapper version cannot be compared, so it proceeds
	/// (the old behaviour) rather than silently skipping the install.
	/// </summary>
	public static InstallGateDecision Evaluate(Version? activeVersion, string installerVersion)
	{
		if (!Version.TryParse(installerVersion, out var mine))
			return new InstallGateDecision(InstallGateOutcome.Proceed, activeVersion);

		return InstallVersionPolicy.WouldDowngrade(activeVersion, mine)
			? new InstallGateDecision(InstallGateOutcome.SkipNewerVersionActive, activeVersion)
			: new InstallGateDecision(InstallGateOutcome.Proceed, activeVersion);
	}
}