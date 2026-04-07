using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace SeaShell.Invoker;

/// <summary>
/// Schedules script execution via Task Scheduler (Windows) or crontab (Linux).
/// </summary>
public static class ScriptScheduler
{
	// ── Public API ─────────────────────────────────────────────────────

	/// <summary>Schedule a script with the given timing tokens.</summary>
	public static int Schedule(string scriptPath, string[] timingTokens, Action<string>? log = null)
	{
		if (!File.Exists(scriptPath))
		{
			log?.Invoke($"Script not found: {scriptPath}");
			return 1;
		}

		scriptPath = Path.GetFullPath(scriptPath);
		var spec = ParseTiming(timingTokens);
		if (spec == null)
		{
			log?.Invoke("Invalid timing expression.");
			log?.Invoke("Examples: @reboot, @logon, daily at 09:00, hourly, weekly on mon,fri at 08:00");
			log?.Invoke("          every 5 minutes, every 5 minutes at 01:00 for 8 hours");
			return 1;
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return ScheduleWindows(scriptPath, spec, log);
		else
			return ScheduleLinux(scriptPath, spec, log);
	}

	/// <summary>Remove a scheduled script.</summary>
	public static int Unschedule(string scriptPath, Action<string>? log = null)
	{
		scriptPath = Path.GetFullPath(scriptPath);
		var stem = Path.GetFileNameWithoutExtension(scriptPath);

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return UnscheduleWindows(stem, log);
		else
			return UnscheduleLinux(stem, log);
	}

	/// <summary>List all SeaShell-scheduled scripts.</summary>
	public static int List(Action<string>? log = null)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return ListWindows(log);
		else
			return ListLinux(log);
	}

	// ── Timing DSL ─────────────────────────────────────────────────────

	public enum ScheduleKind { Reboot, Logon, Daily, Hourly, Weekly, Interval }

	public sealed record ScheduleSpec(
		ScheduleKind Kind,
		int Hour = 0,
		int Minute = 0,
		DayOfWeek[]? DaysOfWeek = null,
		int IntervalValue = 0,
		bool IntervalMinutes = true,  // true = minutes, false = hours
		int DurationHours = 0         // 0 = all day
	);

	/// <summary>
	/// Parse timing tokens into a ScheduleSpec.
	/// Grammar:
	///   @reboot | @logon
	///   daily [at HH:MM]
	///   hourly [at :MM]
	///   weekly [on DAYS] [at HH:MM] [every N minutes|hours [at HH:MM] [for N hours]]
	///   every N minutes|hours [at HH:MM] [for N hours]
	/// </summary>
	public static ScheduleSpec? ParseTiming(string[] tokens)
	{
		if (tokens.Length == 0) return null;
		var i = 0;

		string Next() => i < tokens.Length ? tokens[i++].ToLowerInvariant() : "";
		string Peek() => i < tokens.Length ? tokens[i].ToLowerInvariant() : "";
		bool Has() => i < tokens.Length;

		var first = Next();

		// Special triggers
		if (first == "@reboot") return new ScheduleSpec(ScheduleKind.Reboot);
		if (first == "@logon") return new ScheduleSpec(ScheduleKind.Logon);

		// daily [at HH:MM]
		if (first == "daily")
		{
			var (h, m) = TryParseAt(ref i, tokens, 0, 0);
			return new ScheduleSpec(ScheduleKind.Daily, h, m);
		}

		// hourly [at :MM]
		if (first == "hourly")
		{
			var (_, m) = TryParseAt(ref i, tokens, 0, 0);
			return new ScheduleSpec(ScheduleKind.Hourly, 0, m);
		}

		// weekly [on DAYS] [at HH:MM] [every ...]
		if (first == "weekly")
		{
			DayOfWeek[]? days = null;
			if (Has() && Peek() == "on")
			{
				Next(); // consume "on"
				if (!Has()) return null;
				days = ParseDays(Next());
				if (days == null) return null;
			}
			days ??= new[] { System.DayOfWeek.Sunday };

			// Check for "every" (interval within weekly)
			if (Has() && Peek() == "every")
			{
				Next(); // consume "every"
				var interval = ParseInterval(ref i, tokens);
				if (interval == null) return null;
				return interval with { Kind = ScheduleKind.Weekly, DaysOfWeek = days };
			}

			var (h, m) = TryParseAt(ref i, tokens, 0, 0);
			return new ScheduleSpec(ScheduleKind.Weekly, h, m, days);
		}

		// every N minutes|hours [at HH:MM] [for N hours]
		if (first == "every")
			return ParseInterval(ref i, tokens);

		return null;
	}

