using SeaShell.Installer;
using Xunit;

namespace EngineTest;

/// <summary>
/// The user-profile vs SYSTEM step matrix. A SYSTEM pass must never write the
/// invoking administrator's HKCU (PATH, file associations) nor kill the
/// invoking administrator's daemons.
/// </summary>
public class InstallScopeTests
{
	private static readonly InstallScope UserOnWindows = new(true, false);
	private static readonly InstallScope SystemOnWindows = new(true, true);
	private static readonly InstallScope UserOnLinux = new(false, false);

	[Fact]
	public void UserPassOnWindows_OwnsTheUserProfileSteps()
	{
		Assert.True(UserOnWindows.EditUserProfilePath);
		Assert.True(UserOnWindows.EditFileAssociations);
		Assert.True(UserOnWindows.StripLegacyBinFromUserPath);
		Assert.True(UserOnWindows.CleanLegacyCommonInstall);
		Assert.True(UserOnWindows.RefreshScheduledTaskPaths);
		Assert.True(UserOnWindows.RegisterUserTasks);
		Assert.True(UserOnWindows.HardKillByNameFallback);
		Assert.True(UserOnWindows.SuggestBootstrapperRemoval);

		Assert.False(UserOnWindows.RegisterSystemDaemon);
		Assert.False(UserOnWindows.SweepLegacySystemArtifacts);
	}

	[Fact]
	public void SystemPassOnWindows_OwnsNoUserProfileStep()
	{
		Assert.False(SystemOnWindows.EditUserProfilePath);
		Assert.False(SystemOnWindows.EditFileAssociations);
		Assert.False(SystemOnWindows.StripLegacyBinFromUserPath);
		Assert.False(SystemOnWindows.CleanLegacyCommonInstall);
		Assert.False(SystemOnWindows.RefreshScheduledTaskPaths);
		Assert.False(SystemOnWindows.RegisterUserTasks);
		Assert.False(SystemOnWindows.HardKillByNameFallback);
		Assert.False(SystemOnWindows.SuggestBootstrapperRemoval);

		Assert.True(SystemOnWindows.RegisterSystemDaemon);
		Assert.True(SystemOnWindows.SweepLegacySystemArtifacts);
	}

	[Fact]
	public void UserPassOnLinux_SkipsWindowsOnlySteps_ButStillRefreshesSchedules()
	{
		Assert.False(UserOnLinux.EditFileAssociations);
		Assert.False(UserOnLinux.StripLegacyBinFromUserPath);
		Assert.False(UserOnLinux.RegisterUserTasks);

		Assert.True(UserOnLinux.EditUserProfilePath);      // the ~/.local/bin symlink
		Assert.True(UserOnLinux.RefreshScheduledTaskPaths); // crontab
		Assert.True(UserOnLinux.HardKillByNameFallback);
	}
}