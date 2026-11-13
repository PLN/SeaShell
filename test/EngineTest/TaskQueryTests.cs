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

	private static readonly string[] NotFound =
	{
		"The system cannot find the file specified.",
		"The system cannot find the path specified.",
	};

	[Fact]
	public void ClassifyQuery_DistinguishesAbsentFromCouldNotLook()
	{
		Assert.Equal(PartPresence.Present, TaskQuery.ClassifyQuery(0, "", NotFound));
		Assert.Equal(PartPresence.Absent, TaskQuery.ClassifyQuery(1, "ERROR: The system cannot find the file specified.\r\n", NotFound));
		Assert.Equal(PartPresence.Absent, TaskQuery.ClassifyQuery(1, "ERROR: The system cannot find the path specified.\r\n", NotFound));
		// A denied query is not "not registered".
		Assert.Equal(PartPresence.Unknown, TaskQuery.ClassifyQuery(1, "ERROR: Access is denied.\r\n", NotFound));
		Assert.Equal(PartPresence.Unknown, TaskQuery.ClassifyQuery(-1, "", NotFound));
	}
}