	private static ScheduleSpec? ParseInterval(ref int i, string[] tokens)
	{
		if (i >= tokens.Length) return null;

		if (!int.TryParse(tokens[i], out var n) || n <= 0) return null;
		i++;

		if (i >= tokens.Length) return null;
		var unit = tokens[i].ToLowerInvariant();
		i++;

		bool isMinutes;
		if (unit is "minutes" or "minute" or "min")
			isMinutes = true;
		else if (unit is "hours" or "hour" or "hr")
			isMinutes = false;
		else
			return null;

		var (h, m) = TryParseAt(ref i, tokens, 0, 0);
		var duration = TryParseFor(ref i, tokens);

		return new ScheduleSpec(ScheduleKind.Interval, h, m,
			IntervalValue: n, IntervalMinutes: isMinutes, DurationHours: duration);
	}

	private static (int hour, int minute) TryParseAt(ref int i, string[] tokens, int defH, int defM)
	{
		if (i >= tokens.Length || tokens[i].ToLowerInvariant() != "at")
			return (defH, defM);
		i++; // consume "at"
		if (i >= tokens.Length) return (defH, defM);

		var time = tokens[i];
		i++;

		// :MM format (for hourly)
		if (time.StartsWith(':') && int.TryParse(time[1..], out var minuteOnly))
			return (defH, minuteOnly);

		// HH:MM format
		var parts = time.Split(':');
		if (parts.Length == 2 && int.TryParse(parts[0], out var hh) && int.TryParse(parts[1], out var mm))
			return (hh, mm);

		return (defH, defM);
	}

	private static int TryParseFor(ref int i, string[] tokens)
	{
		if (i >= tokens.Length || tokens[i].ToLowerInvariant() != "for")
			return 0;
		i++; // consume "for"
		if (i >= tokens.Length) return 0;

		if (!int.TryParse(tokens[i], out var n)) return 0;
		i++;

		// consume "hours" if present
		if (i < tokens.Length && tokens[i].ToLowerInvariant() is "hours" or "hour" or "hr")
			i++;

		return n;
	}

	private static readonly Dictionary<string, DayOfWeek> DayMap = new(StringComparer.OrdinalIgnoreCase)
	{
		["sun"] = DayOfWeek.Sunday, ["0"] = DayOfWeek.Sunday, ["7"] = DayOfWeek.Sunday,
		["mon"] = DayOfWeek.Monday, ["1"] = DayOfWeek.Monday,
		["tue"] = DayOfWeek.Tuesday, ["2"] = DayOfWeek.Tuesday,
		["wed"] = DayOfWeek.Wednesday, ["3"] = DayOfWeek.Wednesday,
		["thu"] = DayOfWeek.Thursday, ["4"] = DayOfWeek.Thursday,
		["fri"] = DayOfWeek.Friday, ["5"] = DayOfWeek.Friday,
		["sat"] = DayOfWeek.Saturday, ["6"] = DayOfWeek.Saturday,
	};

