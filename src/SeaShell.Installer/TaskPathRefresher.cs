using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SeaShell;

namespace SeaShell.Installer;

/// <summary>
/// Rewrites baked-in sea/seaw paths in pre-existing scheduled tasks (Windows)
/// and crontab entries (Linux) so they point at the current install's binaries.
/// Run from <c>seashell install</c> so user-scheduled scripts created under an
/// older layout keep working after a version reorg.
/// </summary>
public static class TaskPathRefresher
{
	private const string CronTag = "# seashell:";
	private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

	// ── Pure rewrite cores (unit-testable, no process spawning) ─────────

	/// <summary>
	/// Rewrite the <c>&lt;Actions&gt;/&lt;Exec&gt;/&lt;Command&gt;</c> node of a Task Scheduler XML
	/// to <paramref name="newCommand"/>. Returns null if the command already matches
	/// (case-insensitive) or the XML doesn't contain an Exec/Command; otherwise returns
	/// the rewritten XML string.
	/// </summary>
	public static string? RewriteXmlCommand(string xml, string newCommand)
	{
		XDocument doc;
		try { doc = XDocument.Parse(xml); }
		catch { return null; }

		var commandEl = doc.Root?
			.Element(TaskNs + "Actions")?
			.Element(TaskNs + "Exec")?
			.Element(TaskNs + "Command");
		if (commandEl == null) return null;

		if (string.Equals(commandEl.Value.Trim(), newCommand, StringComparison.OrdinalIgnoreCase))
			return null;

		commandEl.Value = newCommand;
		return (doc.Declaration?.ToString() ?? "") + doc.ToString();
	}

	/// <summary>
	/// Rewrite the sea binary path in a crontab line tagged with <c># seashell:...</c>.
	/// Matches the token immediately following <c>&amp;&amp; </c> (produced by
	/// ScriptScheduler's BuildCronLine). Returns null if the line has no seashell tag,
	/// no matching <c>&amp;&amp; </c> prefix, or the path already equals
	/// <paramref name="newSeaPath"/>.
	/// </summary>
	public static string? RewriteCronLine(string line, string newSeaPath)
	{
		if (line.IndexOf(CronTag, StringComparison.Ordinal) < 0) return null;

		var match = Regex.Match(line, @"&& ('[^']*'|\S+)");
		if (!match.Success) return null;

		var captured = match.Groups[1].Value;
		var oldPath = captured.StartsWith('\'') && captured.EndsWith('\'')
			? captured[1..^1].Replace("'\\''", "'")
			: captured;

		if (string.Equals(oldPath, newSeaPath, StringComparison.Ordinal)) return null;

		var newQuoted = ShellQuote(newSeaPath);
		var start = match.Groups[1].Index;
		var len = match.Groups[1].Length;
		return line[..start] + newQuoted + line[(start + len)..];
	}

	private static string ShellQuote(string s) =>
		s.Contains(' ') || s.Contains('\'')
			? $"'{s.Replace("'", "'\\''")}'"
			: s;

	// ── I/O wrappers (not unit-tested) ──────────────────────────────────

	/// <summary>
	/// On Windows: enumerate user-script tasks under <c>\SeaShell\</c> and rewrite any
	/// whose Exec command doesn't already point at <c>{CurrentBinDir}\seaw.exe</c>.
	/// Skips daemon/elevator infrastructure tasks (they're re-registered separately).
	/// </summary>
	public static void RefreshWindows(Action<string>? log)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

		var targetCommand = Path.Combine(SeaShellPaths.CurrentBinDir, "seaw.exe");
		var userPrefix = $"{Environment.UserName} - ";
		var refreshed = 0;

		foreach (var taskPath in TaskQuery.EnumerateFolder().ToList())
		{
			var name = taskPath.StartsWith(TaskQuery.Folder, StringComparison.Ordinal)
				? taskPath[TaskQuery.Folder.Length..]
				: Path.GetFileName(taskPath);

			if (name.StartsWith("SeaShell Daemon", StringComparison.OrdinalIgnoreCase)) continue;
			if (name.StartsWith("SeaShell Elevator", StringComparison.OrdinalIgnoreCase)) continue;
			if (!name.StartsWith(userPrefix, StringComparison.OrdinalIgnoreCase)) continue;

			var xml = QueryTaskXml(taskPath);
			if (xml == null) continue;

			var rewritten = RewriteXmlCommand(xml, targetCommand);
			if (rewritten == null) continue; // already current or unparseable

			if (DaemonTasks.RegisterTaskFromXml(taskPath, rewritten, elevatedContext: false, log: null))
			{
				log?.Invoke($"Refreshed task: {name}  →  {targetCommand}");
				refreshed++;
			}
			else
			{
				log?.Invoke($"Failed to refresh task: {name}");
			}
		}

		if (refreshed > 0)
			log?.Invoke($"Refreshed {refreshed} scheduled task(s).");
	}

	/// <summary>
	/// On Linux: rewrite any <c># seashell:...</c>-tagged crontab line whose sea path
	/// differs from <c>{CurrentBinDir}/sea</c>.
	/// </summary>
	public static void RefreshLinux(Action<string>? log)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

		var targetSea = Path.Combine(SeaShellPaths.CurrentBinDir, "sea");
		var lines = ReadCrontab();
		var changed = false;
		var refreshed = 0;

		for (var i = 0; i < lines.Count; i++)
		{
			var rewritten = RewriteCronLine(lines[i], targetSea);
			if (rewritten == null) continue;
			lines[i] = rewritten;
			changed = true;
			refreshed++;
		}

		if (!changed) return;
		if (WriteCrontab(lines))
			log?.Invoke($"Refreshed {refreshed} crontab entry(ies) to {targetSea}");
		else
			log?.Invoke("Failed to write crontab.");
	}

	// ── Helpers ─────────────────────────────────────────────────────────

	private static string? QueryTaskXml(string taskPath)
	{
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "schtasks.exe",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				StandardOutputEncoding = Encoding.Unicode,
			};
			psi.ArgumentList.Add("/Query");
			psi.ArgumentList.Add("/TN");
			psi.ArgumentList.Add(taskPath);
			psi.ArgumentList.Add("/XML");

			using var proc = Process.Start(psi)!;
			var output = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit(5_000);
			if (proc.ExitCode != 0) return null;
			return output.TrimStart('\uFEFF');
		}
		catch { return null; }
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
}
