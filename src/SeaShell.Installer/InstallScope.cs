using System;

namespace SeaShell.Installer;

/// <summary>
/// Which install/uninstall steps apply to a target pass. The SYSTEM target
/// (Windows LocalSystem) deliberately owns none of the user-profile state:
/// editing PATH or registering file associations there would write the
/// invoking administrator's HKCU, not SYSTEM's.
///
/// Every predicate is a decision the installer actually branches on, so the
/// matrix is asserted directly instead of being re-derived at each call site.
/// </summary>
public sealed record InstallScope(bool IsWindows, bool IsSystemTarget)
{
	/// <summary>Add/remove the SeaShell bin directory in the user PATH.</summary>
	public bool EditUserProfilePath => !IsSystemTarget;

	/// <summary>Register/remove the .cs/.csw shell associations (HKCU).</summary>
	public bool EditFileAssociations => IsWindows && !IsSystemTarget;

	/// <summary>Drop a pre-0.5.0 flat bin entry from the user PATH.</summary>
	public bool StripLegacyBinFromUserPath => IsWindows && !IsSystemTarget;

	/// <summary>Remove pre-0.5.0 machine-wide "common install" leftovers.</summary>
	public bool CleanLegacyCommonInstall => !IsSystemTarget;

	/// <summary>Remove the pre-0.6.0 dotnet-tool-install-under-SYSTEM footprint.</summary>
	public bool SweepLegacySystemArtifacts => IsSystemTarget;

	/// <summary>
	/// Rewrite baked-in SeaShell paths in user schedules. SYSTEM owns no user
	/// schedules; on Linux every pass refreshes crontab (root's own, at worst).
	/// </summary>
	public bool RefreshScheduledTaskPaths => !IsWindows || !IsSystemTarget;

	/// <summary>Register the boot-triggered SYSTEM daemon task.</summary>
	public bool RegisterSystemDaemon => IsSystemTarget;

	/// <summary>Register the logon daemon (and, when elevated, elevator) tasks.</summary>
	public bool RegisterUserTasks => IsWindows && !IsSystemTarget;

	/// <summary>
	/// Last-resort kill by process name. Name-based killing cannot tell owners
	/// apart, so an elevated SYSTEM pass must never do it — that would take
	/// down the invoking admin's own daemons.
	/// </summary>
	public bool HardKillByNameFallback => !IsSystemTarget;

	/// <summary>Print the "also remove the dotnet tool" hint.</summary>
	public bool SuggestBootstrapperRemoval => !IsSystemTarget;
}