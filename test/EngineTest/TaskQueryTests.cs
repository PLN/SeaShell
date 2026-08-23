using System;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class TaskQueryTests
{
	[Theory]
	[InlineData(@"\SeaShell\SeaShell Daemon (SYSTEM) 0.6.1.257")]
	[InlineData("SeaShell Daemon (qd) 0.6.1.257")]
	public void ParseVersionSuffix_ReturnsVersion(string taskPath)
	{
		Assert.Equal(new Version(0, 6, 1, 257), TaskQuery.ParseVersionSuffix(taskPath));
	}

	[Theory]
	[InlineData(@"\SeaShell\SeaShell Daemon (SYSTEM)")]
	[InlineData("")]
	public void ParseVersionSuffix_InvalidOrMissingVersion_ReturnsNull(string taskPath)
	{
		Assert.Null(TaskQuery.ParseVersionSuffix(taskPath));
	}
}
