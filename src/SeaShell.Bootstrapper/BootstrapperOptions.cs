using System;
using System.Collections.Generic;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// The command line with the bootstrapper's own flags removed.
///
/// Scope flags: <c>--all</c>, <c>--user</c>, <c>--system</c> (see
/// <see cref="ScopeResolver"/>). Update options: <c>--version</c>,
/// <c>--source</c>, <c>--auto on|off</c>. Uninstall: <c>--purge</c>. Internal
/// flags (relaunches, scheduled updates; not in <c>--help</c>): <c>--pass</c>,
/// <c>--detect</c>, <c>--if-needed</c>, <c>--keep-global-tool</c>,
/// <c>--if-idle</c>, <c>--quiet</c>, <c>--dotnet</c>, <c>--relocated-from</c>,
/// and the self-elevation plumbing
/// <c>--log-file</c> (the elevated child tees its console output there; the
/// parent tails it live into the original console), <c>--invoker-sid</c>
/// (detects over-the-shoulder UAC with a different account) and
/// <c>--result-file</c> (the child's <see cref="InstallRunSummary"/>).
/// </summary>
internal sealed record BootstrapperOptions
{
	/// <summary>First positional argument, case preserved; null when none were given.</summary>
	public required string? Command { get; init; }

	/// <summary>Positional arguments after the command (flags already stripped).</summary>
	public required string[] Arguments { get; init; }

	/// <summary>Target LocalSystem rather than the invoking user.</summary>
	public required bool SystemMode { get; init; }

	/// <summary>Everything the platform supports (user + elevator + SYSTEM / root); re-records the scope.</summary>
	public required bool AllMode { get; init; }

	/// <summary>User only; re-records the scope.</summary>
	public required bool UserMode { get; init; }

	/// <summary>Internal per-pass targeting; never rewrites the recorded scope.</summary>
	public required ScopeParts? Pass { get; init; }

	/// <summary>Internal: parts the (elevated) process must detect and add when present.</summary>
	public required ScopeParts Detect { get; init; }

	/// <summary>Internal update mode: skip targets that already have this version and every part.</summary>
	public required bool IfNeeded { get; init; }

	/// <summary>Internal: the run that started this one executes from the global tool; do not remove it.</summary>
	public required bool KeepGlobalTool { get; init; }

	/// <summary><c>update --version</c>: package version to fetch.</summary>
	public required string? PackageVersion { get; init; }

	/// <summary><c>update --source</c>: NuGet source to fetch from (replaces the configured ones).</summary>
	public required string? Source { get; init; }

	/// <summary>Tee all console output here (set only on the elevated relaunch).</summary>
	public required string? LogFile { get; init; }

	/// <summary>SID of the non-elevated invoker (set only on the elevated relaunch).</summary>
	public required string? InvokerSid { get; init; }

	/// <summary>Write this run's <see cref="InstallRunSummary"/> here (set only on the elevated relaunch).</summary>
	public required string? ResultFile { get; init; }

	/// <summary><c>update --auto on|off</c>: the raw value (validated by the command).</summary>
	public required string? AutoUpdate { get; init; }

	/// <summary>Internal (scheduled update): skip when the target's daemon has connected sessions.</summary>
	public required bool IfIdle { get; init; }

	/// <summary>Internal (scheduled update): no output unless something was done or failed; outcome to the Event Log / syslog.</summary>
	public required bool Quiet { get; init; }

	/// <summary>Internal (scheduled update): the absolute <c>dotnet</c> recorded when automatic updates were enabled.</summary>
	public required string? Dotnet { get; init; }

	/// <summary><c>uninstall --purge</c>: also remove the scripts scheduled with <c>seashell schedule</c>.</summary>
	public required bool Purge { get; init; }

	/// <summary>
	/// Internal (uninstall on Windows): this process is a relocated copy; the
	/// value is the process id of the original, which runs from a directory
	/// being removed and deletes what it holds after it exits.
	/// </summary>
	public required int? RelocatedFrom { get; init; }

