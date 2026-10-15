using System;
using System.IO;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class VerificationRulesTests
{
	private static string VersionDir(string version) =>
		Path.Combine(Path.GetTempPath(), "seashell-verify", $"v{version}");

	[Fact]
	public void Junction_NormalizesTrailingSeparatorAndCase()
	{
		var versionDir = VersionDir("0.6.2.279");

		Assert.True(VerificationRules.JunctionPointsToVersion(
			versionDir + Path.DirectorySeparatorChar, versionDir));
		Assert.True(VerificationRules.JunctionPointsToVersion(
			versionDir.ToUpperInvariant(), versionDir));
	}

	[Fact]
	public void Junction_MissingOrPointingElsewhere_Fails()
	{
		Assert.False(VerificationRules.JunctionPointsToVersion(null, VersionDir("0.6.2.279")));
		Assert.False(VerificationRules.JunctionPointsToVersion(
			VersionDir("0.6.2.278"), VersionDir("0.6.2.279")));
	}

	[Theory]
	[InlineData("SeaShell v0.6.2.279", "0.6.2.279", true)]
	[InlineData("SeaShell v0.6.2.278", "0.6.2.279", false)]
	[InlineData("", "0.6.2.279", false)]
	public void SeaVersionOutput_MustNameTheInstalledVersion(string output, string version, bool expected)
	{
		Assert.Equal(expected, VerificationRules.SeaReportsVersion(output, version));
	}

	[Fact]
	public void FileAssociation_MustPointAtTheExtractedBinary()
	{
		const string exe = @"C:\data\current\bin\sea.exe";

		Assert.True(VerificationRules.FileAssociationPointsTo(
			$"\"{exe.ToUpperInvariant()}\" \"%1\"", exe));
		Assert.False(VerificationRules.FileAssociationPointsTo(@"""C:\other\sea.exe"" ""%1""", exe));
		Assert.False(VerificationRules.FileAssociationPointsTo("", exe));
	}

	[Fact]
	public void DaemonPing_MustComeFromTheVersionWeJustStarted()
	{
		// A stale pipe from a same-version reinstall answers with the old
		// daemon's version; anything but our own version is a failure.
		Assert.True(VerificationRules.DaemonReportsVersion("0.6.2.279", "0.6.2.279"));
		Assert.False(VerificationRules.DaemonReportsVersion("0.6.2.278", "0.6.2.279"));
		Assert.False(VerificationRules.DaemonReportsVersion(null, "0.6.2.279"));
	}
}