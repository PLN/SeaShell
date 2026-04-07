using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SeaShell.Protocol;
using SeaShell.Invoker;
using SeaShell.Cli;

// ── seaw.exe console handling ───────────────────────────────────────────

// Detect if running as seaw.exe (Windows subsystem — no console by default).
// seaw.exe defaults to windowless. Only allocate a console if //sea_console
// is present in the script, or if running a non-script command (--help etc).
var isWindowMode = IsSeawExe();
var consoleAllocated = false;

// Log window mode detection to Event Log (seaw runs without console, so
// stderr/stdout may be invisible). This helps diagnose CI test failures.
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
	var diag = $"ProcessPath={Environment.ProcessPath}, IsWindowMode={isWindowMode}";
	if (isWindowMode)
		WriteToEventLog($"[seaw startup] {diag}", EventLogEntryType.Information);
}
if (isWindowMode && args.Length > 0 && !args[0].StartsWith("-"))
{
	// Script invocation — only allocate console if //sea_console directive present
	var scriptFile = Path.GetFullPath(args[0]);
	if (File.Exists(scriptFile))
	{
		var scan = DirectiveScanner.Scan(scriptFile);
		if (scan.Console)
		{
			AllocateConsole();
			consoleAllocated = true;
		}
	}
}
else if (isWindowMode)
{
	// Non-script invocation (--help, --status, no args, etc.) — need a console
	AllocateConsole();
	consoleAllocated = true;
}

// ── Parse args ──────────────────────────────────────────────────────────

var verbose = Environment.GetEnvironmentVariable("SEA_VERBOSE") is "1" or "true";
if (args.Length > 0 && args[0] is "--verbose" or "-V")
{
	verbose = true;
	args = args[1..];
}

if (args.Length > 0 && args[0] is "--help" or "-h")
{
	var version = typeof(Program).Assembly.GetName().Version?.ToString(4) ?? "0.1.0";
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
	Console.WriteLine("  -V, --verbose         Show daemon lifecycle messages");
	Console.WriteLine("  --daemon-start        Start daemon directly");
	Console.WriteLine("  --daemon-stop         Stop daemon via pipe");
	ExitDelayIfNeeded();
	return 0;
}

var daemonAddress = TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity, TransportEndpoint.CurrentVersion);
Action<string> log = msg => Console.WriteLine($"  {msg}");
Action<string> logErr = msg => Console.Error.WriteLine($"  {msg}");

// No arguments → REPL (console mode) or error (window mode)
if (args.Length == 0)
{
	if (isWindowMode)
	{
		LogError("seaw: no script specified. Usage: seaw <script.cs> [args...]");
		ExitDelayIfNeeded();
		return 1;
	}
	return await ReplClient.ReplAsync(daemonAddress, Array.Empty<string>());
}

switch (args[0])
{
	case "--version" or "-v":
		var ver = typeof(Program).Assembly.GetName().Version?.ToString(4) ?? "0.1.0";
		Console.WriteLine($"{{~}} SeaShell v{ver}");
		ExitDelayIfNeeded();
		return 0;
	case "-i" or "--repl":
		var replPackages = args.Length > 1 ? args[1..] : Array.Empty<string>();
		return await ReplClient.ReplAsync(daemonAddress, replPackages);

	// ── Daemon management (via Invoker) ────────────────────────────
	case "--status":
		var status = await DaemonManager.StatusAsync(daemonAddress);
		if (status != null)
		{
			var idleStr = status.IdleTimeoutSeconds > 0
				? $"idle {FormatDuration(status.IdleSeconds)} ({FormatDuration(status.IdleTimeoutSeconds)})"
				: $"idle {FormatDuration(status.IdleSeconds)}";
			Console.WriteLine($"  daemon:   v{status.Version}, up {FormatDuration(status.UptimeSeconds)}, {idleStr}, {status.ActiveScripts} active");
			if (status.ElevatorConnected)
			{
				var elevIdleStr = $"idle {FormatDuration(status.ElevatorIdleSeconds)}";
				Console.WriteLine($"  elevator: v{status.ElevatorVersion ?? "?"}, up {FormatDuration(status.ElevatorUptimeSeconds)}, {elevIdleStr}");
			}
			else
				Console.WriteLine($"  elevator: not connected");
			ExitDelayIfNeeded();
			return 0;
		}
		var daemonTaskState = ScheduledTasks.QueryDaemonTaskState();
		Console.WriteLine(daemonTaskState != null
			? $"  daemon:   not running (task: {daemonTaskState.ToLowerInvariant()})"
			: "  daemon:   not running");

		var elevatorTaskState = ScheduledTasks.QueryElevatorTaskState();
		Console.WriteLine(elevatorTaskState != null
			? $"  elevator: task {elevatorTaskState.ToLowerInvariant()}"
			: "  elevator: not running");
		ExitDelayIfNeeded();
		return 1;

	case "--daemon-stop":
		var stopped = await DaemonManager.DaemonStopAsync(daemonAddress);
		Console.WriteLine(stopped ? "daemon: stop requested" : "daemon: not running");
		ExitDelayIfNeeded();
		return stopped ? 0 : 1;

	case "--daemon-start":
		return DaemonManager.StartDaemon(logErr, logErr);

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

	// ── Script scheduling (via Invoker) ───────────────────────────
	case "--schedule":
		if (args.Length < 3) { Console.Error.WriteLine("Usage: sea --schedule <script.cs> <timing...>"); return 1; }
		return ScriptScheduler.Schedule(Path.GetFullPath(args[1]), args[2..], log);
	case "--unschedule":
		if (args.Length < 2) { Console.Error.WriteLine("Usage: sea --unschedule <script.cs>"); return 1; }
		return ScriptScheduler.Unschedule(Path.GetFullPath(args[1]), log);
	case "--schedule-list":
		return ScriptScheduler.List(log);

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
	LogError($"sea: script not found: {scriptPath}");
	ExitDelayIfNeeded();
	return 1;
}

