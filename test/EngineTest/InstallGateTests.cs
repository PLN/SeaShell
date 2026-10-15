using System;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class InstallGateTests
{
	private static readonly Version Installer = new(0, 6, 2, 279);

	[Fact]
	public void NoActiveVersion_Proceeds()
	{
		Assert.Equal(InstallGateOutcome.Proceed, InstallGate.Evaluate(null, Installer.ToString()).Outcome);
	}

	[Fact]
	public void SameVersionAsActive_Proceeds()
	{
		// Reinstalling/repairing the version that is already current is allowed.
		Assert.Equal(InstallGateOutcome.Proceed, InstallGate.Evaluate(Installer, Installer.ToString()).Outcome);
	}

	[Fact]
	public void NewerInstallerThanActive_Proceeds()
	{
		var decision = InstallGate.Evaluate(new Version(0, 5, 0, 99), Installer.ToString());

		Assert.Equal(InstallGateOutcome.Proceed, decision.Outcome);
	}

	[Fact]
	public void OlderInstallerThanActive_SkipsAndNamesTheActiveVersion()
	{
		var active = new Version(0, 7, 0, 10);

		var decision = InstallGate.Evaluate(active, Installer.ToString());

		Assert.Equal(InstallGateOutcome.SkipNewerVersionActive, decision.Outcome);
		Assert.Equal(active, decision.ActiveVersion);
	}

	[Fact]
	public void UnparseableInstallerVersion_ProceedsRatherThanSkipping()
	{
		// Nothing can be compared, so the install must not be silently skipped.
		var decision = InstallGate.Evaluate(new Version(9, 9, 9, 9), "not-a-version");

		Assert.Equal(InstallGateOutcome.Proceed, decision.Outcome);
		Assert.Equal(new Version(9, 9, 9, 9), decision.ActiveVersion);
	}
}