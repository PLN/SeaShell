using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using SeaShell.Ipc;
using SeaShell.Protocol;

namespace SeaShell.Cli;

/// <summary>
/// Registers/unregisters Windows Task Scheduler tasks for the Daemon and Elevator.
///
/// Task 1: "SeaShell Daemon"   — runs at user logon, limited privileges
/// Task 2: "SeaShell Elevator" — runs at user logon, highest privileges (requires admin to register)
///
/// On Linux, this is a no-op for now (systemd user services TBD).
/// </summary>
static class ScheduledTasks
{
	private static readonly string DaemonTaskName = $"SeaShell Daemon ({Environment.UserName})";
	private static readonly string ElevatorTaskName = $"SeaShell Elevator ({Environment.UserName})";
	private const string TaskFolder = "\\SeaShell\\";

	// ── On-demand task start (silent, used by DaemonManager/ScriptRunner) ──

	/// <summary>Try to start the daemon via Task Scheduler. Returns true if task was started.</summary>
	public static bool TryRunDaemonTask() => TryRunTask(DaemonTaskName);

	/// <summary>Try to start the elevator via Task Scheduler. Returns true if task was started.</summary>
	public static bool TryRunElevatorTask() => TryRunTask(ElevatorTaskName);

	private static bool TryRunTask(string name)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
		var fullName = TaskFolder + name;
		var psi = new ProcessStartInfo
		{
			FileName = "schtasks.exe",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		psi.ArgumentList.Add("/Run");
		psi.ArgumentList.Add("/TN");
		psi.ArgumentList.Add(fullName);

		try
		{
			using var proc = Process.Start(psi)!;
			proc.WaitForExit(5_000);
			return proc.ExitCode == 0;
		}
		catch { return false; }
	}

	// ── Explicit start/stop ────────────────────────────────────────────

