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
	public void Advice_ComparesInstalledAgainstBootstrapper(bool installed, string? installedVersion, StatusAdvice expected)
	{
		Assert.Equal(expected, StatusRules.Advise(installed, installedVersion, "0.6.2.279"));
	}
}