var isConsoleEphemeral = ConsoleHelper.IsConsoleEphemeral();

// Set console title: "Script {~} SeaShell v0.4.2.202"
try
{
	var scriptName = Path.GetFileNameWithoutExtension(scriptPath);
	var seaVersion = typeof(Program).Assembly.GetName().Version?.ToString(4) ?? "0.1.0";
	Console.Title = $"{scriptName} {{~}} SeaShell v{seaVersion}";
}
catch { /* no console, or redirected — ignore */ }

var scriptArgs = args.Length > 1 ? args[1..] : Array.Empty<string>();
var exitCode = await ScriptRunner.RunScriptAsync(scriptPath, scriptArgs, daemonAddress, isConsoleEphemeral, isWindowMode, verbose);
ConsoleHelper.ExitDelay(isConsoleEphemeral);
return exitCode;

// ── seaw.exe helpers ────────────────────────────────────────────────────

void ExitDelayIfNeeded()
{
	if (!consoleAllocated) return;
	if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
	if (!ConsoleHelper.IsConsoleEphemeral()) return;
	ConsoleDelayer.Delay(3);
}

void LogError(string message)
{
	Console.Error.WriteLine(message);
	// In window mode without console, also write to Event Log
	if (isWindowMode && !consoleAllocated && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		WriteToEventLog(message);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static void WriteToEventLog(string message, EventLogEntryType type = EventLogEntryType.Error)
{
	try { EventLog.WriteEntry("SeaShell", message, type); }
	catch { try { EventLog.WriteEntry("Application", $"[SeaShell] {message}", type); } catch { } }
}

static string FormatDuration(int totalSeconds)
{
	if (totalSeconds < 60) return $"{totalSeconds}s";
	if (totalSeconds < 3600) return $"{totalSeconds / 60}m {totalSeconds % 60}s";
	var h = totalSeconds / 3600;
	var m = (totalSeconds % 3600) / 60;
	return $"{h}h {m}m";
}

static bool IsSeawExe()
{
	var exePath = Environment.ProcessPath;
	if (exePath == null) return false;
	var exeName = Path.GetFileNameWithoutExtension(exePath);
	return exeName.Equals("seaw", StringComparison.OrdinalIgnoreCase);
}

static void AllocateConsole()
{
	if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

	// Try to attach to parent's console first (covers: seaw.exe run from a terminal)
	if (!AttachConsole(unchecked((uint)-1))) // ATTACH_PARENT_PROCESS
		AllocConsoleWin();

	// .NET's Console.Out/Error are initialized at startup. For a WinExe, that's
	// before any console exists, so they point to null streams. After attaching
	// or allocating a console, reinitialize them to the real handles.
	Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
	Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
	Console.SetIn(new StreamReader(Console.OpenStandardInput()));
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool AttachConsole(uint dwProcessId);

[DllImport("kernel32.dll", SetLastError = true, EntryPoint = "AllocConsole")]
static extern bool AllocConsoleWin();