	/// <summary>Start both tasks via schtasks /Run. No elevation needed.</summary>
	public static int Start()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Console.WriteLine("Task Scheduler is Windows-only. On Linux, use systemd user services.");
			return 1;
		}

		var ok = true;
		ok &= RunTask(DaemonTaskName);
		ok &= RunTask(ElevatorTaskName);
		return ok ? 0 : 1;
	}

	/// <summary>Stop daemon and elevator. IPC stop first (any daemon), then schtasks /End for tasks.</summary>
	public static async Task<int> Stop()
	{
		// IPC stop — works on any platform, for any running daemon
		var daemonAddress = TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity);
		var daemonStopped = await StopDaemonIpcAsync(daemonAddress);

		// Task Scheduler — also end registered tasks (Windows only)
		var elevatorStopped = false;
		var daemonTaskStopped = false;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			elevatorStopped = EndTask(ElevatorTaskName);
			if (!daemonStopped)
				daemonTaskStopped = EndTask(DaemonTaskName);
		}

		// Unified output, matching --status format
		Console.WriteLine(daemonStopped ? "  daemon:   stopped"
			: daemonTaskStopped            ? "  daemon:   stopped (task)"
			:                                "  daemon:   not running");
		Console.WriteLine(elevatorStopped ? "  elevator: stopped"
			:                               "  elevator: not running");
		return 0;
	}

	/// <summary>Try IPC graceful stop. Returns true if daemon was running and accepted the stop.</summary>
	private static async Task<bool> StopDaemonIpcAsync(string address)
	{
		try
		{
			await using var conn = await TransportClient.ConnectAsync(address, timeoutMs: 2000);
			await conn.Channel.SendAsync(new StopRequest());
			await conn.Channel.ReceiveAsync();
			return true;
		}
		catch { return false; }
	}

	// ── Install / uninstall ────────────────────────────────────────────

	public static int InstallDaemon()
	{
		if (!RequireWindows()) return 1;
		var result = FindBinary("SeaShell.Daemon", "seashell-daemon");
		if (result == null) return 1;
		Console.WriteLine($"  binary: {result.Value.command}{(result.Value.arguments != null ? " " + result.Value.arguments : "")}");
		return RegisterTask(DaemonTaskName, result.Value.command, result.Value.arguments, elevated: false) ? 0 : 1;
	}

	public static int InstallElevator()
	{
		if (!RequireWindows()) return 1;
		var result = FindBinary("SeaShell.Elevator", "seashell-elevator");
		if (result == null) return 1;
		Console.WriteLine($"  binary: {result.Value.command}{(result.Value.arguments != null ? " " + result.Value.arguments : "")}");
		Console.WriteLine("  NOTE: This registers a task with highest privileges.");
		Console.WriteLine("        Requires an elevated shell to register.");
		return RegisterTask(ElevatorTaskName, result.Value.command, result.Value.arguments, elevated: true) ? 0 : 1;
	}

	public static int UninstallDaemon()
	{
		if (!RequireWindows()) return 1;
		return DeleteTask(DaemonTaskName) ? 0 : 1;
	}

	public static int UninstallElevator()
	{
		if (!RequireWindows()) return 1;
		return DeleteTask(ElevatorTaskName) ? 0 : 1;
	}

	// ── Helpers ────────────────────────────────────────────────────────

	private static bool RequireWindows()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
		Console.WriteLine("Task Scheduler is Windows-only. On Linux, use systemd user services.");
		return false;
	}

	private static bool RegisterTask(string name, string command, string? arguments, bool elevated)
	{
		var xml = BuildTaskXml(command, arguments, elevated);
		var tempXml = Path.Combine(Path.GetTempPath(), $"seashell-task-{(elevated ? "elev" : "daemon")}.xml");

		try
		{
			File.WriteAllText(tempXml, xml);

			// schtasks /Create /TN "folder\name" /XML file /F (force overwrite)
			var fullName = TaskFolder + name;
			var psi = new ProcessStartInfo
			{
				FileName = "schtasks.exe",
				ArgumentList = { "/Create", "/TN", fullName, "/XML", tempXml, "/F" },
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			using var proc = Process.Start(psi)!;
			proc.WaitForExit(10_000);
			var stdout = proc.StandardOutput.ReadToEnd();
			var stderr = proc.StandardError.ReadToEnd();

			if (proc.ExitCode != 0)
			{
				Console.Error.WriteLine($"  Failed to register '{name}': {stderr.Trim()}");
				if (elevated)
					Console.Error.WriteLine("  (Elevator task requires running as Administrator)");
				return false;
			}
			Console.WriteLine($"  Registered: {fullName}");
			return true;
		}
		finally
		{
			try { File.Delete(tempXml); } catch { }
		}
	}

	private static bool DeleteTask(string name)
	{
		var fullName = TaskFolder + name;
		var psi = new ProcessStartInfo
		{
			FileName = "schtasks.exe",
			ArgumentList = { "/Delete", "/TN", fullName, "/F" },
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		using var proc = Process.Start(psi)!;
		proc.WaitForExit(10_000);

		if (proc.ExitCode == 0)
		{
			Console.WriteLine($"  Removed: {fullName}");
			return true;
		}
		else
		{
			var stderr = proc.StandardError.ReadToEnd().Trim();
			// Not finding the task is fine during uninstall
			if (stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine($"  Already removed: {fullName}");
				return true;
			}
			Console.Error.WriteLine($"  Failed to remove '{name}': {stderr}");
			return false;
		}
	}

	private static bool RunTask(string name)
	{
		var fullName = TaskFolder + name;
		return RunSchtasks($"Starting {name}", "/Run", "/TN", fullName);
	}

	/// <summary>Stop a task if it's running. Returns true if a running task was stopped.</summary>
	private static bool EndTask(string name)
	{
		var fullName = TaskFolder + name;

		// Query task state first — only attempt stop if actually running
		var state = QueryTaskState(fullName);
		if (state == null) return false; // not registered
		if (!state.Equals("Running", StringComparison.OrdinalIgnoreCase))
			return false; // registered but not running

		// Task is running — stop it
		var psi = new ProcessStartInfo
		{
			FileName = "schtasks.exe",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		psi.ArgumentList.Add("/End");
		psi.ArgumentList.Add("/TN");
		psi.ArgumentList.Add(fullName);

		using var proc = Process.Start(psi)!;
		proc.WaitForExit(10_000);
		return proc.ExitCode == 0;
	}

	/// <summary>Query a task's status. Returns "Running", "Ready", etc., or null if not registered.</summary>
	private static string? QueryTaskState(string fullName)
	{
		var psi = new ProcessStartInfo
		{
			FileName = "schtasks.exe",
			ArgumentList = { "/Query", "/TN", fullName, "/FO", "CSV", "/NH" },
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		try
		{
			using var proc = Process.Start(psi)!;
			var stdout = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit(5_000);
			if (proc.ExitCode != 0) return null; // not registered

			// CSV format: "TaskName","Next Run Time","Status"
			var parts = stdout.Trim().Split(',');
			if (parts.Length >= 3)
				return parts[2].Trim('"', ' ', '\r', '\n');
			return null;
		}
		catch { return null; }
	}

	private static bool RunSchtasks(string label, params string[] args)
	{
		var psi = new ProcessStartInfo
		{
			FileName = "schtasks.exe",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach (var a in args)
			psi.ArgumentList.Add(a);

		using var proc = Process.Start(psi)!;
		proc.WaitForExit(10_000);
		var stderr = proc.StandardError.ReadToEnd().Trim();

		if (proc.ExitCode == 0)
		{
			Console.WriteLine($"  {label}: ok");
			return true;
		}

		Console.Error.WriteLine($"  {label}: {stderr}");
		return false;
	}

	// ── Task XML ───────────────────────────────────────────────────────

	private static string BuildTaskXml(string command, string? arguments, bool elevated)
	{
		var runLevel = elevated ? "HighestAvailable" : "LeastPrivilege";
		var userId = $"{Environment.UserDomainName}\\{Environment.UserName}";

		// Task Scheduler 1.2 XML schema (Vista+)
		XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

		var execContent = new System.Collections.Generic.List<XElement>
		{
			new XElement(ns + "Command", command)
		};
		if (arguments != null)
			execContent.Add(new XElement(ns + "Arguments", arguments));

		var doc = new XDocument(
			new XElement(ns + "Task",
				new XAttribute("version", "1.2"),
				new XElement(ns + "RegistrationInfo",
					new XElement(ns + "Description", elevated
						? "SeaShell elevated process spawner — pre-elevated at logon for UAC-free script elevation"
						: "SeaShell scripting daemon — persistent compilation service for C# scripts")
				),
				new XElement(ns + "Triggers",
					new XElement(ns + "LogonTrigger",
						new XElement(ns + "Enabled", "true"),
						new XElement(ns + "UserId", userId)
					)
				),
				new XElement(ns + "Principals",
					new XElement(ns + "Principal",
						new XAttribute("id", "Author"),
						new XElement(ns + "UserId", userId),
						new XElement(ns + "LogonType", "InteractiveToken"),
						new XElement(ns + "RunLevel", runLevel)
					)
				),
				new XElement(ns + "Settings",
					new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
					new XElement(ns + "DisallowStartIfOnBatteries", "false"),
					new XElement(ns + "StopIfGoingOnBatteries", "false"),
					new XElement(ns + "AllowHardTerminate", "true"),
					new XElement(ns + "StartWhenAvailable", "true"),
					new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
					new XElement(ns + "ExecutionTimeLimit", "PT0S"),  // no timeout — runs indefinitely
					new XElement(ns + "AllowStartOnDemand", "true"),
					new XElement(ns + "Hidden", "false")
				),
				new XElement(ns + "Actions",
					new XElement(ns + "Exec", execContent.ToArray())
				)
			)
		);

		return doc.Declaration?.ToString() + doc.ToString()
			?? "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + doc.ToString();
	}

	// ── Binary discovery ───────────────────────────────────────────────

	private static (string command, string? arguments)? FindBinary(string projectName, string assemblyName)
	{
		var baseDir = AppContext.BaseDirectory;
		var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";

		// Published mode: native apphost sibling
		var candidate = Path.Combine(baseDir, assemblyName + ext);
		if (File.Exists(candidate))
			return (candidate, null);

		// Dotnet tool mode: DLL sibling, no apphost
		var dllCandidate = Path.Combine(baseDir, assemblyName + ".dll");
		if (File.Exists(dllCandidate))
			return ("dotnet", $"exec \"{dllCandidate}\"");

		// Dev mode: sibling project build output
		var dev = FindDevBinary(projectName, assemblyName);
		if (dev != null)
			return (dev, null);

		Console.Error.WriteLine($"sea: could not find {assemblyName}");
		Console.Error.WriteLine("     build the solution first: dotnet build");
		return null;
	}

	private static string? FindDevBinary(string projectName, string assemblyName)
	{
		// Walk up from CLI's output dir to find sibling project output
		// Typical: src/SeaShell.Cli/bin/Debug/net10.0/ → src/{project}/bin/Debug/net10.0/
		var dir = AppContext.BaseDirectory;
		for (int i = 0; i < 5; i++)
		{
			var parent = Path.GetDirectoryName(dir);
			if (parent == null) break;
			dir = parent;
		}
		// dir is now at src/ level (approximately)
		var candidate = Path.Combine(dir, projectName, "bin", "Debug", "net10.0", assemblyName + ".exe");
		if (File.Exists(candidate)) return candidate;

		// Try without .exe (Linux)
		candidate = Path.Combine(dir, projectName, "bin", "Debug", "net10.0", assemblyName);
		if (File.Exists(candidate)) return candidate;

		return null;
	}
}
