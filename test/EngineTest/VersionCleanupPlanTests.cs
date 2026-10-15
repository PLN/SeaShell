using System;
using System.Linq;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class VersionCleanupPlanTests
{
	private static (Version, string) Dir(string version) => (new Version(version), $"/data/v{version}");

	[Fact]
	public void SelectsOnlyVersionsOlderThanTheInstaller_OldestFirst()
	{
		var installed = new[]
		{
			Dir("0.6.0.100"),
			Dir("0.4.0.50"),
			Dir("0.6.2.279"),
			Dir("0.5.0.75"),
		};

		var selected = VersionCleanupPlan.SelectOldestFirst(installed, new Version("0.6.2.279"));

		Assert.Equal(
			new[] { new Version("0.4.0.50"), new Version("0.5.0.75"), new Version("0.6.0.100") },
			selected.Select(v => v.Version).ToArray());
	}

	[Fact]
	public void CurrentAndNewerVersions_AreNeverCandidates()
	{
		var installed = new[] { Dir("0.7.0.1"), Dir("0.6.2.279") };

		Assert.Empty(VersionCleanupPlan.SelectOldestFirst(installed, new Version("0.6.2.279")));
	}

	[Fact]
	public void UnparseableInstallerVersion_DisablesCleanup()
	{
		var installed = new[] { Dir("0.1.0.1") };

		Assert.Empty(VersionCleanupPlan.SelectOldestFirst(installed, null));
	}

	[Theory]
	[InlineData(DaemonProbeState.NoPipeMarker, VersionCleanupAction.Remove)]
	[InlineData(DaemonProbeState.Idle, VersionCleanupAction.StopThenRemove)]
	[InlineData(DaemonProbeState.Unreachable, VersionCleanupAction.KillStaleThenRemove)]
	[InlineData(DaemonProbeState.Busy, VersionCleanupAction.SkipInUse)]
	public void ProbeState_DecidesHowFarCleanupMayGo(DaemonProbeState state, VersionCleanupAction expected)
	{
		Assert.Equal(expected, VersionCleanupPlan.Decide(state));
	}
}