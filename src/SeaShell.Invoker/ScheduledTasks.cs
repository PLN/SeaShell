using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using SeaShell.Ipc;
using SeaShell.Protocol;

namespace SeaShell.Invoker;

/// <summary>
/// Registers/unregisters Windows Task Scheduler tasks for the Daemon and Elevator.
/// Console-free — accepts log callbacks for progress reporting.
/// On Linux, this is a no-op (systemd user services are handled separately).
/// </summary>
public static class ScheduledTasks
{
	private static readonly string Version =
		typeof(ScheduledTasks).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";
	private static readonly string DaemonTaskName = $"SeaShell Daemon ({Environment.UserName}) {Version}";
	private static readonly string ElevatorTaskName = $"SeaShell Elevator ({Environment.UserName}) {Version}";
	private const string TaskFolder = "\\SeaShell\\";

	// ── On-demand task start (silent) ──────────────────────────────────

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

	/// <summary>Start both daemon and elevator tasks.</summary>
	public static int Start(Action<string>? log = null)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			log?.Invoke("Task Scheduler is Windows-only. On Linux, use systemd user services.");
			return 1;
		}

		var ok = true;
		ok &= RunTask(DaemonTaskName, log);
		ok &= RunTask(ElevatorTaskName, log);
		return ok ? 0 : 1;
	}

	/// <summary>Stop daemon and elevator.</summary>
	public static async Task<int> Stop(Action<string>? log = null)
	{
		var daemonAddress = TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity, TransportEndpoint.CurrentVersion);
		var daemonStopped = await StopDaemonIpcAsync(daemonAddress);

		var elevatorStopped = false;
		var daemonTaskStopped = false;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			elevatorStopped = EndTask(ElevatorTaskName);
			if (!daemonStopped)
				daemonTaskStopped = EndTask(DaemonTaskName);
		}

		log?.Invoke(daemonStopped ? "daemon:   stopped"
			: daemonTaskStopped    ? "daemon:   stopped (task)"
			:                        "daemon:   not running");
		log?.Invoke(elevatorStopped ? "elevator: stopped"
			:                         "elevator: not running");
		return 0;
	}

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

	public static int InstallDaemon(Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		CleanupOldTasks("Daemon", log);
		var result = FindAndStageBinary("seashell-daemon", "daemon", log);
		if (result == null) return 1;
		log?.Invoke($"binary: {result.Value.command}{(result.Value.arguments != null ? " " + result.Value.arguments : "")}");
		if (!RegisterTask(DaemonTaskName, result.Value.command, result.Value.arguments, elevated: false, log))
			return 1;
		ServiceManifest.UpdateComponent(Version, "daemon", c => c.ScheduledTask = TaskFolder + DaemonTaskName);
		return 0;
	}

	public static int InstallElevator(Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		CleanupOldTasks("Elevator", log);
		var result = FindAndStageBinary("seashell-elevator", "elevator", log);
		if (result == null) return 1;
		log?.Invoke($"binary: {result.Value.command}{(result.Value.arguments != null ? " " + result.Value.arguments : "")}");
		log?.Invoke("NOTE: This registers a task with highest privileges.");
		log?.Invoke("      Requires an elevated shell to register.");
		if (!RegisterTask(ElevatorTaskName, result.Value.command, result.Value.arguments, elevated: true, log))
			return 1;
		ServiceManifest.UpdateComponent(Version, "elevator", c => c.ScheduledTask = TaskFolder + ElevatorTaskName);
		return 0;
	}

	/// <summary>
	/// Remove old versioned tasks for the current user. Tasks are named
	/// "SeaShell {component} ({user}) {version}" — on update, the old version's
	/// task remains. This enumerates \SeaShell\ and deletes stale entries.
	/// </summary>
	private static void CleanupOldTasks(string component, Action<string>? log)
	{
		try
		{
			// List all tasks in \SeaShell\ folder
			var psi = new ProcessStartInfo
			{
				FileName = "schtasks.exe",
				ArgumentList = { "/Query", "/TN", "\\SeaShell\\", "/FO", "CSV", "/NH" },
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			using var proc = Process.Start(psi)!;
			var output = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit(5_000);
			if (proc.ExitCode != 0) return;

			// Pattern: "\SeaShell\SeaShell Daemon (username) 0.3.17.108"
			// Match tasks for this component + current user, skip the current version
			var user = Environment.UserName;
			var prefix = $"SeaShell {component} ({user})";
			var currentName = component == "Daemon" ? DaemonTaskName : ElevatorTaskName;

			foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				// CSV: "TaskName","Next Run Time","Status"
				var parts = line.Split(',');
				if (parts.Length < 1) continue;
				var taskPath = parts[0].Trim('"', ' ', '\r');

				// Extract task name from full path: \SeaShell\SeaShell Daemon (user) 0.3.17
				var taskName = taskPath.StartsWith(TaskFolder)
					? taskPath[TaskFolder.Length..]
					: Path.GetFileName(taskPath);

				if (taskName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
					&& !taskName.Equals(currentName, StringComparison.OrdinalIgnoreCase))
				{
					// Old version — stop and delete
					EndTask(taskName);
					if (DeleteTask(taskName, null))
						log?.Invoke($"Removed old task: {taskName}");
				}
			}
		}
		catch { }
	}

	public static int UninstallDaemon(Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		return DeleteTask(DaemonTaskName, log) ? 0 : 1;
	}

	public static int UninstallElevator(Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		return DeleteTask(ElevatorTaskName, log) ? 0 : 1;
	}

	// ── Helpers ────────────────────────────────────────────────────────

	private static bool RequireWindows(Action<string>? log)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
		log?.Invoke("Task Scheduler is Windows-only. On Linux, use systemd user services.");
		return false;
	}

	private static bool RegisterTask(string name, string command, string? arguments, bool elevated, Action<string>? log)
	{
		var xml = BuildTaskXml(command, arguments, elevated);
		var tempXml = Path.Combine(Path.GetTempPath(), $"seashell-task-{(elevated ? "elev" : "daemon")}.xml");

		try
		{
			File.WriteAllText(tempXml, xml);
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
			var stderr = proc.StandardError.ReadToEnd();

			if (proc.ExitCode != 0)
			{
				log?.Invoke($"Failed to register '{name}': {stderr.Trim()}");
				if (elevated)
					log?.Invoke("(Elevator task requires running as Administrator)");
				return false;
			}
			log?.Invoke($"Registered: {fullName}");
			return true;
		}
		finally
		{
			try { File.Delete(tempXml); } catch { }
		}
	}

	private static bool DeleteTask(string name, Action<string>? log)
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
			log?.Invoke($"Removed: {fullName}");
			return true;
		}

		var stderr = proc.StandardError.ReadToEnd().Trim();
		if (stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
		{
			log?.Invoke($"Already removed: {fullName}");
			return true;
		}
		log?.Invoke($"Failed to remove '{name}': {stderr}");
		return false;
	}

	private static bool RunTask(string name, Action<string>? log)
	{
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

		using var proc = Process.Start(psi)!;
		proc.WaitForExit(10_000);
		var stderr = proc.StandardError.ReadToEnd().Trim();

		if (proc.ExitCode == 0)
		{
			log?.Invoke($"Starting {name}: ok");
			return true;
		}

		log?.Invoke($"Starting {name}: {stderr}");
		return false;
	}

	private static bool EndTask(string name)
	{
		var fullName = TaskFolder + name;
		var state = QueryTaskState(fullName);
		if (state == null) return false;
		if (!state.Equals("Running", StringComparison.OrdinalIgnoreCase))
			return false;

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
			if (proc.ExitCode != 0) return null;

			var parts = stdout.Trim().Split(',');
			if (parts.Length >= 3)
				return parts[2].Trim('"', ' ', '\r', '\n');
			return null;
		}
		catch { return null; }
	}

	// ── Task XML ───────────────────────────────────────────────────────

	private static string BuildTaskXml(string command, string? arguments, bool elevated)
	{
		var runLevel = elevated ? "HighestAvailable" : "LeastPrivilege";
		var userId = $"{Environment.UserDomainName}\\{Environment.UserName}";

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
					new XElement(ns + "ExecutionTimeLimit", "PT0S"),
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

	/// <summary>Stage binary via manifest and return the command to launch it.</summary>
	private static (string command, string? arguments)? FindAndStageBinary(
		string assemblyName, string componentName, Action<string>? log = null)
	{
		var stagedDir = componentName == "daemon"
			? ServiceManifest.GetOrStageDaemon(Version, log)
			: ServiceManifest.GetOrStageElevator(Version, log);

		if (stagedDir == null)
		{
			log?.Invoke($"could not find {assemblyName}");
			return null;
		}

		var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
		var stagedExe = Path.Combine(stagedDir, assemblyName + ext);
		if (ext != "" && File.Exists(stagedExe))
			return (stagedExe, null);

		var stagedDll = Path.Combine(stagedDir, assemblyName + ".dll");
		if (File.Exists(stagedDll))
			return ("dotnet", $"exec \"{stagedDll}\"");

		log?.Invoke($"staged binary not found in {stagedDir}");
		return null;
	}
}