	/// <summary>A flag was malformed (for example an unknown <c>--pass</c> token).</summary>
	public required string? Error { get; init; }

	/// <summary>The scope-related flags, for <see cref="ScopeResolver"/>.</summary>
	public ScopeRequest ScopeRequest => new()
	{
		All = AllMode,
		User = UserMode,
		System = SystemMode,
		Pass = Pass,
	};

	/// <summary>No scope flag and no internal targeting: the remembered scope applies.</summary>
	public bool UsesRememberedScope => !AllMode && !UserMode && !SystemMode && Pass == null;

	public static BootstrapperOptions Parse(string[] args)
	{
		bool systemMode = false, allMode = false, userMode = false, ifNeeded = false, keepGlobalTool = false;
		bool ifIdle = false, quiet = false, purge = false;
		int? relocatedFrom = null;
		string? autoUpdate = null, dotnet = null;
		ScopeParts? pass = null;
		var detect = ScopeParts.None;
		string? logFile = null, invokerSid = null, resultFile = null, packageVersion = null, source = null, error = null;
		var positional = new List<string>();

		void ParseParts(string flag, string value, ref ScopeParts target)
		{
			if (ScopeTokens.TryParseList(value, out var parts)) target |= parts;
			else error ??= $"{flag}: expected a comma-separated list of user, elevator, system (got '{value}')";
		}

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--system": systemMode = true; continue;
				case "--all": allMode = true; continue;
				case "--user": userMode = true; continue;
				case "--if-needed": ifNeeded = true; continue;
				case "--keep-global-tool": keepGlobalTool = true; continue;
				case "--if-idle": ifIdle = true; continue;
				case "--quiet": quiet = true; continue;
				case "--purge": purge = true; continue;
				case "--auto" when i + 1 < args.Length: autoUpdate = args[++i]; continue;
				case "--dotnet" when i + 1 < args.Length: dotnet = args[++i]; continue;
				case "--relocated-from" when i + 1 < args.Length:
					if (int.TryParse(args[++i], out var pid)) relocatedFrom = pid;
					else error ??= $"--relocated-from: expected a process id (got '{args[i]}')";
					continue;
				// A flag with no following value is left where it is, so it shows
				// up as an unknown command/argument rather than being swallowed.
				case "--pass" when i + 1 < args.Length:
				{
					var parts = pass ?? ScopeParts.None;
					ParseParts("--pass", args[++i], ref parts);
					pass = parts;
					continue;
				}
				case "--detect" when i + 1 < args.Length: ParseParts("--detect", args[++i], ref detect); continue;
				case "--log-file" when i + 1 < args.Length: logFile = args[++i]; continue;
				case "--invoker-sid" when i + 1 < args.Length: invokerSid = args[++i]; continue;
				case "--result-file" when i + 1 < args.Length: resultFile = args[++i]; continue;
				case "--source" when i + 1 < args.Length: source = args[++i]; continue;
				// `seashell --version` (no command yet) is the status view; after a
				// command it is the package version to fetch.
				case "--version" when positional.Count > 0 && i + 1 < args.Length: packageVersion = args[++i]; continue;
				default: positional.Add(args[i]); continue;
			}
		}

		return new BootstrapperOptions
		{
			Command = positional.Count > 0 ? positional[0] : null,
			Arguments = positional.Count > 1 ? positional.GetRange(1, positional.Count - 1).ToArray() : Array.Empty<string>(),
			SystemMode = systemMode,
			AllMode = allMode,
			UserMode = userMode,
			Pass = pass,
			Detect = detect,
			IfNeeded = ifNeeded,
			KeepGlobalTool = keepGlobalTool,
			PackageVersion = packageVersion,
			Source = source,
			LogFile = logFile,
			InvokerSid = invokerSid,
			ResultFile = resultFile,
			AutoUpdate = autoUpdate,
			IfIdle = ifIdle,
			Quiet = quiet,
			Dotnet = dotnet,
			Purge = purge,
			RelocatedFrom = relocatedFrom,
			Error = error,
		};
	}
}
