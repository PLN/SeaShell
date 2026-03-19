using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;

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
	private const string DaemonTaskName = "SeaShell Daemon";
	private const string ElevatorTaskName = "SeaShell Elevator";
	private const string TaskFolder = "\\SeaShell\\";

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

	/// <summary>Stop both tasks via schtasks /End. No elevation needed.</summary>
	public static int Stop()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Console.WriteLine("Task Scheduler is Windows-only. On Linux, use systemd user services.");
			return 1;
		}

		var ok = true;
		ok &= EndTask(ElevatorTaskName);
		ok &= EndTask(DaemonTaskName);
		return ok ? 0 : 1;
	}

	public static int InstallDaemon()
	{
		if (!RequireWindows()) return 1;
		var (daemonExe, _) = FindBinaries();
		if (daemonExe == null) return 1;
		Console.WriteLine($"  binary: {daemonExe}");
		return RegisterTask(DaemonTaskName, daemonExe, elevated: false) ? 0 : 1;
	}

	public static int InstallElevator()
	{
		if (!RequireWindows()) return 1;
		var (_, elevatorExe) = FindBinaries();
		if (elevatorExe == null) return 1;
		Console.WriteLine($"  binary: {elevatorExe}");
		Console.WriteLine("  NOTE: This registers a task with highest privileges.");
		Console.WriteLine("        Requires an elevated shell to register.");
		return RegisterTask(ElevatorTaskName, elevatorExe, elevated: true) ? 0 : 1;
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

	private static bool RequireWindows()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
		Console.WriteLine("Task Scheduler is Windows-only. On Linux, use systemd user services.");
		return false;
	}

	private static bool RegisterTask(string name, string exePath, bool elevated)
	{
		// Build the task XML. Using XML directly gives us full control over RunLevel
		// without needing the COM TaskScheduler API.
		var xml = BuildTaskXml(exePath, elevated);
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

	private static bool EndTask(string name)
	{
		var fullName = TaskFolder + name;
		return RunSchtasks($"Stopping {name}", "/End", "/TN", fullName);
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

	private static string BuildTaskXml(string exePath, bool elevated)
	{
		var runLevel = elevated ? "HighestAvailable" : "LeastPrivilege";
		var userId = $"{Environment.UserDomainName}\\{Environment.UserName}";

		// Task Scheduler 1.2 XML schema (Vista+)
		XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
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
					new XElement(ns + "Exec",
						new XElement(ns + "Command", exePath)
					)
				)
			)
		);

		return doc.Declaration?.ToString() + doc.ToString()
			?? "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + doc.ToString();
	}

	private static (string? daemonExe, string? elevatorExe) FindBinaries()
	{
		var daemon = FindBinary("SeaShell.Daemon", "seashell-daemon");
		var elevator = FindBinary("SeaShell.Elevator", "seashell-elevator");
		return (daemon, elevator);
	}

	private static string? FindBinary(string projectName, string assemblyName)
	{
		var baseDir = AppContext.BaseDirectory;
		var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";

		// Published mode: binary is a sibling of the CLI
		var candidate = Path.Combine(baseDir, assemblyName + ext);
		if (File.Exists(candidate)) return candidate;

		// Dev mode: sibling project build output
		var dev = FindDevBinary(projectName, assemblyName);
		if (dev != null) return dev;

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
