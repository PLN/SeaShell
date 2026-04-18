using System;
using Xunit;

namespace EngineTest;

public class VersionDiscoveryTests
{
	[Fact]
	public void ParseVersionDir_Valid_ReturnsVersion()
	{
		var v = SeaShell.VersionDiscovery.ParseVersionDir("v0.5.0.222");
		Assert.NotNull(v);
		Assert.Equal(new Version(0, 5, 0, 222), v);
	}

	[Fact]
	public void ParseVersionDir_WithoutBuild_ReturnsVersion()
	{
		var v = SeaShell.VersionDiscovery.ParseVersionDir("v1.2.3");
		Assert.NotNull(v);
		Assert.Equal(new Version(1, 2, 3), v);
	}

	[Fact]
	public void ParseVersionDir_WithoutLeadingV_ReturnsNull()
	{
		Assert.Null(SeaShell.VersionDiscovery.ParseVersionDir("0.5.0.222"));
	}

	[Fact]
	public void ParseVersionDir_GibberishReturnsNull()
	{
		Assert.Null(SeaShell.VersionDiscovery.ParseVersionDir("foo"));
		Assert.Null(SeaShell.VersionDiscovery.ParseVersionDir("v"));
		Assert.Null(SeaShell.VersionDiscovery.ParseVersionDir(""));
		Assert.Null(SeaShell.VersionDiscovery.ParseVersionDir("vNext"));
	}
}
