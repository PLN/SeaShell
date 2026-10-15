using System;
using System.IO;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class LegacyInstallPathsTests
{
	private const string Root = "/ProgramData/seashell";

	[Theory]
	[InlineData("/ProgramData/seashell", true)]
	[InlineData("/PROGRAMDATA/SeaShell", true)]
	[InlineData("/ProgramData/seashell-extra", false)]
	[InlineData("/ProgramData/other", false)]
	[InlineData("", false)]
	[InlineData("   ", false)]
	public void IsUnderRoot_MatchesTheRootItselfButNotNamePrefixes(string pathEntry, bool expected)
	{
		Assert.Equal(expected, LegacyInstallPaths.IsUnderRoot(pathEntry, Root));
	}

	[Fact]
	public void IsUnderRoot_MatchesDescendantsAndTrailingSeparators()
	{
		Assert.True(LegacyInstallPaths.IsUnderRoot(Root + Path.DirectorySeparatorChar, Root));
		Assert.True(LegacyInstallPaths.IsUnderRoot(
			Root + Path.DirectorySeparatorChar + "bin", Root));
	}

	[Fact]
	public void IsUnderRoot_WithoutARoot_NeverMatches()
	{
		// CommonApplicationData can be unresolvable; that must not match everything.
		Assert.False(LegacyInstallPaths.IsUnderRoot(Root, null));
		Assert.False(LegacyInstallPaths.IsUnderRoot(Root, ""));
	}

	[Fact]
	public void IsUnderRoot_ExpandsUnexpandedPathEntries()
	{
		if (!OperatingSystem.IsWindows()) return; // %VAR% syntax

		const string name = "SEASHELL_TEST_PATHPROOT";
		Environment.SetEnvironmentVariable(name, Root);
		try
		{
			var entry = $"%{name}%" + Path.DirectorySeparatorChar + "bin";

			Assert.True(LegacyInstallPaths.IsUnderRoot(entry, Root));
		}
		finally
		{
			Environment.SetEnvironmentVariable(name, null);
		}
	}
}