using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;
using SeaShell.Protocol;

// ── SeaShell Bootstrapper ───────────────────────────────────────────
// Installed via: dotnet tool install -g SeaShell
// Command:       seashell install | update | uninstall | status
//
// Extracts embedded per-RID archives to per-version directories,
// manages a `current` junction, and registers daemon + file associations.

var version = typeof(Program).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
var systemMode = args.Any(a => a is "--system");
args = args.Where(a => a is not "--system").ToArray();
var versionDir = SeaShellPaths.VersionDir(version);
var binDir = SeaShellPaths.BinDir(version);

if (args.Length == 0)
	return await Status();

if (args[0] is "--help" or "-h")
{
	Console.WriteLine($"{{~}} SeaShell v{version}");
	Console.WriteLine();
	Console.WriteLine("Usage: seashell              Show installation status");
	Console.WriteLine("       seashell status --system  Show system-wide installation status");
	Console.WriteLine("       seashell install      Install or update SeaShell");
	Console.WriteLine("       seashell install --system  System-wide install (requires elevation)");
	Console.WriteLine("       seashell uninstall    Remove SeaShell binaries and PATH entry");
	Console.WriteLine("       seashell uninstall --system  Remove system-wide install");
	Console.WriteLine("       seashell start        Start daemon and elevator");
	Console.WriteLine("       seashell stop         Stop daemon and elevator");
	Console.WriteLine("       seashell schedule <script.cs> <timing...>");
	Console.WriteLine("       seashell schedule     List scheduled scripts");
	Console.WriteLine("       seashell unschedule <script.cs>");
	return 0;
}

switch (args[0].ToLowerInvariant())
{
	case "install":
	case "update":
		return await Install();

	case "uninstall":
		return await Uninstall();

	case "status":
	case "--version":
	case "-v":
		return await Status();

	case "start":
		return StartDaemon();

	case "stop":
		return await StopDaemon();

	case "schedule":
		return ScheduleCommand(args[1..]);
	case "unschedule":
		return UnscheduleCommand(args[1..]);

	default:
		Console.Error.WriteLine($"seashell: unknown command '{args[0]}'. Use --help.");
		return 1;
}

// ── Install ─────────────────────────────────────────────────────────

