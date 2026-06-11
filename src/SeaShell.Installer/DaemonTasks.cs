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

	// Task names carry the identity's DISPLAY name — human-readable in the Task
	// Scheduler UI, format-compatible with pre-0.6.0 names. Principals inside
	// the task XML use the SID (see BuildTaskXml).
	private static string DaemonTaskNameFor(SeaShellIdentity id) => $"SeaShell Daemon ({id.DisplayName}) {Version}";
	private static string ElevatorTaskNameFor(SeaShellIdentity id) => $"SeaShell Elevator ({id.DisplayName}) {Version}";
	private static string DaemonTaskPathFor(SeaShellIdentity id) => TaskQuery.Folder + DaemonTaskNameFor(id);
	private static string ElevatorTaskPathFor(SeaShellIdentity id) => TaskQuery.Folder + ElevatorTaskNameFor(id);

	private static string DaemonTaskPath => DaemonTaskPathFor(SeaShellIdentity.Current);
	private static string ElevatorTaskPath => ElevatorTaskPathFor(SeaShellIdentity.Current);
	private static string SystemDaemonTaskPath =>
		TaskQuery.Folder + $"SeaShell Daemon (SYSTEM) {Version}";

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

	/// <summary>Query the state of the SYSTEM-principal daemon task. Returns null if not found.</summary>
	public static string? QuerySystemDaemonTaskState() =>
		RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? TaskQuery.QueryState(SystemDaemonTaskPath) : null;

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

	public static int InstallDaemon(SeaShellIdentity target, Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		CleanupOldTasks(target, "Daemon", log);
		var binary = BinaryDiscovery.FindDaemon(Version, target.DataDir, log);
		if (binary == null) { log?.Invoke("could not find seashell-daemon"); return 1; }
		var (command, arguments) = BuildLaunch(binary);
		log?.Invoke($"binary: {command}{(arguments != null ? " " + arguments : "")}");
		var xml = BuildTaskXml(command, arguments,
			principal: TaskPrincipal.CurrentUser,
			trigger: TaskTrigger.Logon,
			runLevel: "LeastPrivilege",
			description: "SeaShell scripting daemon — persistent compilation service for C# scripts",
			userSid: TaskSid(target));
		var taskPath = DaemonTaskPathFor(target);
		if (!RegisterTaskFromXml(taskPath, xml, elevatedContext: false, log))
			return 1;
		MarkerFile.Write(SeaShellPaths.VersionDirIn(target.DataDir, Version), MarkerFile.DaemonTask, taskPath);
		return 0;
	}

	public static int InstallElevator(SeaShellIdentity target, Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		CleanupOldTasks(target, "Elevator", log);
		var binary = BinaryDiscovery.FindElevator(Version, target.DataDir, log);
		if (binary == null) { log?.Invoke("could not find seashell-elevator"); return 1; }
		var (command, arguments) = BuildLaunch(binary);
		log?.Invoke($"binary: {command}{(arguments != null ? " " + arguments : "")}");
		log?.Invoke("NOTE: This registers a task with highest privileges.");
		log?.Invoke("      Requires an elevated shell to register.");
		var xml = BuildTaskXml(command, arguments,
			principal: TaskPrincipal.CurrentUser,
			trigger: TaskTrigger.Logon,
			runLevel: "HighestAvailable",
			description: "SeaShell elevated process spawner — pre-elevated at logon for UAC-free script elevation",
			userSid: TaskSid(target));
		var taskPath = ElevatorTaskPathFor(target);
		if (!RegisterTaskFromXml(taskPath, xml, elevatedContext: true, log))
			return 1;
		MarkerFile.Write(SeaShellPaths.VersionDirIn(target.DataDir, Version), MarkerFile.ElevatorTask, taskPath);
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
	/// Register a daemon scheduled task that runs as SYSTEM at boot. Called by
	/// the elevated bootstrapper during `seashell install --system` — registering
	/// a task with an S-1-5-18 principal requires elevation, not running AS
	/// SYSTEM. Task name: <c>SeaShell Daemon (SYSTEM) {version}</c>; trigger is
	/// <c>BootTrigger</c>; principal is <c>S-1-5-18</c> with
	/// <c>HighestAvailable</c> runlevel.
	/// </summary>
	public static int InstallSystemDaemon(SeaShellIdentity target, Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		CleanupOldTasks(target, "Daemon", log);
		var binary = BinaryDiscovery.FindDaemon(Version, target.DataDir, log);
		if (binary == null) { log?.Invoke("could not find seashell-daemon"); return 1; }
		var (command, arguments) = BuildLaunch(binary);
		log?.Invoke($"binary: {command}{(arguments != null ? " " + arguments : "")}");
		var xml = BuildTaskXml(command, arguments,
			principal: TaskPrincipal.System,
			trigger: TaskTrigger.Boot,
			runLevel: "HighestAvailable",
			description: "SeaShell scripting daemon (SYSTEM) — starts at boot under the SYSTEM account.");
		if (!RegisterTaskFromXml(SystemDaemonTaskPath, xml, elevatedContext: true, log))
			return 1;
		MarkerFile.Write(SeaShellPaths.VersionDirIn(target.DataDir, Version), MarkerFile.DaemonTask, SystemDaemonTaskPath);
		return 0;
	}

	/// <summary>Try to start the SYSTEM daemon task. Requires elevation.</summary>
	public static bool TryRunSystemDaemonTask() =>
		RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TaskQuery.Run(SystemDaemonTaskPath);

	public static int UninstallSystemDaemon(Action<string>? log = null)
	{
		if (!RequireWindows(log)) return 1;
		return TaskQuery.Delete(SystemDaemonTaskPath) ? 0 : 1;
	}

	/// <summary>
	/// Remove old versioned tasks for a target identity. Tasks are named
	/// <c>SeaShell {component} ({displayName}) {version}</c>; on update the prior
	/// version's task would otherwise linger. The display-name prefix matches
	/// pre-0.6.0 task names too (same format), so upgrade cleans them up.
	/// Called from InstallDaemon/InstallElevator/InstallSystemDaemon.
	/// </summary>
	private static void CleanupOldTasks(SeaShellIdentity target, string component, Action<string>? log)
	{
		var prefix = $"SeaShell {component} ({target.DisplayName})";
		var currentName = $"{prefix} {Version}";

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

	/// <summary>
	/// SID string for task XML UserId elements. Task Scheduler accepts SIDs
	/// wherever it accepts account names, and the SID sidesteps every
	/// locale/domain/rename pitfall of <c>DOMAIN\username</c>. Uppercased to
	/// the conventional form.
	/// </summary>
	private static string TaskSid(SeaShellIdentity target) => target.Token.ToUpperInvariant();

	internal static bool RegisterTaskFromXml(string taskPath, string xml, bool elevatedContext, Action<string>? log)
	{
		var tempXml = Path.Combine(Path.GetTempPath(), $"seashell-task-{Guid.NewGuid():N}.xml");
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
				if (elevatedContext)
					log?.Invoke("(This task requires running as Administrator)");
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

	internal enum TaskPrincipal { CurrentUser, System }
	internal enum TaskTrigger { Logon, Boot, None }

	internal static string BuildTaskXml(
		string command,
		string? arguments,
		TaskPrincipal principal,
		TaskTrigger trigger,
		string runLevel,
		string description,
		string? userSid = null)
	{
		XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

		// Principal/trigger UserIds are SIDs, not DOMAIN\username — canonical,
		// locale-independent, and computable for a target identity from a
		// different security context.
		var sid = userSid ?? TaskSid(SeaShellIdentity.Current);

		XElement principalXml = principal switch
		{
			// SYSTEM: no <LogonType> element. Task Scheduler infers ServiceAccount
			// semantics from S-1-5-18 automatically and rejects an explicit
			// "ServiceAccount" LogonType as out-of-range.
			TaskPrincipal.System => new XElement(ns + "Principal",
				new XAttribute("id", "Author"),
				new XElement(ns + "UserId", "S-1-5-18"),
				new XElement(ns + "RunLevel", runLevel)),
			_ => new XElement(ns + "Principal",
				new XAttribute("id", "Author"),
				new XElement(ns + "UserId", sid),
				new XElement(ns + "LogonType", "InteractiveToken"),
				new XElement(ns + "RunLevel", runLevel)),
		};

		XElement? triggerXml = trigger switch
		{
			TaskTrigger.Boot => new XElement(ns + "BootTrigger",
				new XElement(ns + "Enabled", "true")),
			TaskTrigger.Logon => new XElement(ns + "LogonTrigger",
				new XElement(ns + "Enabled", "true"),
				new XElement(ns + "UserId", sid)),
			TaskTrigger.None => null,
			_ => throw new ArgumentOutOfRangeException(nameof(trigger)),
		};

		var execContent = new System.Collections.Generic.List<XElement> { new XElement(ns + "Command", command) };
		if (arguments != null)
			execContent.Add(new XElement(ns + "Arguments", arguments));

		var triggersElement = triggerXml != null
			? new XElement(ns + "Triggers", triggerXml)
			: new XElement(ns + "Triggers");

		var doc = new XDocument(
			new XElement(ns + "Task",
				new XAttribute("version", "1.2"),
				new XElement(ns + "RegistrationInfo",
					new XElement(ns + "Description", description)
				),
				triggersElement,
				new XElement(ns + "Principals", principalXml),
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
