using System;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class StatusRulesTests
{
	[Theory]
	[InlineData("SeaShell v0.6.2.279", "0.6.2.279")]
	[InlineData("v0.6.2.279", "0.6.2.279")]
	[InlineData("0.6.2.279", "0.6.2.279")]                 // no marker: pass through
	[InlineData("SeaShell v0.6.2.279 (x64)", "0.6.2.279 (x64)")] // last marker wins
	public void ParseInstalledVersion_ReadsTheVersionFromSea(string output, string expected)
	{
		Assert.Equal(expected, StatusRules.ParseInstalledVersion(output));
	}

	[Theory]
	[InlineData("daemon: 0.6.2.279, up 3.01:00:00, 0 active", true)]
	[InlineData("daemon: not running", false)]
	[InlineData("", false)]
	public void DaemonRunning_IsReadFromTheStatusLine(string output, bool expected)
	{
		Assert.Equal(expected, StatusRules.DaemonReportsRunning(output));
	}
	[Theory]
	[InlineData("0.6.2.279", "0.6.2.279", false)]
	[InlineData("0.6.2.279", "0.6.1.250", true)]
	[InlineData(null, "0.6.1.250", false)]     // current unknown: cannot claim drift
	[InlineData("0.6.2.279", null, false)]     // task unversioned: nothing to compare
	public void SystemTaskDrift_IsOnlyReportedWhenBothSidesAreKnown(string? current, string? task, bool expected)
	{
		Assert.Equal(expected, StatusRules.TaskVersionDrifts(
			current == null ? null : new Version(current),
			task == null ? null : new Version(task)));
	}

	[Theory]
	[InlineData(false, null, StatusAdvice.NotInstalled)]
	[InlineData(false, "0.6.2.279", StatusAdvice.NotInstalled)]
	[InlineData(true, "0.6.2.279", StatusAdvice.UpToDate)]
	[InlineData(true, null, StatusAdvice.UpToDate)]
	[InlineData(true, "0.6.1.250", StatusAdvice.UpdateAvailable)]
	[InlineData(true, "0.6.2.300", StatusAdvice.BootstrapperOlder)]   // E2E defect 5: offered a downgrade
	[InlineData(true, "0.6.2.279.0", StatusAdvice.UpToDate)]         // compared as versions, not strings
	public void Advice_ComparesInstalledAgainstBootstrapper(bool installed, string? installedVersion, StatusAdvice expected)
	{
		Assert.Equal(expected, StatusRules.Advise(installed, installedVersion, "0.6.2.279"));
	}

	private static readonly DateTimeOffset CheckedAt = new(2026, 11, 12, 11, 40, 0, TimeSpan.Zero);

	[Fact]
	public void Dashboard_InstalledCopy_UsesTheDaemonsReleaseCheck()
	{
		// E2E defect 2: a successful update.check was ignored.
		var newer = new SeaShell.UpdateCheckInfo(new Version(0, 6, 3, 9012), CheckedAt);
		var same = new SeaShell.UpdateCheckInfo(new Version(0, 6, 3, 9011), CheckedAt);

		Assert.Equal(new[] { "Update available: v0.6.3.9012 (run 'seashell update')" },
			StatusRules.ClosingLines(true, "0.6.3.9011", "0.6.3.9011", carries: false, newer));
		Assert.Equal(new[] { $"Up to date (checked {CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm})" },
			StatusRules.ClosingLines(true, "0.6.3.9011", "0.6.3.9011", carries: false, same));
		Assert.Equal(new[] { "Run 'seashell update' to check online for a newer version" },
			StatusRules.ClosingLines(true, "0.6.3.9011", "0.6.3.9011", carries: false, check: null));
	}

	[Fact]
	public void Dashboard_OlderCarriedBootstrapper_OffersNoDowngrade()
	{
		var lines = StatusRules.ClosingLines(true, "0.6.3.9002", "0.6.3.9001", carries: true,
			new SeaShell.UpdateCheckInfo(new Version(0, 6, 2, 283), CheckedAt));

		Assert.Equal(new[] { "Installed v0.6.3.9002 is newer than this bootstrapper (v0.6.3.9001)" }, lines);
	}
}