using System;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class GlobalToolPolicyTests
{
	private static readonly Version Installing = new(0, 6, 3, 300);

	[Fact]
	public void OlderGlobalTool_NotInUse_IsRemoved()
	{
		Assert.Equal(GlobalToolAction.Remove, GlobalToolPolicy.Decide(new Version(0, 6, 2, 283), Installing, inUseByThisRun: false));
	}

	[Fact]
	public void OlderGlobalTool_ThisRunExecutesFromIt_OnlyHints()
	{
		// e.g. `seashell update` started from the old global tool: it is still running.
		Assert.Equal(GlobalToolAction.Hint, GlobalToolPolicy.Decide(new Version(0, 6, 2, 283), Installing, inUseByThisRun: true));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("0.6.3.300")]   // `dotnet tool install -g SeaShell` + `seashell install`: the user's own choice
	[InlineData("0.6.4.1")]
	public void NoOrNotOlderGlobalTool_IsLeftAlone(string? global)
	{
		Assert.Equal(GlobalToolAction.None,
			GlobalToolPolicy.Decide(global == null ? null : Version.Parse(global), Installing, inUseByThisRun: false));
	}
}