async Task<int> Install()
{
	if (systemMode && !IsElevatedOrRoot())
	{
		Console.Error.WriteLine("  ERROR: --system requires elevation (gsudo on Windows, sudo on Linux).");
		return 1;
	}

	using var installLock = InstallLock.TryAcquire();
	if (installLock == null)
	{
		Console.Error.WriteLine("seashell: another install or uninstall is in progress");
		return 1;
	}

	Console.WriteLine($"{{~}} SeaShell Installer v{version}{(systemMode ? " (system)" : "")}");
	Console.WriteLine();

	// 1. Platform info
	Console.WriteLine($"  Platform:  {GetCurrentRid()}");
	Console.WriteLine($"  Version:   {versionDir}");

	// 2. Create version directory structure
	Directory.CreateDirectory(binDir);
	Directory.CreateDirectory(SeaShellPaths.CacheDir(version));
	Console.WriteLine($"  Created version directory");

	// 3. Find and extract the matching archive into bin/
	var rid = GetCurrentRid();
	var assembly = Assembly.GetExecutingAssembly();
	var resourceName = assembly.GetManifestResourceNames()
		.FirstOrDefault(n => n.Contains(rid, StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip"));

	if (resourceName == null)
	{
		Console.Error.WriteLine($"  ERROR: No embedded archive for {rid}");
		Console.Error.WriteLine($"  Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
		return 1;
	}

	Console.WriteLine($"  Extracting {rid} archive...");
	using (var stream = assembly.GetManifestResourceStream(resourceName)!)
	using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
	{
		var count = 0;
		foreach (var entry in zip.Entries)
		{
			if (entry.FullName.EndsWith('/')) continue;
			var dest = Path.Combine(binDir, entry.FullName);
			var destDir = Path.GetDirectoryName(dest);
			if (destDir != null) Directory.CreateDirectory(destDir);
			entry.ExtractToFile(dest, overwrite: true);
			count++;
		}
		Console.WriteLine($"  Extracted {count} files.");
	}

	// 4. Set execute bits on Linux
	if (!isWindows)
	{
		foreach (var name in new[] { "sea", "seashell-daemon", "seashell-elevator" })
		{
			var path = Path.Combine(binDir, name);
			if (File.Exists(path))
			{
				try { File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute); }
				catch { }
			}
		}
	}

	// 5. Write marker files
	var daemonExe = Path.Combine(binDir, isWindows ? "seashell-daemon.exe" : "seashell-daemon");
	var daemonDll = Path.Combine(binDir, "seashell-daemon.dll");
	if (File.Exists(daemonExe))
		MarkerFile.Write(versionDir, MarkerFile.DaemonPath, daemonExe);
	else if (File.Exists(daemonDll))
		MarkerFile.Write(versionDir, MarkerFile.DaemonPath, daemonDll);
	Console.WriteLine($"  daemon.path: {(MarkerFile.Exists(versionDir, MarkerFile.DaemonPath) ? "ok" : "not found")}");

	var elevatorExe = Path.Combine(binDir, isWindows ? "seashell-elevator.exe" : "seashell-elevator");
	var elevatorDll = Path.Combine(binDir, "seashell-elevator.dll");
	if (File.Exists(elevatorExe))
		MarkerFile.Write(versionDir, MarkerFile.ElevatorPath, elevatorExe);
	else if (File.Exists(elevatorDll))
		MarkerFile.Write(versionDir, MarkerFile.ElevatorPath, elevatorDll);
	Console.WriteLine($"  elevator.path: {(MarkerFile.Exists(versionDir, MarkerFile.ElevatorPath) ? "ok" : "not found")}");

	// 6. Update `current` junction (create if missing, update if newer)
	var currentVersion = VersionDiscovery.GetCurrentVersion();
	var myVersion = System.Version.TryParse(version, out var mv) ? mv : null;
	if (currentVersion == null || (myVersion != null && myVersion >= currentVersion))
	{
		JunctionHelper.CreateOrUpdate(SeaShellPaths.CurrentDir, versionDir);
		Console.WriteLine($"  current -> v{version}");
	}
	else
	{
		Console.WriteLine($"  current -> v{currentVersion} (newer, kept)");
	}

	// 7. Add to PATH (User environment, using junction path)
	AddToPath();

	// 8. Verify
	var seaPath = Path.Combine(binDir, isWindows ? "sea.exe" : "sea");
	if (!File.Exists(seaPath))
	{
		Console.Error.WriteLine("  ERROR: sea not found after extraction.");
		return 1;
	}

	var verResult = RunProcess(seaPath, "--version");
	Console.WriteLine($"  {verResult.Trim()}");

	// 9. Register scheduled tasks (Windows only, skip for --system)
	Action<string> taskLog = msg => Console.WriteLine($"  {msg}");
	if (isWindows && !systemMode)
	{
		Console.WriteLine("  Registering daemon...");
		DaemonTasks.InstallDaemon(taskLog);

		if (IsElevated())
		{
			Console.WriteLine("  Registering elevator (elevated)...");
			DaemonTasks.InstallElevator(taskLog);

			// Register Event Log source (requires elevation)
			try
			{
				if (!System.Diagnostics.EventLog.SourceExists("SeaShell"))
				{
					System.Diagnostics.EventLog.CreateEventSource("SeaShell", "Application");
					Console.WriteLine("  Registered Event Log source: SeaShell");
				}
			}
			catch { }
		}
		else
		{
			Console.WriteLine("  Elevator skipped (not elevated). Run elevated to register:");
			Console.WriteLine("    seashell install");
		}
	}

	// 10. File associations (Windows, skip for --system)
	if (isWindows && !systemMode)
	{
		Console.WriteLine("  Registering file associations...");
		FileAssoc.Associate(".cs", seaPath, taskLog);
		var seawPath = Path.Combine(binDir, "seaw.exe");
		if (File.Exists(seawPath))
			FileAssoc.Associate(".csw", seawPath, taskLog);
	}

	// 11. Clean up old versions
	await CleanupOldVersionsAsync();

	Console.WriteLine();
	Console.WriteLine("  {~} Done! Run 'sea' to start.");
	return 0;
}

// ── Start / Stop ────────────────────────────────────────────────────

int StartDaemon()
{
	var seaPath = FindSeaExe();
	if (seaPath == null)
	{
		Console.Error.WriteLine("  Not installed. Run 'seashell install' first.");
		return 1;
	}

	if (isWindows)
	{
		if (DaemonTasks.TryRunDaemonTask())
			Console.WriteLine("  daemon:   starting (task)");
		else
			Console.WriteLine(RunProcess(seaPath, "--daemon-start").Trim());

		if (DaemonTasks.TryRunElevatorTask())
			Console.WriteLine("  elevator: starting (task)");
		else
			Console.WriteLine("  elevator: no task registered");
	}
	else
	{
		Console.WriteLine(RunProcess(seaPath, "--daemon-start").Trim());
	}
	return 0;
}

async Task<int> StopDaemon()
{
	var seaPath = FindSeaExe();
	if (seaPath == null)
	{
		Console.Error.WriteLine("  Not installed.");
		return 1;
	}

	var daemonAddress = TransportEndpoint.GetDaemonAddress(
		TransportEndpoint.CurrentUserIdentity, version);
	var ipcOk = await DaemonIpc.StopAsync(daemonAddress);
	Console.WriteLine($"  daemon:   {(ipcOk ? "stopped" : "not running")}");

	if (isWindows)
	{
		if (!ipcOk)
		{
			var path = TaskQuery.FindByPrefix($"SeaShell Daemon ({Environment.UserName})");
			if (path != null && TaskQuery.End(path))
				Console.WriteLine("  daemon:   stopped (task)");
		}

		var elevPath = TaskQuery.FindByPrefix($"SeaShell Elevator ({Environment.UserName})");
		if (elevPath != null)
		{
			Console.WriteLine(TaskQuery.End(elevPath)
				? "  elevator: stopped"
				: "  elevator: not running");
		}
		else
		{
			Console.WriteLine("  elevator: no task registered");
		}
	}
	return 0;
}

// ── Schedule ────────────────────────────────────────────────────────

int ScheduleCommand(string[] scheduleArgs)
{
	var seaPath = FindSeaExe();
	if (seaPath == null)
	{
		Console.Error.WriteLine("  Not installed. Run 'seashell install' first.");
		return 1;
	}

	if (scheduleArgs.Length == 0)
	{
		Console.WriteLine(RunProcess(seaPath, "--schedule-list").Trim());
		return 0;
	}

	var allArgs = "--schedule " + string.Join(" ", scheduleArgs.Select(QuoteArg));
	var output = RunProcess(seaPath, allArgs).Trim();
	if (!string.IsNullOrEmpty(output))
		Console.WriteLine(output);
	return 0;
}

int UnscheduleCommand(string[] unschedArgs)
{
	var seaPath = FindSeaExe();
	if (seaPath == null)
	{
		Console.Error.WriteLine("  Not installed.");
		return 1;
	}

	if (unschedArgs.Length == 0)
	{
		Console.Error.WriteLine("Usage: seashell unschedule <script.cs>");
		return 1;
	}

	var output = RunProcess(seaPath, $"--unschedule {QuoteArg(unschedArgs[0])}").Trim();
	if (!string.IsNullOrEmpty(output))
		Console.WriteLine(output);
	return 0;
}

static string QuoteArg(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

// ── Uninstall ───────────────────────────────────────────────────────

async Task<int> Uninstall()
{
	if (systemMode && !IsElevatedOrRoot())
	{
		Console.Error.WriteLine("  ERROR: --system requires elevation (gsudo on Windows, sudo on Linux).");
		return 1;
	}

	using var installLock = InstallLock.TryAcquire();
	if (installLock == null)
	{
		Console.Error.WriteLine("seashell: another install or uninstall is in progress");
		return 1;
	}

	Console.WriteLine($"{{~}} SeaShell Uninstaller{(systemMode ? " (system)" : "")}");
	Console.WriteLine();

	await StopExistingInstancesAsync();

	// Remove file associations (Windows, skip for --system)
	if (isWindows && !systemMode)
	{
		FileAssoc.Unassociate(".cs", msg => Console.WriteLine($"  {msg}"));
		FileAssoc.Unassociate(".csw", msg => Console.WriteLine($"  {msg}"));
	}

	// Remove from PATH
	RemoveFromPath();

	// Remove scheduled tasks belonging to this user, across all installed versions.
	// First via markers (precise), then sweep the \SeaShell\ folder for orphans
	// (covers tasks left behind by a crashed prior uninstall).
	if (isWindows && !systemMode)
	{
		foreach (var (ver, dir) in VersionDiscovery.EnumerateVersions())
		{
			foreach (var marker in new[] { MarkerFile.DaemonTask, MarkerFile.ElevatorTask })
			{
				var taskPath = MarkerFile.Read(dir, marker);
				if (!string.IsNullOrEmpty(taskPath) && TaskQuery.Delete(taskPath))
					Console.WriteLine($"  Removed task: {taskPath}");
			}
		}

		var userPrefix = $"({Environment.UserName})";
		foreach (var path in TaskQuery.EnumerateFolder().ToList())
		{
			var name = path.StartsWith(TaskQuery.Folder, StringComparison.Ordinal)
				? path[TaskQuery.Folder.Length..]
				: Path.GetFileName(path);
			var isSeaShell = name.StartsWith("SeaShell Daemon ", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("SeaShell Elevator ", StringComparison.OrdinalIgnoreCase);
			if (!isSeaShell) continue;
			if (!name.Contains(userPrefix, StringComparison.Ordinal)) continue;

			if (TaskQuery.Delete(path))
				Console.WriteLine($"  Removed orphan task: {name}");
		}
	}

	// Remove current junction
	if (JunctionHelper.IsJunction(SeaShellPaths.CurrentDir))
	{
		try { Directory.Delete(SeaShellPaths.CurrentDir, false); }
		catch { }
		Console.WriteLine("  Removed current junction");
	}

	// Remove all version directories
	var dataDir = SeaShellPaths.DataDir;
	foreach (var (ver, dir) in VersionDiscovery.EnumerateVersions())
	{
		Console.WriteLine($"  Removing v{ver}...");
		try { Directory.Delete(dir, true); }
		catch (Exception ex) { Console.Error.WriteLine($"  Warning: {ex.Message}"); }
	}

	// Remove shared dirs
	var snippetsDir = SeaShellPaths.SnippetsDir;
	if (Directory.Exists(snippetsDir))
	{
		try { Directory.Delete(snippetsDir, true); } catch { }
	}

	// Remove data dir if empty
	if (Directory.Exists(dataDir))
	{
		try { Directory.Delete(dataDir, false); } // non-recursive: only if empty
		catch { }
	}

	Console.WriteLine("  {~} Uninstalled.");
	Console.WriteLine("  Run 'dotnet tool uninstall -g SeaShell' to remove the bootstrapper.");
	return 0;
}

// ── Version cleanup ────────────────────────────────────────────────

async Task CleanupOldVersionsAsync()
{
	var myVersion = System.Version.TryParse(version, out var mv) ? mv : null;
	if (myVersion == null) return;

	var uninstallable = new List<(System.Version ver, string dir)>();
	foreach (var (ver, dir) in VersionDiscovery.EnumerateVersions())
	{
		if (ver >= myVersion) continue; // never touch newer or current
		uninstallable.Add((ver, dir));
	}
	if (uninstallable.Count == 0) return;
	uninstallable.Sort((a, b) => a.ver.CompareTo(b.ver)); // oldest first

	Console.WriteLine();
	Console.WriteLine("  Cleaning up old versions:");

	foreach (var (ver, dir) in uninstallable)
	{
		Console.Write($"    v{ver}: ");

		// Step a: delete daemon.path and elevator.path markers — permanent.
		// With markers gone, BinaryDiscovery no longer returns this version
		// and new sea invocations can't start its daemon. If cleanup aborts
		// partway through (daemon still in use), the directory lingers until
		// the next install pass retries.
		MarkerFile.Delete(dir, MarkerFile.DaemonPath);
		MarkerFile.Delete(dir, MarkerFile.ElevatorPath);

		// Step b: probe the daemon. If reachable with ActiveScripts > 0, skip.
		var pipe = MarkerFile.Read(dir, MarkerFile.DaemonPipe);
		if (!string.IsNullOrEmpty(pipe))
		{
			var ping = await DaemonIpc.PingAsync(pipe);
			if (ping != null)
			{
				if (ping.ActiveScripts > 0)
				{
					Console.WriteLine($"in use ({ping.ActiveScripts} active), skipped");
					continue;
				}
				// Idle — ask politely.
				await DaemonIpc.StopAsync(pipe);
			}
			else
			{
				// Pipe marker present but unreachable — if the PID is still
				// alive, it's a hung daemon; kill it.
				var pidStr = MarkerFile.Read(dir, MarkerFile.DaemonPid);
				if (pidStr != null && int.TryParse(pidStr, out var pid))
				{
					try { Process.GetProcessById(pid).Kill(); } catch { }
				}
			}
		}

		// Step c: remove scheduled tasks registered for this version.
		if (isWindows)
		{
			var daemonTask = MarkerFile.Read(dir, MarkerFile.DaemonTask);
			if (!string.IsNullOrEmpty(daemonTask))
			{
				TaskQuery.End(daemonTask);
				TaskQuery.Delete(daemonTask);
			}
			var elevatorTask = MarkerFile.Read(dir, MarkerFile.ElevatorTask);
			if (!string.IsNullOrEmpty(elevatorTask))
			{
				TaskQuery.End(elevatorTask);
				TaskQuery.Delete(elevatorTask);
			}
		}

		// Step d: remove version directory.
		try
		{
			Directory.Delete(dir, true);
			Console.WriteLine("removed");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"failed ({ex.Message})");
		}
	}
}

// ── Status ──────────────────────────────────────────────────────────

async Task<int> Status()
{
	Console.WriteLine($"{{~}} SeaShell{(systemMode ? " (system)" : "")}");
	Console.WriteLine();

	var currentVersion = VersionDiscovery.GetCurrentVersion();
	var seaPath = FindSeaExe();
	var installed = seaPath != null;

	string? installedVersion = null;
	if (installed)
	{
		var verOutput = RunProcess(seaPath!, "--version").Trim();
		installedVersion = verOutput.Contains('v')
			? verOutput[(verOutput.LastIndexOf('v') + 1)..].Trim()
			: verOutput;

		Console.WriteLine($"  Installed    v{installedVersion}");
		Console.WriteLine($"  Current      {SeaShellPaths.CurrentDir} -> v{currentVersion}");
		Console.WriteLine($"  Platform     {GetCurrentRid()}");
	}
	else
	{
		Console.WriteLine("  Installed    no");
		Console.WriteLine($"  Bundled      v{version} ready to install");
	}

	Console.WriteLine($"  Bootstrapper v{version}");
	Console.WriteLine();

	if (installed)
	{
		if (systemMode && isWindows)
		{
			var currentUser = Environment.UserName.ToLowerInvariant();
			var foundDaemons = new List<(string ident, string ver, string state)>();
			var foundElevators = new List<(string ident, string ver, string state)>();
			var anyAclBlocked = false;

			foreach (var ep in TransportEndpoint.EnumerateAllSeashellEndpoints())
			{
				if (ep.Identity == currentUser) continue;
				var state = ProbeSystemPipe(ep.Address);
				if (state.Contains("ACL", StringComparison.Ordinal)) anyAclBlocked = true;
				(ep.IsElevator ? foundElevators : foundDaemons).Add((ep.Identity, ep.Version, state));
			}

			if (foundDaemons.Count == 0) Console.WriteLine("  daemon       not running");
			else foreach (var (ident, ver, state) in foundDaemons)
				Console.WriteLine($"  daemon       v{ver} ({ident}) — {state}");

			if (foundElevators.Count == 0) Console.WriteLine("  elevator     not running");
			else foreach (var (ident, ver, state) in foundElevators)
				Console.WriteLine($"  elevator     v{ver} ({ident}) — {state}");

			if (anyAclBlocked) Console.WriteLine("  (re-run under gsudo for full details)");
		}
		else
		{
			var statusOutput = RunProcess(seaPath!, "--status").Trim();
			if (!string.IsNullOrEmpty(statusOutput))
			{
				var daemonRunning = statusOutput.Contains(", up ");
				foreach (var line in statusOutput.Split('\n'))
				{
					var trimmed = line.Trim();
					if (trimmed.Length > 0) Console.WriteLine($"  {trimmed}");
				}

				if (!daemonRunning && isWindows)
				{
					var elevState = DaemonTasks.QueryElevatorTaskState();
					if (elevState != null) Console.WriteLine($"  elevator: task {elevState.ToLowerInvariant()}");
				}
			}
		}
	}
	else
	{
		Console.WriteLine("  daemon       -");
		Console.WriteLine("  elevator     -");
	}

	var versions = VersionDiscovery.EnumerateVersions();
	if (versions.Count > 0)
	{
		Console.WriteLine();
		Console.WriteLine("  Versions:");
		foreach (var (ver, dir) in versions)
		{
			var isCurrent = currentVersion != null && ver == currentVersion ? " *" : "";
			var daemon = MarkerFile.Exists(dir, MarkerFile.DaemonPath) ? "daemon: ok" : "daemon: -";
			var elevator = MarkerFile.Exists(dir, MarkerFile.ElevatorPath) ? "elevator: ok" : "elevator: -";
			Console.WriteLine($"    v{ver}{isCurrent}  {daemon}  {elevator}");
		}
	}

	Console.WriteLine();

	if (installed && installedVersion != null && installedVersion != version)
		Console.WriteLine($"  Update: v{installedVersion} -> v{version}  (run 'seashell update')");
	else if (installed)
		Console.WriteLine("  Up to date");
	else
		Console.WriteLine("  Run 'seashell install' to get started");

	await Task.CompletedTask;
	return 0;
}

// ── Helpers ─────────────────────────────────────────────────────────

string? FindSeaExe()
{
	var name = isWindows ? "sea.exe" : "sea";
	var path = Path.Combine(binDir, name);
	if (File.Exists(path)) return path;
	path = Path.Combine(SeaShellPaths.CurrentBinDir, name);
	if (File.Exists(path)) return path;
	return null;
}

string GetCurrentRid()
{
	if (isWindows) return "win-x64";
	try
	{
		var lddOutput = RunProcess("ldd", "--version");
		if (lddOutput.Contains("musl", StringComparison.OrdinalIgnoreCase))
			return "linux-musl-x64";
	}
	catch { }
	return "linux-x64";
}

async Task StopExistingInstancesAsync()
{
	// Try graceful stop via IPC against every installed version.
	foreach (var (ver, _) in VersionDiscovery.EnumerateVersions())
	{
		var address = TransportEndpoint.GetDaemonAddress(
			TransportEndpoint.CurrentUserIdentity, ver.ToString());
		await DaemonIpc.StopAsync(address);
	}

	if (isWindows)
	{
		try
		{
			foreach (var p in Process.GetProcessesByName("seashell-daemon"))
				try { p.Kill(); } catch { }
			foreach (var p in Process.GetProcessesByName("seashell-elevator"))
				try { p.Kill(); } catch { }
		}
		catch { }
	}
	else
	{
		try { RunProcess("pkill", "-f seashell-daemon"); } catch { }
		try { RunProcess("pkill", "-f seashell-elevator"); } catch { }
	}
}

void AddToPath()
{
	var pathDir = SeaShellPaths.CurrentBinDir;

	if (isWindows)
	{
		var target = EnvironmentVariableTarget.User;
		var existingPath = Environment.GetEnvironmentVariable("PATH", target) ?? "";
		if (!existingPath.Contains(pathDir, StringComparison.OrdinalIgnoreCase))
		{
			Environment.SetEnvironmentVariable("PATH", $"{pathDir};{existingPath}", target);
			Console.WriteLine($"  Added {pathDir} to user PATH");
			Console.WriteLine("  NOTE: Restart your terminal for PATH changes to take effect.");
		}
	}
	else
	{
		var localBin = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".local", "bin");
		Directory.CreateDirectory(localBin);

		var seaLink = Path.Combine(localBin, "sea");
		var seaTarget = Path.Combine(pathDir, "sea");
		try
		{
			if (File.Exists(seaLink)) File.Delete(seaLink);
			File.CreateSymbolicLink(seaLink, seaTarget);
			Console.WriteLine($"  Symlinked sea -> {localBin}/sea");
		}
		catch (Exception ex) { Console.Error.WriteLine($"  Warning: symlink failed: {ex.Message}"); }
	}
}

void RemoveFromPath()
{
	var pathDir = SeaShellPaths.CurrentBinDir;

	if (isWindows)
	{
		var target = EnvironmentVariableTarget.User;
		var existingPath = Environment.GetEnvironmentVariable("PATH", target) ?? "";
		var parts = existingPath.Split(';')
			.Where(p => !p.Equals(pathDir, StringComparison.OrdinalIgnoreCase))
			.ToArray();
		var newPath = string.Join(';', parts);
		if (newPath != existingPath)
		{
			Environment.SetEnvironmentVariable("PATH", newPath, target);
			Console.WriteLine("  Removed from user PATH");
		}
	}
	else
	{
		var seaLink = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".local", "bin", "sea");
		if (File.Exists(seaLink))
		{
			try { File.Delete(seaLink); Console.WriteLine("  Removed symlink"); }
			catch { }
		}
	}
}

bool IsElevated()
{
	if (!isWindows) return false;
	try
	{
		using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
		var principal = new System.Security.Principal.WindowsPrincipal(identity);
		return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
	}
	catch { return false; }
}

bool IsElevatedOrRoot()
{
	if (isWindows) return IsElevated();
	try
	{
		[DllImport("libc", EntryPoint = "geteuid")]
		static extern uint GetEuid();
		return GetEuid() == 0;
	}
	catch { return false; }
}

// Probe a named pipe without speaking the protocol. Returns a human-readable
// state: "reachable", "present (ACL-blocked)", or "not running". Used by
// `seashell status --system` to classify each enumerated pipe.
string ProbeSystemPipe(string pipeName)
{
	if (!isWindows) return "not running";
	try
	{
		using var client = new System.IO.Pipes.NamedPipeClientStream(
			".", pipeName, System.IO.Pipes.PipeDirection.InOut);
		client.Connect(200);
		return "reachable";
	}
	catch (TimeoutException)                 { return "not running"; }
	catch (System.IO.FileNotFoundException)  { return "not running"; }
	catch (UnauthorizedAccessException)      { return "present (ACL-blocked)"; }
	catch                                    { return "not running"; }
}

string RunProcess(string exe, string args)
{
	try
	{
		var psi = new ProcessStartInfo(exe, args)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		using var proc = Process.Start(psi);
		if (proc == null) return "";
		var output = proc.StandardOutput.ReadToEnd();
		var error = proc.StandardError.ReadToEnd();
		proc.WaitForExit(10_000);
		return output + error;
	}
	catch { return ""; }
}
