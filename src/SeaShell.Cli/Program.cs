using System;
using System.IO;
using SeaShell.Protocol;
using SeaShell.Invoker;
using SeaShell.Cli;

// ── Parse args ──────────────────────────────────────────────────────────

if (args.Length > 0 && args[0] is "--help" or "-h")
{
	var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
	Console.WriteLine($"{{~}} SeaShell v{version}");
	Console.WriteLine("Copyright (c) PLN. MIT License.");
	Console.WriteLine();
	Console.WriteLine("Usage: sea                      Interactive REPL");
	Console.WriteLine("       sea <script.cs> [args...] Run a script");
	Console.WriteLine("       sea -i [packages...]      REPL with NuGet packages");
	Console.WriteLine();
	Console.WriteLine("  --status              Show daemon and elevator status");
	Console.WriteLine();
	Console.WriteLine("  Task Scheduler (Windows):");
	Console.WriteLine("  --install-daemon      Register daemon task (limited privileges)");
	Console.WriteLine("  --install-elevator    Register elevator task (requires elevation)");
	Console.WriteLine("  --uninstall-daemon    Remove daemon task");
	Console.WriteLine("  --uninstall-elevator  Remove elevator task");
	Console.WriteLine("  --start               Start registered tasks");
	Console.WriteLine("  --stop                Stop registered tasks");
	Console.WriteLine();
	Console.WriteLine("  File association (Windows):");
	Console.WriteLine("  --associate [.ext]    Associate extension with sea (default: .cs)");
	Console.WriteLine("  --unassociate [.ext]  Remove association");
	Console.WriteLine();
	Console.WriteLine("  Dev/debug:");
	Console.WriteLine("  --daemon-start        Start daemon directly");
	Console.WriteLine("  --daemon-stop         Stop daemon via pipe");
	return 0;
}

var daemonAddress = TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity, TransportEndpoint.CurrentVersion);
Action<string> log = msg => Console.WriteLine($"  {msg}");
Action<string> logErr = msg => Console.Error.WriteLine($"  {msg}");

// No arguments → REPL
if (args.Length == 0)
	return await ReplClient.ReplAsync(daemonAddress, Array.Empty<string>());

switch (args[0])
{
	case "--version" or "-v":
		var ver = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
		Console.WriteLine($"{{~}} SeaShell v{ver}");
		return 0;
	case "-i" or "--repl":
		var replPackages = args.Length > 1 ? args[1..] : Array.Empty<string>();
		return await ReplClient.ReplAsync(daemonAddress, replPackages);

	// ── Daemon management (via Invoker) ────────────────────────────
	case "--status":
		var status = await DaemonManager.StatusAsync(daemonAddress);
		if (status != null)
		{
			Console.WriteLine($"  daemon:   v{status.Version}, uptime {status.UptimeSeconds}s, {status.ActiveScripts} active");
			Console.WriteLine($"  elevator: {(status.ElevatorConnected ? "connected" : "not connected")}");
			return 0;
		}
		Console.WriteLine("  daemon:   not running");
		Console.WriteLine("  elevator: unknown (daemon not running)");
		return 1;

	case "--daemon-stop":
		var stopped = await DaemonManager.DaemonStopAsync(daemonAddress);
		Console.WriteLine(stopped ? "daemon: stop requested" : "daemon: not running");
		return stopped ? 0 : 1;

	case "--daemon-start":
		return DaemonManager.StartDaemon(logErr);

	// ── Task management (via Invoker) ──────────────────────────────
	case "--start":
		return ScheduledTasks.Start(log);
	case "--stop":
		return await ScheduledTasks.Stop(log);
	case "--install-daemon":
		return ScheduledTasks.InstallDaemon(log);
	case "--install-elevator":
		return ScheduledTasks.InstallElevator(log);
	case "--uninstall-daemon":
		return ScheduledTasks.UninstallDaemon(log);
	case "--uninstall-elevator":
		return ScheduledTasks.UninstallElevator(log);

	// ── File association (CLI-only) ────────────────────────────────
	case "--associate":
		var ext = args.Length > 1 ? args[1] : ".cs";
		return FileAssoc.Associate(ext);
	case "--unassociate":
		var uext = args.Length > 1 ? args[1] : ".cs";
		return FileAssoc.Unassociate(uext);
}

// ── Run script ──────────────────────────────────────────────────────────

var scriptPath = Path.GetFullPath(args[0]);
if (!File.Exists(scriptPath))
{
	Console.Error.WriteLine($"sea: script not found: {scriptPath}");
	return 1;
}

var isConsoleEphemeral = ConsoleHelper.IsConsoleEphemeral();
var scriptArgs = args.Length > 1 ? args[1..] : Array.Empty<string>();
var exitCode = await ScriptRunner.RunScriptAsync(scriptPath, scriptArgs, daemonAddress, isConsoleEphemeral);
ConsoleHelper.ExitDelay(isConsoleEphemeral);
return exitCode;
