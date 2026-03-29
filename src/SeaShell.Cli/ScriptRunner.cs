using System;
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
		string scriptPath, string[] scriptArgs, string daemonAddress, bool isConsoleEphemeral)
	{
		using var cts = new CancellationTokenSource();
		Console.CancelKeyPress += (_, e) =>
		{
			e.Cancel = true;
			cts.Cancel();
		};

		var invoker = new ScriptInvoker(msg => Console.Error.WriteLine($"sea: {msg}"));
		var result = await invoker.RunAsync(
			scriptPath, scriptArgs, daemonAddress,
			OutputMode.Inherit, ct: cts.Token);

		LastExitDelay = result.ExitDelay;
		return result.ExitCode;
	}
}
