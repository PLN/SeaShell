using System;
using System.Collections.Generic;

namespace SeaShell.Bootstrapper;

/// <summary>
/// The command line with the bootstrapper's own plumbing flags removed.
/// <c>--log-file</c> and <c>--invoker-sid</c> are internal plumbing for the
/// self-elevated relaunch of <c>install --all</c>: the elevated child tees its
/// console output to the log file (the parent tails it live into the original
/// console) and compares the invoker SID to detect over-the-shoulder UAC with
/// a different account.
/// </summary>
internal sealed record BootstrapperOptions
{
	/// <summary>First positional argument, case preserved; null when none were given.</summary>
	public required string? Command { get; init; }

	/// <summary>Positional arguments after the command (flags already stripped).</summary>
	public required string[] Arguments { get; init; }

	/// <summary>Target LocalSystem rather than the invoking user.</summary>
	public required bool SystemMode { get; init; }

	/// <summary>Composite user + elevator + SYSTEM install behind one UAC prompt.</summary>
	public required bool AllMode { get; init; }

	/// <summary>Tee all console output here (set only on the elevated relaunch).</summary>
	public required string? LogFile { get; init; }

	/// <summary>SID of the non-elevated invoker (set only on the elevated relaunch).</summary>
	public required string? InvokerSid { get; init; }

	public static BootstrapperOptions Parse(string[] args)
	{
		var systemMode = false;
		var allMode = false;
		string? logFile = null, invokerSid = null;
		var positional = new List<string>();

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--system": systemMode = true; continue;
				case "--all": allMode = true; continue;
				// A flag with no following value is left where it is, so it shows
				// up as an unknown command/argument rather than being swallowed.
				case "--log-file" when i + 1 < args.Length: logFile = args[++i]; continue;
				case "--invoker-sid" when i + 1 < args.Length: invokerSid = args[++i]; continue;
				default: positional.Add(args[i]); continue;
			}
		}

		return new BootstrapperOptions
		{
			Command = positional.Count > 0 ? positional[0] : null,
			Arguments = positional.Count > 1 ? positional.GetRange(1, positional.Count - 1).ToArray() : Array.Empty<string>(),
			SystemMode = systemMode,
			AllMode = allMode,
			LogFile = logFile,
			InvokerSid = invokerSid,
		};
	}
}