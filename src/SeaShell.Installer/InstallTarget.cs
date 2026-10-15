using System;

namespace SeaShell.Installer;

/// <summary>
/// The install TARGET for a single pass: an identity, the bootstrapper version
/// being installed, and every path derived from that pair.
///
/// Immutable on purpose. <c>install --all</c> runs the install flow twice —
/// once for the current user, once for LocalSystem — and the old code did that
/// by mutating a handful of captured path variables. A pass now builds a new
/// <see cref="InstallTarget"/> with <see cref="WithIdentity"/>, so no step can
/// silently run against the previous pass's directories.
/// </summary>
public sealed record InstallTarget
{
	public InstallTarget(SeaShellIdentity identity, string version, bool isWindows)
	{
		Identity = identity;
		Version = version;
		IsWindows = isWindows;
		IsSystemTarget = isWindows && identity.IsSystem;
		Scope = new InstallScope(isWindows, IsSystemTarget);
	}

	/// <summary>The identity being installed for (current user, or LocalSystem).</summary>
	public SeaShellIdentity Identity { get; }

	/// <summary>Bootstrapper version being installed by this pass.</summary>
	public string Version { get; }

	/// <summary>Host platform.</summary>
	public bool IsWindows { get; }

	/// <summary>True when this pass targets LocalSystem (Windows only).</summary>
	public bool IsSystemTarget { get; }

	/// <summary>Which user-profile vs SYSTEM steps apply to this pass.</summary>
	public InstallScope Scope { get; }

	/// <summary>Human-readable identity name used in task names and log output.</summary>
	public string DisplayName => Identity.DisplayName;

	/// <summary>Root data directory of the target identity.</summary>
	public string DataDir => Identity.DataDir;

	/// <summary>Per-version directory for <see cref="Version"/> under the target.</summary>
	public string VersionDir => SeaShellPaths.VersionDirIn(DataDir, Version);

	/// <summary>Extracted binaries directory for this pass.</summary>
	public string BinDir => SeaShellPaths.BinDirIn(DataDir, Version);

	/// <summary>Compilation cache directory for this pass.</summary>
	public string CacheDir => SeaShellPaths.CacheDirIn(DataDir, Version);

	/// <summary>The <c>current</c> junction location of the target identity.</summary>
	public string CurrentDir => SeaShellPaths.CurrentDirIn(DataDir);

	/// <summary>
	/// Retarget this pass at another identity (the <c>install --all</c>
	/// user → SYSTEM switch). Everything path-derived follows from the new
	/// identity; nothing is shared with the previous pass.
	/// </summary>
	public InstallTarget WithIdentity(SeaShellIdentity identity) => new(identity, Version, IsWindows);
}