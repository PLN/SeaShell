using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>How a scheduled (<c>--quiet</c>) update ended.</summary>
internal enum QuietOutcome
{
	/// <summary>Nothing to do (up to date, or skipped because sessions are connected).</summary>
	Nothing,

	/// <summary>Something was installed.</summary>
	Done,

	Failed,
}

/// <summary>
/// <c>update --quiet</c>, as the scheduled automatic update runs it: all
/// output (this process's and its children's) goes into a buffer. At the end
/// the full outcome of every run is appended to <c>{DataDir}/update.log</c>
/// (<see cref="UpdateLog"/>), and also written to the system log — Windows
/// Event Log (source <c>SeaShell</c>, registered by the elevated install),
/// syslog through <c>logger</c> on Linux — with the full output when something
/// was done or failed; only then is the output also printed (so cron mails it).
/// </summary>
internal sealed class QuietRun
{
	private readonly StringWriter _buffer = new();
	private readonly TextWriter _out;
	private readonly TextWriter _err;

	private QuietRun()
	{
		_out = Console.Out;
		_err = Console.Error;
		var synced = TextWriter.Synchronized(_buffer);
		Console.SetOut(synced);
		Console.SetError(synced);
		HideOwnConsoleWindow();
	}

	public static QuietRun? Begin(bool quiet) => quiet ? new QuietRun() : null;

	/// <summary>The buffer children's output is copied into.</summary>
	public TextWriter Sink => Console.Out;

	public void End(QuietOutcome outcome, string summary, int exitCode)
	{
		Console.SetOut(_out);
		Console.SetError(_err);
		var output = _buffer.ToString();
		if (outcome != QuietOutcome.Nothing)
			(outcome == QuietOutcome.Failed ? _err : _out).Write(output);
		var logError = SystemLog.Write(outcome == QuietOutcome.Failed,
			outcome == QuietOutcome.Nothing ? summary : $"{summary}\n\n{output}");
		if (logError != null)
			output += $"{(output.Length > 0 && !output.EndsWith('\n') ? "\n" : "")}[system log entry not written: {logError}]\n";
		AppendToUpdateLog(outcome, summary, exitCode, output);
	}

	// The one record that does not depend on the Event Log source being
	// registered (a non-elevated user install cannot create it).
	private void AppendToUpdateLog(QuietOutcome outcome, string summary, int exitCode, string output)
	{
		var path = UpdateLog.PathIn(SeaShellIdentity.Current.DataDir);
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			if (File.Exists(path) && UpdateLog.ShouldRollOver(new FileInfo(path).Length))
				File.Move(path, path + ".1", overwrite: true);
			var commandLine = DotnetHost.Describe(Environment.ProcessPath ?? "seashell", Environment.GetCommandLineArgs().Skip(1));
			File.AppendAllText(path, UpdateLog.Entry(UpdateLog.Timestamp(DateTimeOffset.Now), exitCode,
				outcome.ToString().ToLowerInvariant(), commandLine, summary, output));
		}
		catch (Exception ex)
		{
			// Last resort: where cron / the task's console shows it.
			_err.WriteLine($"seashell: could not append to {path}: {ex.Message}");
		}
	}

	// A task started in the user's session gets a console window of its own;
	// when nothing else shares it, hide it so a daily check does not flash up.
	private static void HideOwnConsoleWindow()
	{
		if (!OperatingSystem.IsWindows()) return;
		try
		{
			var window = GetConsoleWindow();
			if (window == IntPtr.Zero) return;
			var processes = new uint[4];
			if (GetConsoleProcessList(processes, (uint)processes.Length) == 1)
				ShowWindow(window, 0 /* SW_HIDE */);
		}
		catch { }
	}

	[DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
	[DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
	[DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

/// <summary>Event Log (Windows) / syslog (Linux) entries for unattended runs.</summary>
internal static class SystemLog
{
	public const string Source = "SeaShell";

	/// <summary>Write one entry; null on success, else why it was not written.</summary>
	public static string? Write(bool error, string message)
	{
		try
		{
			if (OperatingSystem.IsWindows())
			{
				// The source is registered by the elevated install; without it a
				// non-elevated process cannot create it (update.log has the entry).
				if (message.Length > 30000) message = message[..30000] + "\n[truncated]";
				EventLog.WriteEntry(Source, message, error ? EventLogEntryType.Error : EventLogEntryType.Information, 3000);
				return null;
			}

			var psi = new ProcessStartInfo("logger")
			{
				UseShellExecute = false,
				RedirectStandardInput = true,
			};
			foreach (var a in new[] { "-t", "seashell", "-p", error ? "user.err" : "user.info" }) psi.ArgumentList.Add(a);
			using var proc = Process.Start(psi)!;
			proc.StandardInput.Write(message);
			proc.StandardInput.Close();
			return proc.WaitForExit(5_000) && proc.ExitCode == 0 ? null : "logger did not finish successfully";
		}
		catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
	}
}
