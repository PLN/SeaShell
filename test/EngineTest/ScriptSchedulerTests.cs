using System;
using Xunit;
using SeaShell.Invoker;
using static SeaShell.Invoker.ScriptScheduler;

namespace SeaShell.Engine.Tests;

public class ScriptSchedulerTests
{
	// ── Special triggers ───────────────────────────────────────────

	[Fact]
	public void ParseTiming_Reboot()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "@reboot" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Reboot, spec!.Kind);
	}

	[Fact]
	public void ParseTiming_Logon()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "@logon" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Logon, spec!.Kind);
	}

	// ── Daily ──────────────────────────────────────────────────────

	[Fact]
	public void ParseTiming_Daily_NoTime()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "daily" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Daily, spec!.Kind);
		Assert.Equal(0, spec.Hour);
		Assert.Equal(0, spec.Minute);
	}

	[Fact]
	public void ParseTiming_Daily_WithTime()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "daily", "at", "09:30" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Daily, spec!.Kind);
		Assert.Equal(9, spec.Hour);
		Assert.Equal(30, spec.Minute);
	}

	// ── Hourly ─────────────────────────────────────────────────────

	[Fact]
	public void ParseTiming_Hourly_NoMinute()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "hourly" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Hourly, spec!.Kind);
		Assert.Equal(0, spec.Minute);
	}

	[Fact]
	public void ParseTiming_Hourly_AtMinute()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "hourly", "at", ":15" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Hourly, spec!.Kind);
		Assert.Equal(15, spec.Minute);
	}

	// ── Weekly ─────────────────────────────────────────────────────

	[Fact]
	public void ParseTiming_Weekly_Default()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "weekly" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Weekly, spec!.Kind);
		Assert.Equal(DayOfWeek.Sunday, spec.DaysOfWeek![0]);
		Assert.Equal(0, spec.Hour);
	}

	[Fact]
	public void ParseTiming_Weekly_SingleDay()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "weekly", "on", "mon" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Weekly, spec!.Kind);
		Assert.Single(spec.DaysOfWeek!);
		Assert.Equal(DayOfWeek.Monday, spec.DaysOfWeek![0]);
	}

	[Fact]
	public void ParseTiming_Weekly_SingleDay_WithTime()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "weekly", "on", "mon", "at", "08:00" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Weekly, spec!.Kind);
		Assert.Equal(DayOfWeek.Monday, spec.DaysOfWeek![0]);
		Assert.Equal(8, spec.Hour);
		Assert.Equal(0, spec.Minute);
	}

	[Fact]
	public void ParseTiming_Weekly_MultipleDays()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "weekly", "on", "mon,wed,fri", "at", "08:00" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Weekly, spec!.Kind);
		Assert.Equal(3, spec.DaysOfWeek!.Length);
		Assert.Equal(DayOfWeek.Monday, spec.DaysOfWeek![0]);
		Assert.Equal(DayOfWeek.Wednesday, spec.DaysOfWeek![1]);
		Assert.Equal(DayOfWeek.Friday, spec.DaysOfWeek![2]);
		Assert.Equal(8, spec.Hour);
	}

	[Fact]
	public void ParseTiming_Weekly_MultipleDays_WithInterval()
	{
		var spec = ScriptScheduler.ParseTiming(new[] {
			"weekly", "on", "mon,wed,fri", "every", "5", "minutes", "at", "01:23", "for", "17", "hours"
		});
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Weekly, spec!.Kind);
		Assert.Equal(3, spec.DaysOfWeek!.Length);
		Assert.Equal(5, spec.IntervalValue);
		Assert.True(spec.IntervalMinutes);
		Assert.Equal(1, spec.Hour);
		Assert.Equal(23, spec.Minute);
		Assert.Equal(17, spec.DurationHours);
	}

	// ── Interval ───────────────────────────────────────────────────

	[Fact]
	public void ParseTiming_Every5Minutes()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "every", "5", "minutes" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Interval, spec!.Kind);
		Assert.Equal(5, spec.IntervalValue);
		Assert.True(spec.IntervalMinutes);
	}

	[Fact]
	public void ParseTiming_Every2Hours()
	{
		var spec = ScriptScheduler.ParseTiming(new[] { "every", "2", "hours" });
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Interval, spec!.Kind);
		Assert.Equal(2, spec.IntervalValue);
		Assert.False(spec.IntervalMinutes);
	}

	[Fact]
	public void ParseTiming_Every5Minutes_WithTimeAndDuration()
	{
		var spec = ScriptScheduler.ParseTiming(new[] {
			"every", "5", "minutes", "at", "01:23", "for", "17", "hours"
		});
		Assert.NotNull(spec);
		Assert.Equal(ScheduleKind.Interval, spec!.Kind);
		Assert.Equal(5, spec.IntervalValue);
		Assert.True(spec.IntervalMinutes);
		Assert.Equal(1, spec.Hour);
		Assert.Equal(23, spec.Minute);
		Assert.Equal(17, spec.DurationHours);
	}

	// ── Edge cases ─────────────────────────────────────────────────

	[Fact]
	public void ParseTiming_Empty_ReturnsNull()
	{
		Assert.Null(ScriptScheduler.ParseTiming(Array.Empty<string>()));
	}

	[Fact]
	public void ParseTiming_Unknown_ReturnsNull()
	{
		Assert.Null(ScriptScheduler.ParseTiming(new[] { "biweekly" }));
	}

	// ── Day parsing ────────────────────────────────────────────────

	[Fact]
	public void ParseDays_Numbers()
	{
		var days = ScriptScheduler.ParseDays("1,3,5");
		Assert.NotNull(days);
		Assert.Equal(3, days!.Length);
		Assert.Equal(DayOfWeek.Monday, days[0]);
		Assert.Equal(DayOfWeek.Wednesday, days[1]);
		Assert.Equal(DayOfWeek.Friday, days[2]);
	}

	[Fact]
	public void ParseDays_Invalid_ReturnsNull()
	{
		Assert.Null(ScriptScheduler.ParseDays("xyz"));
	}

	// ── FormatSpec ─────────────────────────────────────────────────

	[Fact]
	public void FormatSpec_DailyAt()
	{
		var spec = new ScheduleSpec(ScheduleKind.Daily, 9, 30);
		Assert.Equal("daily at 09:30", ScriptScheduler.FormatSpec(spec));
	}

	[Fact]
	public void FormatSpec_WeeklyMultipleDays()
	{
		var spec = new ScheduleSpec(ScheduleKind.Weekly, 8, 0,
			new[] { DayOfWeek.Monday, DayOfWeek.Friday });
		Assert.Equal("weekly on mon,fri at 08:00", ScriptScheduler.FormatSpec(spec));
	}
}
