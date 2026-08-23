using System;

namespace SeaShell.Installer;

/// <summary>Version-selection policy shared by installer entry points.</summary>
public static class InstallVersionPolicy
{
	/// <summary>
	/// Returns <see langword="true"/> when running this installer would be a
	/// downgrade of the target's active installation.
	/// </summary>
	public static bool WouldDowngrade(Version? currentVersion, Version installerVersion) =>
		currentVersion != null && currentVersion > installerVersion;
}
