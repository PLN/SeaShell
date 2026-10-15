using System;
using SeaShell;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

/// <summary>
/// The install target is the state <c>install --all</c> switches between
/// passes. Retargeting must produce a new snapshot and leave the pass it came
/// from pointing at its own directories.
/// </summary>
public class InstallTargetTests
{
	private const string Version = "0.6.2.279";

	[Fact]
	public void RetargetingToSystem_DoesNotDisturbTheUserPass()
	{
		if (!OperatingSystem.IsWindows() || SeaShellIdentity.Current.IsSystem) return;

		var user = new InstallTarget(SeaShellIdentity.Current, Version, isWindows: true);

		var system = user.WithIdentity(SeaShellIdentity.LocalSystem);

		Assert.NotEqual(user.DataDir, system.DataDir);
		Assert.True(system.IsSystemTarget);
		Assert.Equal(SeaShellPaths.VersionDirIn(system.DataDir, Version), system.VersionDir);
		Assert.Equal(SeaShellPaths.BinDirIn(system.DataDir, Version), system.BinDir);
		Assert.Equal(SeaShellPaths.CurrentDirIn(system.DataDir), system.CurrentDir);

		// The pass that was retargeted from still resolves to the user profile.
		Assert.Equal(SeaShellIdentity.Current.DataDir, user.DataDir);
		Assert.Equal(SeaShellPaths.VersionDirIn(user.DataDir, Version), user.VersionDir);
		Assert.False(user.IsSystemTarget);
	}

	[Fact]
	public void SystemTargetFlag_IsWindowsOnly()
	{
		// On Linux --system means "run as root": the ambient identity already
		// IS the target, and there is no separate LocalSystem profile to own
		// user-profile state.
		var linux = new InstallTarget(SeaShellIdentity.Current, Version, isWindows: false);

		Assert.False(linux.IsSystemTarget);
		Assert.False(linux.Scope.RegisterSystemDaemon);
		Assert.True(linux.Scope.EditUserProfilePath);
	}

	[Fact]
	public void Scope_FollowsTheTargetRatherThanTheHost()
	{
		if (!OperatingSystem.IsWindows()) return;

		var system = new InstallTarget(SeaShellIdentity.LocalSystem, Version, isWindows: true);

		Assert.False(system.Scope.HardKillByNameFallback);
		Assert.True(system.Scope.RegisterSystemDaemon);
	}
}