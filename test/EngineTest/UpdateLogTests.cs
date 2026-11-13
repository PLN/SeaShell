using System;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

/// <summary>
/// update.log: the record of scheduled updates that does not depend on the Event
/// Log source (review finding: a failed run of a user-only install left no trace).
/// </summary>
public class UpdateLogTests
{
	[Fact]
	public void StatusFindsTheLastRun_WithItsExitCode()
	{
		var time = UpdateLog.Timestamp(new DateTimeOffset(2026, 11, 12, 13, 5, 0, TimeSpan.Zero));
		var log = UpdateLog.Entry(time, 0, "nothing", "seashell.exe update --pass user --if-idle --quiet", "SeaShell up to date", "")
			+ UpdateLog.Entry(time.AddDays(1), 1, "failed", "seashell.exe update --pass user --if-idle --quiet --source C:\\nope",
				"SeaShell update failed: could not fetch the package", "  ERROR: could not fetch\n=== not a header\n");

		var last = UpdateLog.LastRun(log)!;
		Assert.Equal(1, last.ExitCode);
		Assert.Equal("failed", last.Outcome);
		Assert.Equal(time.AddDays(1), last.Time);
		Assert.Null(UpdateLog.LastRun(""));
	}
}
