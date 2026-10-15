using System;
using System.Linq;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Script scheduling, delegated to the installed sea binary (which owns the
/// Task Scheduler / crontab entries themselves).
/// </summary>
internal sealed class ScheduleCommands
{
	private readonly InstallTarget _target;

	public ScheduleCommands(InstallTarget target) => _target = target;

	public int Schedule(string[] scheduleArgs)
	{
		var seaPath = EnvironmentProbe.FindSea(_target.BinDir, _target.IsWindows);
		if (seaPath == null)
		{
			Console.Error.WriteLine("  Not installed. Run 'seashell install' first.");
			return 1;
		}

		if (scheduleArgs.Length == 0)
		{
			Console.WriteLine(EnvironmentProbe.RunProcess(seaPath, "--schedule-list").Trim());
			return 0;
		}

		var allArgs = "--schedule " + string.Join(" ", scheduleArgs.Select(QuoteArg));
		var output = EnvironmentProbe.RunProcess(seaPath, allArgs).Trim();
		if (!string.IsNullOrEmpty(output))
			Console.WriteLine(output);
		return 0;
	}

	public int Unschedule(string[] unschedArgs)
	{
		var seaPath = EnvironmentProbe.FindSea(_target.BinDir, _target.IsWindows);
		if (seaPath == null)
		{
			Console.Error.WriteLine("  Not installed.");
			return 1;
		}

		if (unschedArgs.Length == 0)
		{
			Console.Error.WriteLine("Usage: seashell unschedule <script.cs>");
			return 1;
		}

		var output = EnvironmentProbe.RunProcess(seaPath, $"--unschedule {QuoteArg(unschedArgs[0])}").Trim();
		if (!string.IsNullOrEmpty(output))
			Console.WriteLine(output);
		return 0;
	}

	private static string QuoteArg(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}