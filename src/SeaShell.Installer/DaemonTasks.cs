using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using SeaShell.Ipc;
using SeaShell.Protocol;

namespace SeaShell.Installer;

/// <summary>
/// Register/unregister and start/stop the Windows Task Scheduler entries for the
/// Daemon and Elevator. Console-free — accepts log callbacks for progress.
/// On Linux this is a no-op (systemd user services are handled separately).
/// </summary>
public static class DaemonTasks
{
	private static readonly string Version =
		typeof(DaemonTasks).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";
	private static readonly string DaemonTaskName = $"SeaShell Daemon ({Environment.UserName}) {Version}";
	private static readonly string ElevatorTaskName = $"SeaShell Elevator ({Environment.UserName}) {Version}";
	private static string DaemonTaskPath => TaskQuery.Folder + DaemonTaskName;
	private static string ElevatorTaskPath => TaskQuery.Folder + ElevatorTaskName;

	// ── On-demand task start (silent) ──────────────────────────────────

	/// <summary>Try to start the daemon via Task Scheduler. Returns true if task was started.</summary>
	public static bool TryRunDaemonTask() =>
		RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TaskQuery.Run(DaemonTaskPath);

	/// <summary>Try to start the elevator via Task Scheduler. Returns true if task was started.</summary>
	public static bool TryRunElevatorTask() =>
		RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TaskQuery.Run(ElevatorTaskPath);

	/// <summary>Query the state of the daemon task. Returns null if not found.</summary>
	public static string? QueryDaemonTaskState() =>
		RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? TaskQuery.QueryState(DaemonTaskPath) : null;

	/// <summary>Query the state of the elevator task. Returns null if not found.</summary>
	public static string? QueryElevatorTaskState() =>
		RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? TaskQuery.QueryState(ElevatorTaskPath) : null;

	// ── Explicit start/stop ────────────────────────────────────────────

	/// <summary>Start both daemon and elevator tasks.</summary>
	public static int Start(Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		var ok = true;
		ok &= StartTask(DaemonTaskPath, log);
		ok &= StartTask(ElevatorTaskPath, log);
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
			elevatorStopped = EndIfRunning(ElevatorTaskPath);
			if (!daemonStopped)
				daemonTaskStopped = EndIfRunning(DaemonTaskPath);
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
		var binary = BinaryDiscovery.FindDaemon(Version, log);
		if (binary == null) { log?.Invoke("could not find seashell-daemon"); return 1; }
		var (command, arguments) = BuildLaunch(binary);
		log?.Invoke($"binary: {command}{(arguments != null ? " " + arguments : "")}");
		if (!RegisterTask(DaemonTaskPath, command, arguments, elevated: false, log))
			return 1;
		MarkerFile.Write(SeaShellPaths.VersionDir(Version), MarkerFile.DaemonTask, DaemonTaskPath);
		return 0;
	}

	public static int InstallElevator(Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		CleanupOldTasks("Elevator", log);
		var binary = BinaryDiscovery.FindElevator(Version, log);
		if (binary == null) { log?.Invoke("could not find seashell-elevator"); return 1; }
		var (command, arguments) = BuildLaunch(binary);
		log?.Invoke($"binary: {command}{(arguments != null ? " " + arguments : "")}");
		log?.Invoke("NOTE: This registers a task with highest privileges.");
		log?.Invoke("      Requires an elevated shell to register.");
		if (!RegisterTask(ElevatorTaskPath, command, arguments, elevated: true, log))
			return 1;
		MarkerFile.Write(SeaShellPaths.VersionDir(Version), MarkerFile.ElevatorTask, ElevatorTaskPath);
		return 0;
	}

	public static int UninstallDaemon(Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		return TaskQuery.Delete(DaemonTaskPath) ? 0 : 1;
	}

	public static int UninstallElevator(Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		return TaskQuery.Delete(ElevatorTaskPath) ? 0 : 1;
	}

	/// <summary>
	/// Remove old versioned tasks for the current user. Tasks are named
	/// <c>SeaShell {component} ({user}) {version}</c>; on update the prior version's
	/// task would otherwise linger. Called from InstallDaemon/InstallElevator.
	/// </summary>
	private static void CleanupOldTasks(string component, Action<string>? log)
	{
		var user = Environment.UserName;
		var prefix = $"SeaShell {component} ({user})";
		var currentName = component == "Daemon" ? DaemonTaskName : ElevatorTaskName;

		foreach (var path in TaskQuery.EnumerateFolder())
		{
			var name = path.StartsWith(TaskQuery.Folder, StringComparison.Ordinal)
				? path[TaskQuery.Folder.Length..]
				: Path.GetFileName(path);

			if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
			if (name.Equals(currentName, StringComparison.OrdinalIgnoreCase)) continue;

			TaskQuery.End(path);
			if (TaskQuery.Delete(path))
				log?.Invoke($"Removed old task: {name}");
		}
	}

	// ── Helpers ────────────────────────────────────────────────────────

	private static bool RequireWindows(Action<string>? log)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
		log?.Invoke("Task Scheduler is Windows-only. On Linux, use systemd user services.");
		return false;
	}

	private static (string command, string? arguments) BuildLaunch(string binaryPath)
	{
		if (binaryPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			return ("dotnet", $"exec \"{binaryPath}\"");
		return (binaryPath, null);
	}

	private static bool RegisterTask(string taskPath, string command, string? arguments, bool elevated, Action<string>? log)
	{
		var xml = BuildTaskXml(command, arguments, elevated);
		var tempXml = Path.Combine(Path.GetTempPath(), $"seashell-task-{(elevated ? "elev" : "daemon")}.xml");
		try
		{
			File.WriteAllText(tempXml, xml);
			var psi = new ProcessStartInfo
			{
				FileName = "schtasks.exe",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			psi.ArgumentList.Add("/Create");
			psi.ArgumentList.Add("/TN");
			psi.ArgumentList.Add(taskPath);
			psi.ArgumentList.Add("/XML");
			psi.ArgumentList.Add(tempXml);
			psi.ArgumentList.Add("/F");

			using var proc = Process.Start(psi)!;
			var stderr = proc.StandardError.ReadToEnd();
			proc.WaitForExit(10_000);

			if (proc.ExitCode != 0)
			{
				log?.Invoke($"Failed to register '{taskPath}': {stderr.Trim()}");
				if (elevated)
					log?.Invoke("(Elevator task requires running as Administrator)");
				return false;
			}
			log?.Invoke($"Registered: {taskPath}");
			return true;
		}
		finally
		{
			try { File.Delete(tempXml); } catch { }
		}
	}

	private static bool StartTask(string taskPath, Action<string>? log)
	{
		if (TaskQuery.Run(taskPath))
		{
			log?.Invoke($"Starting {taskPath}: ok");
			return true;
		}
		log?.Invoke($"Starting {taskPath}: failed");
		return false;
	}

	private static bool EndIfRunning(string taskPath)
	{
		var state = TaskQuery.QueryState(taskPath);
		if (state == null) return false;
		if (!state.Equals("Running", StringComparison.OrdinalIgnoreCase)) return false;
		return TaskQuery.End(taskPath);
	}

	// ── Task XML ───────────────────────────────────────────────────────

	private static string BuildTaskXml(string command, string? arguments, bool elevated)
	{
		var runLevel = elevated ? "HighestAvailable" : "LeastPrivilege";
		var userId = $"{Environment.UserDomainName}\\{Environment.UserName}";

		XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

		var execContent = new System.Collections.Generic.List<XElement> { new XElement(ns + "Command", command) };
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
}
