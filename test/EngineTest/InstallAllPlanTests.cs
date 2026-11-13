using System.Linq;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class InstallAllPlanTests
{
	private const string Invoker = "S-1-5-21-1-2-3-1001";
	private const string SameAccount = "s-1-5-21-1-2-3-1001";   // same token, different case
	private const string OtherAccount = "S-1-5-21-1-2-3-500";

	private const ScopeParts All = ScopeParts.User | ScopeParts.Elevator | ScopeParts.System;

	[Fact]
	public void NonElevated_ScopeWithElevatedParts_RelaunchesAndRunsNoPassesItself()
	{
		var plan = InstallAllPlanner.Plan(All, isElevated: false, invokerToken: null, elevatedToken: Invoker, opportunisticElevator: true);

		Assert.True(plan.RelaunchElevated);
		Assert.Empty(plan.Passes);
	}

	[Fact]
	public void NonElevated_UserOnlyScope_InstallsWithoutAPrompt()
	{
		// A remembered user-only scope keeps the 0.6.2 experience: no UAC.
		var plan = InstallAllPlanner.Plan(ScopeParts.User, isElevated: false, invokerToken: null, elevatedToken: Invoker, opportunisticElevator: true);

		Assert.False(plan.RelaunchElevated);
		Assert.Equal(new[] { InstallPassScope.User }, plan.Passes.ToArray());
		Assert.False(plan.RegisterElevator);
	}

	[Fact]
	public void Elevated_FullScope_InstallsUserWithElevatorFirstThenSystem()
	{
		var plan = InstallAllPlanner.Plan(All, isElevated: true, invokerToken: Invoker, elevatedToken: SameAccount, opportunisticElevator: false);

		Assert.False(plan.RelaunchElevated);
		Assert.False(plan.SkipUserParts);
		Assert.True(plan.RegisterElevator);
		Assert.Equal(new[] { InstallPassScope.User, InstallPassScope.System }, plan.Passes.ToArray());
	}

	[Theory]
	[InlineData(true, true)]    // no scope flag: an elevated console registers the elevator, as 0.6.2 did
	[InlineData(false, false)]  // --user / --pass user: exactly what was asked for
	public void Elevated_UserOnlyScope_ElevatorOnlyWhenNoScopeWasRequested(bool opportunistic, bool expected)
	{
		var plan = InstallAllPlanner.Plan(ScopeParts.User, isElevated: true, invokerToken: null, elevatedToken: Invoker, opportunisticElevator: opportunistic);

		Assert.Equal(new[] { InstallPassScope.User }, plan.Passes.ToArray());
		Assert.Equal(expected, plan.RegisterElevator);
	}

	[Fact]
	public void OverTheShoulderUac_HelperInstallsSystemOnly_ThenTheInvokerRunsTheUserPass()
	{
		var plan = InstallAllPlanner.Plan(All, isElevated: true, invokerToken: Invoker, elevatedToken: OtherAccount, opportunisticElevator: false);

		Assert.True(plan.SkipUserParts);
		Assert.False(plan.RegisterElevator);
		Assert.Equal(new[] { InstallPassScope.System }, plan.Passes.ToArray());

		// The helper reports the skip; the original process installs the user part.
		var helper = new InstallRunSummary { UserSkipped = true };
		helper.Add(ScopeParts.System, PassResult.Installed);
		Assert.True(InstallAllPlanner.RunUserPassAfterRelaunch(All, helper));
	}

	[Fact]
	public void SameAccountHelper_DidTheUserPass_InvokerDoesNotRepeatIt()
	{
		var helper = new InstallRunSummary();
		helper.Add(ScopeParts.User | ScopeParts.Elevator | ScopeParts.System, PassResult.Installed);

		Assert.False(InstallAllPlanner.RunUserPassAfterRelaunch(All, helper));
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

	[Fact]
	public void Linux_UserWithRootScope_UserPassHereThenRootPassThroughSudo()
	{
		var plan = InstallAllPlanner.PlanLinux(ScopeParts.User | ScopeParts.System, isRoot: false);

		Assert.True(plan.UserPass);
		Assert.True(plan.SudoSystemPass);
		Assert.False(plan.RootPassOnly);
	}

	[Fact]
	public void Linux_InvokedAsRoot_OnlyTheRootPass()
	{
		var plan = InstallAllPlanner.PlanLinux(ScopeParts.User | ScopeParts.System, isRoot: true);

		Assert.True(plan.RootPassOnly);
		Assert.False(plan.UserPass);
		Assert.False(plan.SudoSystemPass);
	}
}
