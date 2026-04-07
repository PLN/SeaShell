using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Invoker;

namespace SeaShell.Cli;

/// <summary>
/// Thin CLI wrapper around <see cref="ScriptInvoker"/>. Adds console-specific
/// concerns: Ctrl+C handling, exit delay for ephemeral consoles, and log formatting.
/// </summary>
static class ScriptRunner
{
	/// <summary>Exit delay (seconds) reported by the last script. Used by ConsoleHelper.</summary>
	internal static int LastExitDelay = 7;

	public static async Task<int> RunScriptAsync(
		string scriptPath, string[] scriptArgs, string daemonAddress,
		bool isConsoleEphemeral, bool windowMode = false, bool verbose = false)
	{
		using var cts = new CancellationTokenSource();
		Console.CancelKeyPress += (_, e) =>
		{
			e.Cancel = true;
			cts.Cancel();
		};

		// In window mode without a console, errors go to Event Log
		// instead of stderr (which is a null stream for WinExe).
		var useEventLog = windowMode && !isConsoleEphemeral && !HasConsole()
			&& RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#pragma warning disable CA1416 // platform guard is the useEventLog check above
		Action<string> log = useEventLog
			? msg => WriteEventLog($"sea: {msg}")
			: msg => Console.Error.WriteLine($"sea: {msg}");
#pragma warning restore CA1416
		Action<string>? verboseLog = verbose ? log : null;
		var invoker = new ScriptInvoker(log, verboseLog);
		var result = await invoker.RunAsync(
			scriptPath, scriptArgs, daemonAddress,
			OutputMode.Inherit, windowMode: windowMode, ct: cts.Token);

		LastExitDelay = result.ExitDelay;
		return result.ExitCode;
	}

	private static bool HasConsole()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
		return GetConsoleWindow() != IntPtr.Zero;
	}

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetConsoleWindow();

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static void WriteEventLog(string message)
	{
		try
		{
			EventLog.WriteEntry("SeaShell", message, EventLogEntryType.Error);
		}
		catch
		{
			try { EventLog.WriteEntry("Application", $"[SeaShell] {message}", EventLogEntryType.Error); }
			catch { }
		}
	}
}
