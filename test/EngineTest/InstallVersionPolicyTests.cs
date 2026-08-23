using System;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class InstallVersionPolicyTests
{
	private static readonly Version Installer = new(0, 6, 1, 257);

	[Fact]
	public void MissingCurrentVersion_IsNotDowngrade()
	{
		Assert.False(InstallVersionPolicy.WouldDowngrade(null, Installer));
	}

	[Theory]
	[InlineData(0, 6, 1, 256, false)]
	[InlineData(0, 6, 1, 257, false)]
	[InlineData(0, 6, 1, 259, true)]
	public void ComparesParsedVersionsNumerically(
		int major, int minor, int build, int revision, bool expected)
	{
		var current = new Version(major, minor, build, revision);

		Assert.Equal(expected, InstallVersionPolicy.WouldDowngrade(current, Installer));
	}
}