	public static DayOfWeek[]? ParseDays(string input)
	{
		var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries);
		var result = new List<DayOfWeek>();
		foreach (var part in parts)
		{
			if (DayMap.TryGetValue(part.Trim(), out var dow))
				result.Add(dow);
			else
				return null;
		}
		return result.Count > 0 ? result.ToArray() : null;
	}

	// ── Windows: Task Scheduler ────────────────────────────────────────

	private const string TaskFolder = "\\SeaShell\\";

	private static string GetTaskName(string scriptStem)
		=> $"{Environment.UserName} - {scriptStem}";

	private static int ScheduleWindows(string scriptPath, ScheduleSpec spec, Action<string>? log)
	{
		var stem = Path.GetFileNameWithoutExtension(scriptPath);
		var taskName = GetTaskName(stem);
		var fullName = TaskFolder + taskName;

		// Find seaw.exe in the same directory as this assembly, or in the install dir
		var binDir = Path.GetDirectoryName(typeof(ScriptScheduler).Assembly.Location) ?? ".";
		var seawPath = Path.Combine(binDir, "seaw.exe");
		if (!File.Exists(seawPath))
		{
			// Try install dir
			var installDir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"seashell", "bin");
			seawPath = Path.Combine(installDir, "seaw.exe");
		}
		if (!File.Exists(seawPath))
		{
			log?.Invoke("seaw.exe not found. Is SeaShell installed?");
			return 1;
		}

		var workingDir = Path.GetDirectoryName(scriptPath) ?? ".";
		var xml = BuildScriptTaskXml(seawPath, scriptPath, workingDir, spec);

		// Write XML to temp file
		var tmpFile = Path.GetTempFileName();
		try
		{
			File.WriteAllText(tmpFile, xml);

			var psi = new ProcessStartInfo
			{
				FileName = "schtasks.exe",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			psi.ArgumentList.Add("/Create");
			psi.ArgumentList.Add("/TN");
			psi.ArgumentList.Add(fullName);
			psi.ArgumentList.Add("/XML");
			psi.ArgumentList.Add(tmpFile);
			psi.ArgumentList.Add("/F"); // force replace

			using var proc = Process.Start(psi)!;
			proc.WaitForExit(10_000);
			var stderr = proc.StandardError.ReadToEnd().Trim();

			if (proc.ExitCode == 0)
			{
				log?.Invoke($"Scheduled: {fullName}");
				log?.Invoke($"  action: seaw.exe {scriptPath}");
				log?.Invoke($"  timing: {FormatSpec(spec)}");
				return 0;
			}

			log?.Invoke($"Failed to create task: {stderr}");
			return 1;
		}
		finally
		{
			try { File.Delete(tmpFile); } catch { }
		}
	}

	private static int UnscheduleWindows(string scriptStem, Action<string>? log)
	{
		var taskName = GetTaskName(scriptStem);
		var fullName = TaskFolder + taskName;

		var psi = new ProcessStartInfo
		{
			FileName = "schtasks.exe",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		psi.ArgumentList.Add("/Delete");
		psi.ArgumentList.Add("/TN");
		psi.ArgumentList.Add(fullName);
		psi.ArgumentList.Add("/F");

		try
		{
			using var proc = Process.Start(psi)!;
			proc.WaitForExit(10_000);
			if (proc.ExitCode == 0)
			{
				log?.Invoke($"Removed: {fullName}");
				return 0;
			}
			log?.Invoke($"Task not found: {fullName}");
			return 1;
		}
		catch (Exception ex)
		{
			log?.Invoke($"Error: {ex.Message}");
			return 1;
		}
	}

	private static int ListWindows(Action<string>? log)
	{
		var psi = new ProcessStartInfo
		{
			FileName = "schtasks.exe",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		psi.ArgumentList.Add("/Query");
		psi.ArgumentList.Add("/TN");
		psi.ArgumentList.Add("\\SeaShell\\");
		psi.ArgumentList.Add("/FO");
		psi.ArgumentList.Add("CSV");
		psi.ArgumentList.Add("/NH");

		try
		{
			using var proc = Process.Start(psi)!;
			var output = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit(5_000);
			if (proc.ExitCode != 0) { log?.Invoke("No scheduled scripts."); return 0; }

			var user = Environment.UserName;
			var found = false;

			foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = line.Split(',');
				if (parts.Length < 3) continue;
				var taskPath = parts[0].Trim('"', ' ', '\r');
				var state = parts[2].Trim('"', ' ', '\r', '\n');

				var taskName = taskPath.StartsWith(TaskFolder)
					? taskPath[TaskFolder.Length..]
					: taskPath;

				// Skip daemon/elevator infrastructure tasks
				if (taskName.StartsWith("SeaShell Daemon", StringComparison.OrdinalIgnoreCase)) continue;
				if (taskName.StartsWith("SeaShell Elevator", StringComparison.OrdinalIgnoreCase)) continue;

				// Only show current user's tasks
				if (!taskName.StartsWith($"{user} - ", StringComparison.OrdinalIgnoreCase)) continue;

				var scriptName = taskName[($"{user} - ".Length)..];
				log?.Invoke($"  {scriptName,-30} {state.ToLowerInvariant()}");
				found = true;
			}

			if (!found)
				log?.Invoke("No scheduled scripts.");
			return 0;
		}
		catch
		{
			log?.Invoke("No scheduled scripts.");
			return 0;
		}
	}

	// ── Windows: Task XML generation ───────────────────────────────────

	private static string BuildScriptTaskXml(string command, string scriptPath, string workingDir, ScheduleSpec spec)
	{
		XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
		var userId = $"{Environment.UserDomainName}\\{Environment.UserName}";
		var stem = Path.GetFileNameWithoutExtension(scriptPath);

		var trigger = BuildTrigger(ns, userId, spec);

		var execContent = new List<XElement>
		{
			new XElement(ns + "Command", command),
			new XElement(ns + "Arguments", scriptPath),
			new XElement(ns + "WorkingDirectory", workingDir),
		};

		var doc = new XDocument(
			new XElement(ns + "Task",
				new XAttribute("version", "1.2"),
				new XElement(ns + "RegistrationInfo",
					new XElement(ns + "Description", $"SeaShell scheduled script: {stem}")
				),
				new XElement(ns + "Triggers", trigger),
				new XElement(ns + "Principals",
					new XElement(ns + "Principal",
						new XAttribute("id", "Author"),
						new XElement(ns + "UserId", userId),
						new XElement(ns + "LogonType", "InteractiveToken"),
						new XElement(ns + "RunLevel", "LeastPrivilege")
					)
				),
				new XElement(ns + "Settings",
					new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
					new XElement(ns + "DisallowStartIfOnBatteries", "false"),
					new XElement(ns + "StopIfGoingOnBatteries", "false"),
					new XElement(ns + "AllowHardTerminate", "true"),
					new XElement(ns + "StartWhenAvailable", "true"),
					new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
					new XElement(ns + "ExecutionTimeLimit", "PT0S"),
					new XElement(ns + "AllowStartOnDemand", "true"),
					new XElement(ns + "Hidden", "false")
				),
				new XElement(ns + "Actions",
					new XElement(ns + "Exec", execContent.ToArray())
				)
			)
		);

		return doc.Declaration?.ToString() + doc.ToString()
			?? "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + doc.ToString();
	}

	private static XElement BuildTrigger(XNamespace ns, string userId, ScheduleSpec spec)
	{
		switch (spec.Kind)
		{
			case ScheduleKind.Reboot:
				return new XElement(ns + "BootTrigger",
					new XElement(ns + "Enabled", "true"));

			case ScheduleKind.Logon:
				return new XElement(ns + "LogonTrigger",
					new XElement(ns + "Enabled", "true"),
					new XElement(ns + "UserId", userId));

			case ScheduleKind.Daily:
				return new XElement(ns + "CalendarTrigger",
					new XElement(ns + "StartBoundary", $"2025-01-01T{spec.Hour:D2}:{spec.Minute:D2}:00"),
					new XElement(ns + "Enabled", "true"),
					new XElement(ns + "ScheduleByDay",
						new XElement(ns + "DaysInterval", "1")));

			case ScheduleKind.Hourly:
			{
				var trigger = new XElement(ns + "CalendarTrigger",
					new XElement(ns + "StartBoundary", $"2025-01-01T00:{spec.Minute:D2}:00"),
					new XElement(ns + "Enabled", "true"),
					new XElement(ns + "ScheduleByDay",
						new XElement(ns + "DaysInterval", "1")),
					new XElement(ns + "Repetition",
						new XElement(ns + "Interval", "PT1H"),
						new XElement(ns + "Duration", "P1D")));
				return trigger;
			}

			case ScheduleKind.Weekly:
			{
				var daysElement = new XElement(ns + "DaysOfWeek");
				foreach (var day in spec.DaysOfWeek ?? new[] { DayOfWeek.Sunday })
					daysElement.Add(new XElement(ns + DayName(day)));

				var trigger = new XElement(ns + "CalendarTrigger",
					new XElement(ns + "StartBoundary", $"2025-01-01T{spec.Hour:D2}:{spec.Minute:D2}:00"),
					new XElement(ns + "Enabled", "true"),
					new XElement(ns + "ScheduleByWeek",
						new XElement(ns + "WeeksInterval", "1"),
						daysElement));

				// Weekly with interval (e.g. "weekly on mon every 5 minutes at 01:00 for 8 hours")
				if (spec.IntervalValue > 0)
				{
					var interval = spec.IntervalMinutes
						? $"PT{spec.IntervalValue}M"
						: $"PT{spec.IntervalValue}H";
					var duration = spec.DurationHours > 0 ? $"PT{spec.DurationHours}H" : "P1D";
					trigger.Add(new XElement(ns + "Repetition",
						new XElement(ns + "Interval", interval),
						new XElement(ns + "Duration", duration)));
				}
				return trigger;
			}

			case ScheduleKind.Interval:
			{
				var interval = spec.IntervalMinutes
					? $"PT{spec.IntervalValue}M"
					: $"PT{spec.IntervalValue}H";
				var duration = spec.DurationHours > 0 ? $"PT{spec.DurationHours}H" : "P1D";

				return new XElement(ns + "CalendarTrigger",
					new XElement(ns + "StartBoundary", $"2025-01-01T{spec.Hour:D2}:{spec.Minute:D2}:00"),
					new XElement(ns + "Enabled", "true"),
					new XElement(ns + "ScheduleByDay",
						new XElement(ns + "DaysInterval", "1")),
					new XElement(ns + "Repetition",
						new XElement(ns + "Interval", interval),
						new XElement(ns + "Duration", duration)));
			}

			default:
				throw new ArgumentOutOfRangeException();
		}
	}

	private static string DayName(DayOfWeek day) => day switch
	{
		DayOfWeek.Sunday => "Sunday",
		DayOfWeek.Monday => "Monday",
		DayOfWeek.Tuesday => "Tuesday",
		DayOfWeek.Wednesday => "Wednesday",
		DayOfWeek.Thursday => "Thursday",
		DayOfWeek.Friday => "Friday",
		DayOfWeek.Saturday => "Saturday",
		_ => "Sunday",
	};

	// ── Linux: crontab ─────────────────────────────────────────────────

	private const string CronTag = "# seashell:";

	private static int ScheduleLinux(string scriptPath, ScheduleSpec spec, Action<string>? log)
	{
		if (spec.Kind == ScheduleKind.Logon)
		{
			log?.Invoke("@logon is not supported on Linux (crontab). Use @reboot instead.");
			return 1;
		}

		var stem = Path.GetFileNameWithoutExtension(scriptPath);
		var tag = $"{CronTag}{stem}";

		// Find sea binary
		var seaPath = FindSeaLinux();
		if (seaPath == null)
		{
			log?.Invoke("sea not found on PATH or in ~/.local/share/seashell/bin");
			return 1;
		}

		var cronLine = BuildCronLine(spec, seaPath, scriptPath);
		var fullLine = $"{cronLine} {tag}";

		// Read existing crontab, filter out old entry for this script, append new
		var existing = ReadCrontab();
		var lines = existing
			.Where(l => !l.Contains(tag))
			.Append(fullLine)
			.ToList();

		if (!WriteCrontab(lines))
		{
			log?.Invoke("Failed to write crontab.");
			return 1;
		}

		log?.Invoke($"Scheduled: {stem}");
		log?.Invoke($"  cron: {fullLine}");
		return 0;
	}

	private static int UnscheduleLinux(string scriptStem, Action<string>? log)
	{
		var tag = $"{CronTag}{scriptStem}";
		var existing = ReadCrontab();
		var filtered = existing.Where(l => !l.Contains(tag)).ToList();

		if (filtered.Count == existing.Count)
		{
			log?.Invoke($"No crontab entry found for: {scriptStem}");
			return 1;
		}

		if (!WriteCrontab(filtered))
		{
			log?.Invoke("Failed to write crontab.");
			return 1;
		}

		log?.Invoke($"Removed: {scriptStem}");
		return 0;
	}

	private static int ListLinux(Action<string>? log)
	{
		var existing = ReadCrontab();
		var found = false;

		foreach (var line in existing)
		{
			var tagIdx = line.IndexOf(CronTag, StringComparison.Ordinal);
			if (tagIdx < 0) continue;

			var scriptName = line[(tagIdx + CronTag.Length)..].Trim();
			var schedule = line[..tagIdx].Trim();
			log?.Invoke($"  {scriptName,-30} {schedule}");
			found = true;
		}

		if (!found)
			log?.Invoke("No scheduled scripts.");
		return 0;
	}

	private static string BuildCronLine(ScheduleSpec spec, string seaPath, string scriptPath)
	{
		var dir = Path.GetDirectoryName(scriptPath) ?? "/";
		var cmd = $"cd {QuoteShell(dir)} && {QuoteShell(seaPath)} {QuoteShell(scriptPath)}";

		return spec.Kind switch
		{
			ScheduleKind.Reboot => $"@reboot {cmd}",
			ScheduleKind.Daily => $"{spec.Minute} {spec.Hour} * * * {cmd}",
			ScheduleKind.Hourly => $"{spec.Minute} * * * * {cmd}",
			ScheduleKind.Weekly => $"{spec.Minute} {spec.Hour} * * {FormatCronDays(spec.DaysOfWeek)} {cmd}",
			ScheduleKind.Interval when spec.IntervalMinutes =>
				$"*/{spec.IntervalValue} * * * * {cmd}",
			ScheduleKind.Interval =>
				$"0 */{spec.IntervalValue} * * * {cmd}",
			_ => $"@reboot {cmd}",
		};
	}

	private static string FormatCronDays(DayOfWeek[]? days)
	{
		if (days == null || days.Length == 0) return "0";
		return string.Join(",", days.Select(d => ((int)d).ToString()));
	}

	private static string? FindSeaLinux()
	{
		// Check PATH
		try
		{
			var psi = new ProcessStartInfo("which", "sea")
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
			};
			using var proc = Process.Start(psi)!;
			var path = proc.StandardOutput.ReadToEnd().Trim();
			proc.WaitForExit(3_000);
			if (proc.ExitCode == 0 && !string.IsNullOrEmpty(path)) return path;
		}
		catch { }

		// Check standard install location
		var installPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".local", "share", "seashell", "bin", "sea");
		return File.Exists(installPath) ? installPath : null;
	}

	private static List<string> ReadCrontab()
	{
		try
		{
			var psi = new ProcessStartInfo("crontab", "-l")
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			using var proc = Process.Start(psi)!;
			var output = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit(3_000);
			if (proc.ExitCode != 0) return new List<string>();
			return output.Split('\n').ToList();
		}
		catch { return new List<string>(); }
	}

	private static bool WriteCrontab(List<string> lines)
	{
		var tmpFile = Path.GetTempFileName();
		try
		{
			// Remove trailing empty lines, ensure single trailing newline
			while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
				lines.RemoveAt(lines.Count - 1);
			File.WriteAllText(tmpFile, string.Join("\n", lines) + "\n");

			var psi = new ProcessStartInfo("crontab", tmpFile)
			{
				UseShellExecute = false,
				RedirectStandardError = true,
			};
			using var proc = Process.Start(psi)!;
			proc.WaitForExit(3_000);
			return proc.ExitCode == 0;
		}
		finally
		{
			try { File.Delete(tmpFile); } catch { }
		}
	}

	private static string QuoteShell(string s) =>
		s.Contains(' ') || s.Contains('\'') ? $"'{s.Replace("'", "'\\''")}'" : s;

	// ── Display helpers ────────────────────────────────────────────────

	public static string FormatSpec(ScheduleSpec spec) => spec.Kind switch
	{
		ScheduleKind.Reboot => "@reboot",
		ScheduleKind.Logon => "@logon",
		ScheduleKind.Daily => $"daily at {spec.Hour:D2}:{spec.Minute:D2}",
		ScheduleKind.Hourly => $"hourly at :{spec.Minute:D2}",
		ScheduleKind.Weekly when spec.IntervalValue > 0 =>
			$"weekly on {FormatDays(spec.DaysOfWeek)} every {spec.IntervalValue} {(spec.IntervalMinutes ? "min" : "hr")}" +
			$" at {spec.Hour:D2}:{spec.Minute:D2}" +
			(spec.DurationHours > 0 ? $" for {spec.DurationHours}h" : ""),
		ScheduleKind.Weekly =>
			$"weekly on {FormatDays(spec.DaysOfWeek)} at {spec.Hour:D2}:{spec.Minute:D2}",
		ScheduleKind.Interval =>
			$"every {spec.IntervalValue} {(spec.IntervalMinutes ? "min" : "hr")}" +
			(spec.Hour > 0 || spec.Minute > 0 ? $" at {spec.Hour:D2}:{spec.Minute:D2}" : "") +
			(spec.DurationHours > 0 ? $" for {spec.DurationHours}h" : ""),
		_ => "unknown",
	};

	private static string FormatDays(DayOfWeek[]? days)
	{
		if (days == null || days.Length == 0) return "sun";
		return string.Join(",", days.Select(d => d.ToString().ToLowerInvariant()[..3]));
	}
}
