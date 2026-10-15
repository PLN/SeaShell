using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class SeaShellTaskNamesTests
{
	[Theory]
	[InlineData(@"\SeaShell\SeaShell Daemon (SYSTEM) 0.6.1.257", "SYSTEM", true)]
	[InlineData(@"\SeaShell\SeaShell Elevator (SYSTEM) 0.6.1.257", "SYSTEM", true)]
	[InlineData(@"\SeaShell\SeaShell Daemon (qd) 0.6.1.257", "qd", true)]
	[InlineData(@"\SeaShell\SeaShell Daemon (qd)", "qd", true)]              // pre-0.6.0, no version
	[InlineData("SeaShell Daemon (qd) 0.6.1.257", "qd", true)]               // no folder prefix
	[InlineData(@"\SeaShell\SeaShell Daemon (qd) 0.6.1.257", "SYSTEM", false)]
	[InlineData(@"\SeaShell\SeaShell Elevator (qd) 0.6.1.257", "SYSTEM", false)]
	[InlineData(@"\SeaShell\SeaShell Daemon (qd) 0.6.1.257", "QD", false)]   // identity is case-sensitive
	[InlineData(@"\SeaShell\SomeOtherTool (qd)", "qd", false)]
	[InlineData(@"\SeaShell\SeaShell Daemon Helper", "qd", false)]           // not a daemon/elevator task
	public void MatchesTarget_FindsOnlyThisTargetsTasks(string taskPath, string targetDisplayName, bool expected)
	{
		Assert.Equal(expected, SeaShellTaskNames.MatchesTarget(taskPath, targetDisplayName));
	}

	[Theory]
	[InlineData(@"\SeaShell\SeaShell Daemon (qd) 0.6.1.257", "SeaShell Daemon (qd) 0.6.1.257")]
	[InlineData("SeaShell Daemon (qd) 0.6.1.257", "SeaShell Daemon (qd) 0.6.1.257")]
	public void NameOf_StripsTheFolderPrefix(string taskPath, string expected)
	{
		Assert.Equal(expected, SeaShellTaskNames.NameOf(taskPath));
	}

	[Fact]
	public void IsSeaShellTask_IgnoresForeignTasks()
	{
		Assert.True(SeaShellTaskNames.IsSeaShellTask(@"\SeaShell\SeaShell Elevator (SYSTEM) 0.6.1.257"));
		Assert.False(SeaShellTaskNames.IsSeaShellTask(@"\SeaShell\Backup (SYSTEM) 0.6.1.257"));
	}
}