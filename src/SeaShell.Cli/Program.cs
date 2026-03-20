using System;
using System.IO;
using SeaShell.Ipc;
using SeaShell.Protocol;
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

var daemonAddress = TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity);

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
	case "--status":
		return await DaemonManager.StatusAsync(daemonAddress);
	case "--daemon-stop":
		return await DaemonManager.DaemonStopAsync(daemonAddress);
	case "--daemon-start":
		return DaemonManager.StartDaemon();
	case "--start":
		return ScheduledTasks.Start();
	case "--stop":
		return ScheduledTasks.Stop();
	case "--install-daemon":
		return ScheduledTasks.InstallDaemon();
	case "--install-elevator":
		return ScheduledTasks.InstallElevator();
	case "--uninstall-daemon":
		return ScheduledTasks.UninstallDaemon();
	case "--uninstall-elevator":
		return ScheduledTasks.UninstallElevator();
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
