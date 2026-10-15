using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Bootstrapper;
using SeaShell.Installer;

// ── SeaShell Bootstrapper ───────────────────────────────────────────
// Installed via: dotnet tool install -g SeaShell
// Command:       seashell install | update | uninstall | status
//
// Extracts embedded per-RID archives to per-version directories,
// manages a `current` junction, and registers daemon + file associations.
//
// This file parses the command line and dispatches. Each command lives in
// its own class in the SeaShell.Bootstrapper namespace; the install target
// (identity + version + derived paths) is passed to them explicitly, so no
// command reads captured mutable state from another one.

var version = typeof(Program).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
var options = BootstrapperOptions.Parse(args);

if (options.LogFile != null)
{
	var teeStream = new StreamWriter(File.Open(options.LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
	{
		AutoFlush = true,
	};
	Console.SetOut(new TeeWriter(Console.Out, teeStream));
	Console.SetError(new TeeWriter(Console.Error, teeStream));
}

// The install TARGET. On Windows, --system targets LocalSystem: every
// identity-derived value (data dir, task principal, lock name) is computed
// for S-1-5-18 and the elevated bootstrapper operates on it directly — no
// SYSTEM-context execution needed. On Linux, --system is just "run as root",
// so the ambient identity is already the target.
// `install --all` runs the install flow twice (user, then SYSTEM) by
// retargeting between passes; each pass builds its own InstallTarget, so a
// step can never run against the other pass's directories.
var target = new InstallTarget(
	options.SystemMode && isWindows ? SeaShellIdentity.LocalSystem : SeaShellIdentity.Current,
	version,
	isWindows);

if (options.Command == null)
	return await new StatusReporter(target, options).RunAsync();

if (options.Command is "--help" or "-h")
{
	Console.WriteLine($"{{~}} SeaShell v{version}");
	Console.WriteLine();
	Console.WriteLine("Usage: seashell              Show installation status");
	Console.WriteLine("       seashell status --system  Show SYSTEM installation status");
	Console.WriteLine("       seashell install      Install or update SeaShell (current user)");
	Console.WriteLine("       seashell install --all     User + elevator + SYSTEM in one pass (one UAC prompt)");
	Console.WriteLine("       seashell install --system  SYSTEM-only install (requires elevation)");
	Console.WriteLine("       seashell uninstall    Remove SeaShell binaries and PATH entry");
	Console.WriteLine("       seashell uninstall --system  Remove SYSTEM install");
	Console.WriteLine("       seashell start        Start daemon and elevator");
	Console.WriteLine("       seashell stop         Stop daemon and elevator");
	Console.WriteLine("       seashell schedule <script.cs> <timing...>");
	Console.WriteLine("       seashell schedule     List scheduled scripts");
	Console.WriteLine("       seashell unschedule <script.cs>");
	return 0;
}

switch (options.Command.ToLowerInvariant())
{
	case "install":
	case "update":
		if (InstallAllPlanner.IsCompositeInstall(options.AllMode, isWindows))
			return await new InstallAllRunner(target, options).RunAsync(options.Command.ToLowerInvariant());
		return (await new InstallRunner(target, options).RunAsync()).ExitCode;

	case "uninstall":
		return await new UninstallRunner(target, options).RunAsync();

	case "status":
	case "--version":
	case "-v":
		return await new StatusReporter(target, options).RunAsync();

	case "start":
		return new DaemonLifecycle(target).Start();

	case "stop":
		return await new DaemonLifecycle(target).StopAsync();

	case "schedule":
		return new ScheduleCommands(target).Schedule(options.Arguments);
	case "unschedule":
		return new ScheduleCommands(target).Unschedule(options.Arguments);

	default:
		Console.Error.WriteLine($"seashell: unknown command '{options.Command}'. Use --help.");
		return 1;
}