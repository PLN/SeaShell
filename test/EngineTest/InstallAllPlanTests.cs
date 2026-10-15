using System.Linq;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class InstallAllPlanTests
{
	private const string Invoker = "S-1-5-21-1-2-3-1001";
	private const string SameAccount = "s-1-5-21-1-2-3-1001";   // same token, different case
	private const string OtherAccount = "S-1-5-21-1-2-3-500";

	[Fact]
	public void NonElevatedCaller_RelaunchesAndRunsNoPassesItself()
	{
		var plan = InstallAllPlanner.Plan(isElevated: false, invokerToken: null, elevatedToken: Invoker);

		Assert.True(plan.RelaunchElevated);
		Assert.Empty(plan.Passes);
	}

	[Fact]
	public void ElevatedCaller_InstallsUserFirstThenSystem()
	{
		var plan = InstallAllPlanner.Plan(isElevated: true, invokerToken: Invoker, elevatedToken: SameAccount);

		Assert.False(plan.RelaunchElevated);
		Assert.False(plan.SkipUserParts);
		Assert.Equal(
			new[] { InstallPassScope.User, InstallPassScope.System },
			plan.Passes.ToArray());
	}

	[Fact]
	public void NoInvokerToken_AssumedToBeTheElevatedAccount()
	{
		var plan = InstallAllPlanner.Plan(isElevated: true, invokerToken: null, elevatedToken: Invoker);

		Assert.False(plan.SkipUserParts);
		Assert.Equal(2, plan.Passes.Count);
	}

	[Fact]
	public void OverTheShoulderUac_InstallsSystemOnly()
	{
		var plan = InstallAllPlanner.Plan(isElevated: true, invokerToken: Invoker, elevatedToken: OtherAccount);

		Assert.True(plan.SkipUserParts);
		Assert.Equal(new[] { InstallPassScope.System }, plan.Passes.ToArray());
	}

	[Theory]
	[InlineData(null, Invoker, false)]      // no invoker recorded -> same account
	[InlineData(Invoker, Invoker, false)]
	[InlineData(Invoker, SameAccount, false)] // SID comparison is case-insensitive
	[InlineData(Invoker, OtherAccount, true)]
	public void SkipUserParts_OnlyWhenTheAccountsDiffer(string? invoker, string elevated, bool expected)
	{
		Assert.Equal(expected, InstallAllPlanner.ShouldSkipUserParts(invoker, elevated));
	}

	[Theory]
	[InlineData(false, true)]  // the user pass installed something -> start the chain
	[InlineData(true, false)]  // it skipped (newer version active) -> leave it alone
	public void UserServiceChain_StartsOnlyWhenTheUserPassInstalledSomething(bool skippedNewerVersion, bool expected)
	{
		Assert.Equal(expected, InstallAllPlanner.ShouldStartUserServiceChain(skippedNewerVersion));
	}

	[Theory]
	[InlineData(true, true, true)]
	[InlineData(true, false, false)]  // --all on Linux is an ordinary single pass
	[InlineData(false, true, false)]
	public void CompositeInstall_IsWindowsOnly(bool allMode, bool isWindows, bool expected)
	{
		Assert.Equal(expected, InstallAllPlanner.IsCompositeInstall(allMode, isWindows));
	}
}