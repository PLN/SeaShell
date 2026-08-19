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
var allMode = args.Any(a => a is "--all");

// --log-file / --invoker-sid: internal plumbing for the self-elevated relaunch
// of `install --all`. The elevated child tees its console output to the log
// file (the parent tails it live into the original console) and compares the
// invoker SID to detect over-the-shoulder UAC with a different account.
string? logFile = null, invokerSid = null;
{
	var filtered = new List<string>();
	for (var i = 0; i < args.Length; i++)
	{
		switch (args[i])
		{
			case "--system" or "--all": continue;
			case "--log-file" when i + 1 < args.Length: logFile = args[++i]; continue;
			case "--invoker-sid" when i + 1 < args.Length: invokerSid = args[++i]; continue;
			default: filtered.Add(args[i]); continue;
		}
	}
	args = filtered.ToArray();
}

if (logFile != null)
{
	var teeStream = new StreamWriter(File.Open(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
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
// Mutable: `install --all` runs the install flow twice (user, then SYSTEM)
// by retargeting between passes — the local functions close over these.
var target = systemMode && isWindows ? SeaShellIdentity.LocalSystem : SeaShellIdentity.Current;
var systemTarget = isWindows && target.IsSystem;
var dataDir = target.DataDir;
var versionDir = SeaShellPaths.VersionDirIn(dataDir, version);
var binDir = SeaShellPaths.BinDirIn(dataDir, version);

void SetTarget(SeaShellIdentity t)
{
	target = t;
	systemTarget = isWindows && t.IsSystem;
	dataDir = t.DataDir;
	versionDir = SeaShellPaths.VersionDirIn(dataDir, version);
	binDir = SeaShellPaths.BinDirIn(dataDir, version);
}

if (args.Length == 0)
	return await Status();

if (args[0] is "--help" or "-h")
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

switch (args[0].ToLowerInvariant())
{
	case "install":
	case "update":
		if (allMode && isWindows)
			return await InstallAll(args[0].ToLowerInvariant());
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

	// Lock is scoped to the TARGET identity — two elevated admins racing a
	// --system install contend on the same lock.
	using var installLock = InstallLock.TryAcquire(target);
	if (installLock == null)
	{
		Console.Error.WriteLine("seashell: another install or uninstall is in progress");
		return 1;
	}

	Console.WriteLine($"{{~}} SeaShell Installer v{version}{(systemMode ? " (system)" : "")}");
	Console.WriteLine();

	// 1. Platform info
	Console.WriteLine($"  Platform:  {GetCurrentRid()}");
	Console.WriteLine($"  Target:    {target.DisplayName}");
	Console.WriteLine($"  Version:   {versionDir}");

	// 2. Stop running daemons/elevators and wipe any pre-0.5.0 flat-layout state.
	//    Running daemons first so rename-then-delete in the sweep below isn't
	//    blocked by held handles on Windows.
	await StopExistingInstancesAsync();
	LegacyCleanup.SweepFilesystem(dataDir, msg => Console.WriteLine($"  {msg}"));
	LegacyCleanup.SweepStaleTempDir(msg => Console.WriteLine($"  {msg}"));
	if (systemTarget)
	{
		// Pre-0.6.0 --system installs ran `dotnet tool install` under SYSTEM's
		// profile via a transient task; remove that footprint.
		LegacyCleanup.SweepLegacySystemArtifacts(msg => Console.WriteLine($"  {msg}"));
	}
	else if (isWindows)
	{
		StripWindowsPathEntries(new[] { Path.Combine(dataDir, "bin") });
	}
	if (!systemTarget)
		LegacyCommonInstallCleanup();

	// 3. Create version directory structure
	Directory.CreateDirectory(binDir);
	Directory.CreateDirectory(SeaShellPaths.CacheDirIn(dataDir, version));
	Console.WriteLine($"  Created version directory");

	// 4. Find and extract the matching archive into bin/
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

	// 5. Set execute bits on Linux
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

	// 6. Write marker files
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

	// 7. Update `current` junction (create if missing, update if newer)
	var currentVersion = VersionDiscovery.GetCurrentVersion(dataDir);
	var myVersion = System.Version.TryParse(version, out var mv) ? mv : null;
	if (currentVersion == null || (myVersion != null && myVersion >= currentVersion))
	{
		JunctionHelper.CreateOrUpdate(SeaShellPaths.CurrentDirIn(dataDir), versionDir);
		Console.WriteLine($"  current -> v{version}");
	}
	else
	{
		Console.WriteLine($"  current -> v{currentVersion} (newer, kept)");
	}

	// 8. Add to PATH (User environment, using junction path). Skipped for the
	//    SYSTEM target — that would edit the invoking admin's HKCU, not SYSTEM's,
	//    and SYSTEM consumers find binaries via markers, not PATH.
	if (!systemTarget)
		AddToPath();

	// 8b. Refresh baked-in paths in user-scheduled tasks / crontab entries so
	//     schedules created under a prior layout keep pointing at the current
	//     install. Skip for SYSTEM — it doesn't own user-scoped schedules.
	if (isWindows && !systemTarget)
		TaskPathRefresher.RefreshWindows(msg => Console.WriteLine($"  {msg}"));
	else if (!isWindows)
		TaskPathRefresher.RefreshLinux(msg => Console.WriteLine($"  {msg}"));

	var seaPath = Path.Combine(binDir, isWindows ? "sea.exe" : "sea");
	if (!File.Exists(seaPath))
	{
		Console.Error.WriteLine("  ERROR: sea not found after extraction.");
		return 1;
	}

	// 9. Register scheduled tasks (Windows only)
	Action<string> taskLog = msg => Console.WriteLine($"  {msg}");
	if (systemTarget)
	{
		Console.WriteLine("  Registering daemon (SYSTEM, boot trigger)...");
		DaemonTasks.InstallSystemDaemon(target, taskLog);

		// Event Log source (we're elevated; SYSTEM context can too).
		try
		{
			if (!System.Diagnostics.EventLog.SourceExists("SeaShell"))
			{
				System.Diagnostics.EventLog.CreateEventSource("SeaShell", "Application");
				Console.WriteLine("  Registered Event Log source: SeaShell");
			}
		}
		catch { }

		// Boot trigger only covers the next reboot — start the daemon now so
		// the install is immediately usable (and verifiable below).
		Console.WriteLine(DaemonTasks.TryRunSystemDaemonTask()
			? "  Starting daemon (SYSTEM)..."
			: "  WARNING: could not start the SYSTEM daemon task");
	}
	else if (isWindows)
	{
		Console.WriteLine("  Registering daemon...");
		DaemonTasks.InstallDaemon(target, taskLog);

		if (IsElevated())
		{
			Console.WriteLine("  Registering elevator (elevated)...");
			DaemonTasks.InstallElevator(target, taskLog);

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

	// 10. File associations — user-context only. SYSTEM's HKCU isn't useful.
	if (isWindows && !systemTarget)
	{
		Console.WriteLine("  Registering file associations...");
		FileAssoc.Associate(".cs", seaPath, taskLog);
		var seawPath = Path.Combine(binDir, "seaw.exe");
		if (File.Exists(seawPath))
			FileAssoc.Associate(".csw", seawPath, taskLog);
	}

	// 11. Clean up old versions
	await CleanupOldVersionsAsync();

	// 12. End-to-end verification — marker contents resolve to real files,
	//     junction points where we expect, sea binary reports our version,
	//     scheduled tasks and file associations wired up correctly. For the
	//     SYSTEM target this includes waiting for the just-started daemon to
	//     come up and answer a ping (the daemon writes its pid/pipe markers
	//     under its own identity — self-reporting doubles as verification).
	Console.WriteLine();
	if (!await VerifyInstallationAsync(seaPath))
		return 1;

	Console.WriteLine();
	Console.WriteLine("  {~} Done! Run 'sea' to start.");
	return 0;
}

// ── install --all: user + elevator + SYSTEM, one elevation ─────────

// Composite install behind a single UAC prompt. Non-elevated invocations
// relaunch themselves elevated and stream the child's output back into the
// original console via a tailed log file.
async Task<int> InstallAll(string commandName)
{
	if (!IsElevated())
		return await RelaunchElevatedAsync(commandName);

	// Over-the-shoulder UAC check: if the elevated account differs from the
	// invoker, the user-profile parts would land in the wrong profile.
	var skipUserParts = invokerSid != null
		&& !string.Equals(invokerSid, SeaShellIdentity.Current.Token, StringComparison.OrdinalIgnoreCase);
	if (skipUserParts)
	{
		Console.WriteLine($"  NOTE: elevated as a different account than the invoker ({invokerSid}).");
		Console.WriteLine("        Installing SYSTEM only. For the user-profile install, run");
		Console.WriteLine("        'seashell install' as that user (elevated, for the elevator task).");
	}

	var rc = 0;
	if (!skipUserParts)
	{
		SetTarget(SeaShellIdentity.Current);
		rc = await Install();
		if (rc != 0) return rc;

		// We're elevated and both tasks are registered — bring the whole chain
		// up now: daemon first, then the elevator (it connects TO the daemon,
		// retrying every 3s, so order only affects latency).
		Console.WriteLine();
		Console.WriteLine("  Starting daemon...");
		if (!DaemonTasks.TryRunDaemonTask())
			Console.WriteLine("  WARNING: could not start the daemon task");
		else if (await VerifyDaemonLiveAsync("daemon"))
		{
			Console.WriteLine("  Starting elevator...");
			if (!DaemonTasks.TryRunElevatorTask())
				Console.WriteLine("  WARNING: could not start the elevator task");
			else
				await VerifyElevatorRegisteredAsync();
		}
	}

	Console.WriteLine();
	SetTarget(SeaShellIdentity.LocalSystem);
	var rcSystem = await Install();
	return rc != 0 ? rc : rcSystem;
}

async Task<int> RelaunchElevatedAsync(string commandName)
{
	var exe = Environment.ProcessPath;
	if (exe == null)
	{
		Console.Error.WriteLine("seashell: cannot determine own executable path for elevation.");
		return 1;
	}

	var childLog = Path.Combine(Path.GetTempPath(), $"seashell-install-{Guid.NewGuid():N}.log");
	var psi = new ProcessStartInfo
	{
		FileName = exe,
		UseShellExecute = true,        // required for the UAC verb
		Verb = "runas",
		WindowStyle = ProcessWindowStyle.Hidden,
	};
	psi.ArgumentList.Add(commandName);
	psi.ArgumentList.Add("--all");
	psi.ArgumentList.Add("--log-file");
	psi.ArgumentList.Add(childLog);
	psi.ArgumentList.Add("--invoker-sid");
	psi.ArgumentList.Add(SeaShellIdentity.Current.Token);

	Console.WriteLine("  Requesting elevation (UAC prompt)...");
	Process child;
	try
	{
		child = Process.Start(psi)!;
	}
	catch (System.ComponentModel.Win32Exception)
	{
		Console.Error.WriteLine("  ERROR: elevation was declined.");
		return 1;
	}

	await TailLogUntilExitAsync(childLog, child);
	return child.ExitCode;
}

// Stream a log file the elevated child is appending to into this console,
// until the child exits; then drain the remainder and clean up.
async Task TailLogUntilExitAsync(string logPath, Process child)
{
	long position = 0;

	async Task DrainAsync()
	{
		if (!File.Exists(logPath)) return;
		try
		{
			using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (fs.Length <= position) return;
			fs.Position = position;
			using var reader = new StreamReader(fs);
			var chunk = await reader.ReadToEndAsync();
			position = fs.Length;
			Console.Write(chunk);
		}
		catch (IOException) { } // file mid-create; retry next tick
	}

	while (!child.HasExited)
	{
		await DrainAsync();
		await Task.Delay(200);
	}
	await child.WaitForExitAsync(); // settle ExitCode
	await DrainAsync();
	try { File.Delete(logPath); } catch { }
}

// After starting the elevator task: the elevator connects to the daemon and
// registers; the daemon then reports its version in PingResponse — one check
// proving daemon up + elevator up + handshake complete.
async Task<bool> VerifyElevatorRegisteredAsync()
{
	for (var i = 0; i < 30; i++)
	{
		var pipe = MarkerFile.Read(versionDir, MarkerFile.DaemonPipe);
		if (!string.IsNullOrEmpty(pipe))
		{
			var ping = await DaemonIpc.PingAsync(pipe, timeoutMs: 1000);
			if (ping?.ElevatorVersion != null)
			{
				Console.WriteLine($"    elevator → OK (v{ping.ElevatorVersion}, registered with daemon)");
				return true;
			}
		}
		await Task.Delay(500);
	}
	Console.WriteLine("    elevator → WARNING (did not register with the daemon within 15s)");
	return false;
}

// ── Verification ───────────────────────────────────────────────────

async Task<bool> VerifyInstallationAsync(string seaPath)
{
	Console.WriteLine("  Verifying installation...");
	var ok = true;

	ok &= VerifyMarkerPointsToExistingFile(MarkerFile.DaemonPath, "daemon.path");
	ok &= VerifyMarkerPointsToExistingFile(MarkerFile.ElevatorPath, "elevator.path");

	var junctionTarget = JunctionHelper.ReadTarget(SeaShellPaths.CurrentDirIn(dataDir));
	if (junctionTarget != null &&
	    string.Equals(Path.GetFullPath(junctionTarget).TrimEnd(Path.DirectorySeparatorChar),
	                  Path.GetFullPath(versionDir).TrimEnd(Path.DirectorySeparatorChar),
	                  StringComparison.OrdinalIgnoreCase))
	{
		Console.WriteLine($"    current junction → OK (→ v{version})");
	}
	else
	{
		Console.Error.WriteLine($"    current junction → FAIL (target: {junctionTarget ?? "<missing>"})");
		ok = false;
	}

	var verResult = RunProcess(seaPath, "--version").Trim();
	if (verResult.Contains($"v{version}", StringComparison.Ordinal))
	{
		Console.WriteLine($"    sea --version → OK ({verResult})");
	}
	else
	{
		Console.Error.WriteLine($"    sea --version → FAIL (got {verResult}, expected v{version})");
		ok = false;
	}

	if (systemTarget)
	{
		var sysDaemonState = DaemonTasks.QuerySystemDaemonTaskState();
		if (sysDaemonState != null)
			Console.WriteLine($"    daemon task (SYSTEM) → OK ({sysDaemonState})");
		else
			Console.Error.WriteLine("    daemon task (SYSTEM) → FAIL (not registered)");
		ok &= sysDaemonState != null;

		// The daemon was started above; wait for it to come up and answer.
		// It writes daemon.pid/daemon.pipe under its own (SYSTEM) identity at
		// startup, and the pipe ACL admits Administrators — so an elevated
		// installer can confirm the full chain end-to-end.
		ok &= await VerifyDaemonLiveAsync("daemon (SYSTEM)");
		// No elevator and no file associations under SYSTEM — nothing else to check.
	}
	else if (isWindows)
	{
		var daemonState = DaemonTasks.QueryDaemonTaskState();
		if (daemonState != null)
			Console.WriteLine($"    daemon task → OK ({daemonState})");
		else
			Console.Error.WriteLine("    daemon task → FAIL (not registered)");
		ok &= daemonState != null;

		if (IsElevated())
		{
			var elevState = DaemonTasks.QueryElevatorTaskState();
			if (elevState != null)
				Console.WriteLine($"    elevator task → OK ({elevState})");
			else
				Console.Error.WriteLine("    elevator task → FAIL (not registered)");
			ok &= elevState != null;
		}

		ok &= VerifyFileAssociation(".cs", seaPath);
		var seawPath = Path.Combine(binDir, "seaw.exe");
		if (File.Exists(seawPath))
			ok &= VerifyFileAssociation(".csw", seawPath);
	}

	if (!ok)
	{
		Console.Error.WriteLine();
		Console.Error.WriteLine("  ERROR: Installation verification failed. Run 'seashell status' for details.");
	}
	return ok;
}

bool VerifyMarkerPointsToExistingFile(string marker, string label)
{
	var path = MarkerFile.Read(versionDir, marker);
	if (!string.IsNullOrEmpty(path) && File.Exists(path))
	{
		Console.WriteLine($"    {label} → OK ({Path.GetFileName(path)})");
		return true;
	}
	Console.Error.WriteLine($"    {label} → FAIL (marker: {path ?? "<missing>"})");
	return false;
}

// Wait for the freshly-started daemon of THIS version to write its pipe
// marker and answer a ping. ~15s budget: marker polling is cheap; a ping
// against a stale pipe (same-version reinstall) fails fast and retries.
async Task<bool> VerifyDaemonLiveAsync(string label)
{
	for (var i = 0; i < 30; i++)
	{
		var pipe = MarkerFile.Read(versionDir, MarkerFile.DaemonPipe);
		if (!string.IsNullOrEmpty(pipe))
		{
			var ping = await DaemonIpc.PingAsync(pipe, timeoutMs: 1000);
			if (ping != null && ping.Version == version)
			{
				Console.WriteLine($"    {label} → OK (pid {ping.Pid}, v{ping.Version})");
				return true;
			}
		}
		await Task.Delay(500);
	}
	Console.Error.WriteLine($"    {label} → FAIL (daemon did not come up within 15s)");
	return false;
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
bool VerifyFileAssociation(string extension, string expectedExe)
{
	var progId = $"SeaShell_{extension.TrimStart('.').Replace(".", "_")}";
	try
	{
		using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
			$@"Software\Classes\{progId}\shell\open\command", writable: false);
		var command = key?.GetValue("")?.ToString() ?? "";
		if (command.Contains(expectedExe, StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine($"    {extension} association → OK");
			return true;
		}
		Console.Error.WriteLine($"    {extension} association → FAIL (command: {command})");
		return false;
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"    {extension} association → FAIL ({ex.Message})");
		return false;
	}
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

	using var installLock = InstallLock.TryAcquire(target);
	if (installLock == null)
	{
		Console.Error.WriteLine("seashell: another install or uninstall is in progress");
		return 1;
	}

	Console.WriteLine($"{{~}} SeaShell Uninstaller{(systemMode ? " (system)" : "")}");
	Console.WriteLine();

	await StopExistingInstancesAsync();

	// Wipe any pre-0.5.0 flat-layout state so the final Directory.Delete
	// below on an empty DataDir actually succeeds.
	LegacyCleanup.SweepFilesystem(dataDir, msg => Console.WriteLine($"  {msg}"));
	LegacyCleanup.SweepStaleTempDir(msg => Console.WriteLine($"  {msg}"));
	if (systemTarget)
		LegacyCleanup.SweepLegacySystemArtifacts(msg => Console.WriteLine($"  {msg}"));

	// Remove file associations and PATH entry — user-context concerns; the
	// SYSTEM target never had either.
	if (isWindows && !systemTarget)
	{
		FileAssoc.Unassociate(".cs", msg => Console.WriteLine($"  {msg}"));
		FileAssoc.Unassociate(".csw", msg => Console.WriteLine($"  {msg}"));
	}
	if (!systemTarget)
		RemoveFromPath();

	// Remove scheduled tasks belonging to the target identity, across all
	// installed versions. First via markers (precise), then sweep the
	// \SeaShell\ folder for orphans (covers tasks left behind by a crashed
	// prior uninstall). The "({DisplayName})" prefix matches pre-0.6.0 task
	// names too, including "(SYSTEM)".
	if (isWindows)
	{
		foreach (var (ver, dir) in VersionDiscovery.EnumerateVersions(dataDir))
		{
			foreach (var marker in new[] { MarkerFile.DaemonTask, MarkerFile.ElevatorTask })
			{
				var taskPath = MarkerFile.Read(dir, marker);
				if (!string.IsNullOrEmpty(taskPath) && TaskQuery.Delete(taskPath))
					Console.WriteLine($"  Removed task: {taskPath}");
			}
		}

		var targetPrefix = $"({target.DisplayName})";
		foreach (var path in TaskQuery.EnumerateFolder().ToList())
		{
			var name = path.StartsWith(TaskQuery.Folder, StringComparison.Ordinal)
				? path[TaskQuery.Folder.Length..]
				: Path.GetFileName(path);
			var isSeaShell = name.StartsWith("SeaShell Daemon ", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("SeaShell Elevator ", StringComparison.OrdinalIgnoreCase);
			if (!isSeaShell) continue;
			if (!name.Contains(targetPrefix, StringComparison.Ordinal)) continue;

			if (TaskQuery.Delete(path))
				Console.WriteLine($"  Removed orphan task: {name}");
		}
	}

	// Remove current junction
	if (JunctionHelper.IsJunction(SeaShellPaths.CurrentDirIn(dataDir)))
	{
		try { Directory.Delete(SeaShellPaths.CurrentDirIn(dataDir), false); }
		catch { }
		Console.WriteLine("  Removed current junction");
	}

	// Remove all version directories
	foreach (var (ver, dir) in VersionDiscovery.EnumerateVersions(dataDir))
	{
		Console.WriteLine($"  Removing v{ver}...");
		try { Directory.Delete(dir, true); }
		catch (Exception ex) { Console.Error.WriteLine($"  Warning: {ex.Message}"); }
	}

	// Remove shared dirs
	var snippetsDir = Path.Combine(dataDir, "snippets");
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
	if (!systemTarget)
		Console.WriteLine("  Run 'dotnet tool uninstall -g SeaShell' to remove the bootstrapper.");
	return 0;
}

// ── Version cleanup ────────────────────────────────────────────────

async Task CleanupOldVersionsAsync()
{
	var myVersion = System.Version.TryParse(version, out var mv) ? mv : null;
	if (myVersion == null) return;

	var uninstallable = new List<(System.Version ver, string dir)>();
	foreach (var (ver, dir) in VersionDiscovery.EnumerateVersions(dataDir))
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
	if (systemMode && isWindows)
		return await SystemStatus();

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

// Status view for the SYSTEM install, runnable from any user context.
// Filesystem and ping details need elevation (systemprofile ACLs, pipe ACE);
// without it the view degrades to task state + pipe presence probing.
async Task<int> SystemStatus()
{
	Console.WriteLine("{~} SeaShell (system)");
	Console.WriteLine();

	var sys = SeaShellIdentity.LocalSystem;
	var elevated = IsElevated();

	var versions = VersionDiscovery.EnumerateVersions(sys.DataDir);
	var current = VersionDiscovery.GetCurrentVersion(sys.DataDir);
	if (versions.Count > 0)
	{
		Console.WriteLine($"  Installed    v{current?.ToString() ?? versions[0].Version.ToString()}");
		foreach (var (ver, dir) in versions)
		{
			var isCurrent = current != null && ver == current ? " *" : "";
			var daemon = MarkerFile.Exists(dir, MarkerFile.DaemonPath) ? "daemon: ok" : "daemon: -";
			Console.WriteLine($"    v{ver}{isCurrent}  {daemon}");
		}
	}
	else
	{
		Console.WriteLine(elevated
			? "  Installed    no"
			: "  Installed    unknown (run elevated for filesystem details)");
	}

	// Prefix search so a SYSTEM daemon task from any version shows up. Tasks
	// registered with a SYSTEM principal aren't readable without elevation,
	// so absence only means "not registered" when we could actually look.
	var taskPath = TaskQuery.FindByPrefix("SeaShell Daemon (SYSTEM)");
	if (taskPath != null)
		Console.WriteLine($"  daemon task  {TaskQuery.QueryState(taskPath) ?? "unknown"} ({taskPath[(taskPath.LastIndexOf('\\') + 1)..]})");
	else
		Console.WriteLine(elevated ? "  daemon task  not registered" : "  daemon task  unknown (run elevated)");

	// Live daemon: prefer a real ping via the pipe marker (works elevated on
	// 0.6.0+ daemons thanks to the Administrators ACE), fall back to raw pipe
	// probing of every non-self endpoint.
	var pinged = false;
	if (elevated && current != null)
	{
		var pipe = MarkerFile.Read(SeaShellPaths.VersionDirIn(sys.DataDir, current.ToString()), MarkerFile.DaemonPipe);
		if (!string.IsNullOrEmpty(pipe))
		{
			var ping = await DaemonIpc.PingAsync(pipe);
			if (ping != null)
			{
				Console.WriteLine($"  daemon       v{ping.Version}, pid {ping.Pid}, up {TimeSpan.FromSeconds(ping.UptimeSeconds):g}, {ping.ActiveScripts} active");
				pinged = true;
			}
		}
	}

	if (!pinged)
	{
		var self = SeaShellIdentity.Current;
		var found = 0;
		var anyAclBlocked = false;
		foreach (var ep in TransportEndpoint.EnumerateAllSeashellEndpoints())
		{
			if (self.Matches(ep.Identity)) continue;
			var state = ProbeSystemPipe(ep.Address);
			if (state.Contains("ACL", StringComparison.Ordinal)) anyAclBlocked = true;
			var kind = ep.IsElevator ? "elevator" : "daemon";
			Console.WriteLine($"  {kind,-12} v{ep.Version} ({ep.Identity}) — {state}");
			found++;
		}
		if (found == 0) Console.WriteLine("  daemon       not running");
		if (anyAclBlocked) Console.WriteLine("  (re-run under gsudo for full details)");
	}

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
	// 1. Graceful stop via IPC on every live SeaShell pipe for the TARGET
	//    identity (covers any version — legacy or new — that still holds a
	//    pipe). Matches both the SID token and the pre-0.6.0 username token,
	//    so upgrading over an old install stops its daemons too. For the
	//    SYSTEM target this works against 0.6.0+ daemons (Administrators ACE
	//    on the pipe); older SYSTEM daemons fall through to steps 2/3.
	foreach (var ep in TransportEndpoint.EnumerateAllSeashellEndpoints())
	{
		if (ep.IsElevator) continue; // elevator accepts spawn requests, not StopRequest
		if (!target.Matches(ep.Identity)) continue;
		await DaemonIpc.StopAsync(ep.Address);
	}

	// 2. Ask Task Scheduler to end any registered daemon/elevator tasks for
	//    the target (catches task-scheduler-launched daemons that aren't
	//    currently holding the IPC pipe open, and elevators where IPC stop
	//    isn't the right signal). "({DisplayName})" matches pre-0.6.0 names
	//    too, including "(SYSTEM)".
	if (isWindows)
	{
		var targetPrefix = $"({target.DisplayName})";
		foreach (var path in TaskQuery.EnumerateFolder().ToList())
		{
			var name = path.StartsWith(TaskQuery.Folder, StringComparison.Ordinal)
				? path[TaskQuery.Folder.Length..]
				: Path.GetFileName(path);
			var isSea = name.StartsWith("SeaShell Daemon ", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("SeaShell Elevator ", StringComparison.OrdinalIgnoreCase);
			if (!isSea || !name.Contains(targetPrefix, StringComparison.Ordinal)) continue;
			TaskQuery.End(path);
		}
	}

	// 3. Kill by pid markers — daemons/elevators of the target whose task is
	//    gone and who ignore IPC. The markers live in the target's version
	//    dirs and are readable (and the processes killable) by an elevated
	//    installer even when the target is SYSTEM.
	foreach (var (_, dir) in VersionDiscovery.EnumerateVersions(dataDir))
	{
		foreach (var marker in new[] { MarkerFile.DaemonPid, MarkerFile.ElevatorPid })
		{
			var pidStr = MarkerFile.Read(dir, marker);
			if (pidStr == null || !int.TryParse(pidStr, out var pid)) continue;
			try
			{
				using var proc = Process.GetProcessById(pid);
				if (proc.ProcessName.StartsWith("seashell-", StringComparison.OrdinalIgnoreCase))
					proc.Kill();
			}
			catch { } // already gone, or pid reused by something else
		}
	}

	// 4. Hard kill by process name — absolute fallback for hung daemons that
	//    ignore IPC and left no usable markers. Only for the ambient identity:
	//    name-based killing can't distinguish owners, and an elevated --system
	//    install must not take down the admin's own user daemons.
	if (!systemTarget)
	{
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

	// 5. Brief pause so Windows releases file locks before cleanup.
	await Task.Delay(500);
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
	if (isWindows)
	{
		// Drop both the current junction path and the pre-0.5.0 flat bin path.
		StripWindowsPathEntries(new[] {
			SeaShellPaths.CurrentBinDir,
			Path.Combine(SeaShellPaths.DataDir, "bin"),
		});
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

void StripWindowsPathEntries(string[] unwanted)
	=> StripWindowsPathEntriesWhere(EnvironmentVariableTarget.User,
		p => unwanted.Any(u => p.Equals(u, StringComparison.OrdinalIgnoreCase)));

void StripWindowsPathEntriesWhere(EnvironmentVariableTarget target, Func<string, bool> isUnwanted)
{
	string existingPath;
	try { existingPath = Environment.GetEnvironmentVariable("PATH", target) ?? ""; }
	catch (System.Security.SecurityException)
	{
		Console.WriteLine($"  PATH ({target}): access denied, skipping");
		return;
	}

	var parts = existingPath.Split(';').Where(p => !isUnwanted(p)).ToArray();
	var newPath = string.Join(';', parts);
	if (newPath == existingPath) return;

	try
	{
		Environment.SetEnvironmentVariable("PATH", newPath, target);
		Console.WriteLine($"  Removed stale PATH entries ({target})");
	}
	catch (System.Security.SecurityException)
	{
		Console.WriteLine($"  PATH ({target}): write denied, skipping");
	}
}

// Remove leftover pre-0.5.0 common-install state: C:\ProgramData\seashell on
// Windows, /etc/profile.d/seashell.sh on Linux. PATH entries are scrubbed
// from user scope always; from machine scope only when elevated. The on-disk
// directory/file is removed only when we can — otherwise just log.
void LegacyCommonInstallCleanup()
{
	if (isWindows)
	{
		StripWindowsPathEntriesWhere(EnvironmentVariableTarget.User, IsUnderProgramDataSeashell);
		var legacyDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
			"seashell");

		if (IsElevated())
		{
			StripWindowsPathEntriesWhere(EnvironmentVariableTarget.Machine, IsUnderProgramDataSeashell);
			if (Directory.Exists(legacyDir))
			{
				try
				{
					Directory.Delete(legacyDir, recursive: true);
					Console.WriteLine($"  Removed legacy installation: {legacyDir}");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"  Warning: could not remove {legacyDir}: {ex.Message}");
				}
			}
		}
		else if (Directory.Exists(legacyDir))
		{
			Console.WriteLine($"  NOTE: {legacyDir} still exists; re-run elevated to remove.");
		}
	}
	else
	{
		const string sysProfile = "/etc/profile.d/seashell.sh";
		if (!File.Exists(sysProfile)) return;
		if (IsElevatedOrRoot())
		{
			try
			{
				File.Delete(sysProfile);
				Console.WriteLine($"  Removed legacy: {sysProfile}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"  Warning: could not remove {sysProfile}: {ex.Message}");
			}
		}
		else
		{
			Console.WriteLine($"  NOTE: {sysProfile} exists; re-run with sudo to remove.");
		}
	}
}

static bool IsUnderProgramDataSeashell(string pathEntry)
{
	if (string.IsNullOrWhiteSpace(pathEntry)) return false;
	string expanded;
	try { expanded = Environment.ExpandEnvironmentVariables(pathEntry); }
	catch { expanded = pathEntry; }
	var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
	if (string.IsNullOrEmpty(programData)) return false;
	var root = Path.Combine(programData, "seashell");
	var trimmed = expanded.TrimEnd('\\', '/');
	return trimmed.Equals(root, StringComparison.OrdinalIgnoreCase)
		|| trimmed.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

/// <summary>
/// Duplicates console writes into a second writer (the --log-file the parent
/// of a self-elevated install tails). Char-level is enough — Console writers
/// funnel everything through Write(char)/Write(string).
/// </summary>
file sealed class TeeWriter : System.IO.TextWriter
{
	private readonly System.IO.TextWriter _a;
	private readonly System.IO.TextWriter _b;

	public TeeWriter(System.IO.TextWriter a, System.IO.TextWriter b) { _a = a; _b = b; }

	public override System.Text.Encoding Encoding => _a.Encoding;

	public override void Write(char value) { _a.Write(value); _b.Write(value); }
	public override void Write(string? value) { _a.Write(value); _b.Write(value); }
	public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
	public override void Flush() { _a.Flush(); _b.Flush(); }
}
