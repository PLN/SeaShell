using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

// ── SeaShell Bootstrapper ───────────────────────────────────────────
// Installed via: dotnet tool install -g SeaShell
// Command:       seashell install | update | uninstall | status
//
// Extracts embedded per-RID archives to the local install directory,
// adds sea/seaw to PATH, and registers daemon + file associations.

var version = typeof(Program).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
var systemMode = args.Any(a => a is "--system");
args = args.Where(a => a is not "--system").ToArray();
var installDir = systemMode && isWindows
	? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "seashell", "bin")
	: GetInstallDir();

if (args.Length == 0)
	return Status();

if (args[0] is "--help" or "-h")
{
	Console.WriteLine($"{{~}} SeaShell v{version}");
	Console.WriteLine();
	Console.WriteLine("Usage: seashell              Show installation status");
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
		return Install();

	case "uninstall":
		return Uninstall();

	case "status":
	case "--version":
	case "-v":
		return Status();

	case "start":
		return StartDaemon();

	case "stop":
		return StopDaemon();

	case "schedule":
		return ScheduleCommand(args[1..]);
	case "unschedule":
		return UnscheduleCommand(args[1..]);

	default:
		Console.Error.WriteLine($"seashell: unknown command '{args[0]}'. Use --help.");
		return 1;
}

// ── Install ─────────────────────────────────────────────────────────

int Install()
{
	if (systemMode && !IsElevatedOrRoot())
	{
		Console.Error.WriteLine("  ERROR: --system requires elevation (gsudo on Windows, sudo on Linux).");
		return 1;
	}

	Console.WriteLine($"{{~}} SeaShell Installer v{version}{(systemMode ? " (system)" : "")}");
	Console.WriteLine();

	// 1. Check .NET runtime
	Console.WriteLine($"  Platform:  {GetCurrentRid()}");
	Console.WriteLine($"  Install:   {installDir}");

	// 2. Stop daemon gracefully (it runs from its own staging dir, not installDir)
	var seaPathBefore = Path.Combine(installDir, isWindows ? "sea.exe" : "sea");
	if (File.Exists(seaPathBefore))
	{
		Console.WriteLine("  Stopping running instances...");
		try { RunProcess(seaPathBefore, "--daemon-stop"); } catch { }
	}

	// 3. Clean up .old files from previous hot upgrades
	if (Directory.Exists(installDir))
	{
		foreach (var oldFile in Directory.GetFiles(installDir, "*.old"))
		{
			try { File.Delete(oldFile); }
			catch { } // Still locked — leave for next install
		}
	}

	// 4. Find and extract the matching archive
	var rid = GetCurrentRid();
	var archiveName = $"archives.{rid}.zip";

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
	Directory.CreateDirectory(installDir);

	using (var stream = assembly.GetManifestResourceStream(resourceName)!)
	using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
	{
		var count = 0;
		foreach (var entry in zip.Entries)
		{
			if (entry.FullName.EndsWith('/')) continue;
			var dest = Path.Combine(installDir, entry.FullName);
			try
			{
				entry.ExtractToFile(dest, overwrite: true);
			}
			catch (IOException) when (isWindows && File.Exists(dest))
			{
				// File is locked by a running process — rename and install alongside.
				// The old process keeps running from the renamed file; new processes
				// use the newly extracted file. Renamed files are cleaned up on next install.
				var oldPath = dest + ".old";
				try { File.Delete(oldPath); } catch { }
				File.Move(dest, oldPath);
				entry.ExtractToFile(dest, overwrite: false);
			}
			count++;
		}
		Console.WriteLine($"  Extracted {count} files.");
	}

	// 5. Set execute bits on Linux
	if (!isWindows)
	{
		foreach (var name in new[] { "sea", "seashell-daemon", "seashell-elevator" })
		{
			var path = Path.Combine(installDir, name);
			if (File.Exists(path))
			{
				try { File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute); }
				catch { }
			}
		}
	}

	// 6. Add to PATH
	AddToPath();

	// 7. Verify
	var seaPath = Path.Combine(installDir, isWindows ? "sea.exe" : "sea");
	if (!File.Exists(seaPath))
	{
		Console.Error.WriteLine("  ERROR: sea not found after extraction.");
		return 1;
	}

	var verResult = RunProcess(seaPath, "--version");
	Console.WriteLine($"  {verResult.Trim()}");

	// 8. Stage daemon + elevator binaries and write manifest
	Console.WriteLine("  Staging daemon...");
	StageBinariesAndWriteManifest(installDir);

	// 9. Register scheduled tasks (Windows only, skip for --system)
	if (isWindows && !systemMode)
	{
		Console.WriteLine("  Registering daemon...");
		RunProcess(seaPath, "--install-daemon");

		if (IsElevated())
		{
			Console.WriteLine("  Registering elevator (elevated)...");
			RunProcess(seaPath, "--install-elevator");

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
		RunProcess(seaPath, "--associate .cs");
		var seawPath = Path.Combine(installDir, "seaw.exe");
		if (File.Exists(seawPath))
			RunProcess(seawPath, "--associate .csw");
	}

	Console.WriteLine();
	Console.WriteLine("  {~} Done! Run 'sea' to start.");
	return 0;
}

// ── Start / Stop ────────────────────────────────────────────────────

int StartDaemon()
{
	var seaPath = Path.Combine(installDir, isWindows ? "sea.exe" : "sea");
	if (!File.Exists(seaPath))
	{
		Console.Error.WriteLine("  Not installed. Run 'seashell install' first.");
		return 1;
	}

	if (isWindows)
	{
		// Try Task Scheduler first (preferred — starts as the registered task)
		var daemonTask = FindTask("SeaShell Daemon");
		if (daemonTask != null)
		{
			Console.WriteLine(RunSchedTask(daemonTask, "/Run")
				? $"  daemon:   starting ({daemonTask})"
				: "  daemon:   failed to start task");
		}
		else
		{
			// No task registered — fall back to direct daemon start
			Console.WriteLine(RunProcess(seaPath, "--daemon-start").Trim());
		}

		var elevatorTask = FindTask("SeaShell Elevator");
		if (elevatorTask != null)
		{
			Console.WriteLine(RunSchedTask(elevatorTask, "/Run")
				? $"  elevator: starting ({elevatorTask})"
				: "  elevator: failed to start task");
		}
		else
		{
			Console.WriteLine("  elevator: no task registered");
		}
	}
	else
	{
		Console.WriteLine(RunProcess(seaPath, "--daemon-start").Trim());
	}
	return 0;
}

int StopDaemon()
{
	var seaPath = Path.Combine(installDir, isWindows ? "sea.exe" : "sea");
	if (!File.Exists(seaPath))
	{
		Console.Error.WriteLine("  Not installed.");
		return 1;
	}

	// Stop daemon: try IPC first (works on all platforms), then task fallback
	var daemonStopped = RunProcess(seaPath, "--daemon-stop").Trim();
	var ipcOk = daemonStopped.Contains("stop requested", StringComparison.OrdinalIgnoreCase);
	Console.WriteLine($"  daemon:   {(ipcOk ? "stopped" : "not running")}");

	if (isWindows)
	{
		// If IPC didn't work, try ending the daemon task
		if (!ipcOk)
		{
			var daemonTask = FindTask("SeaShell Daemon");
			if (daemonTask != null && RunSchedTask(daemonTask, "/End"))
				Console.WriteLine("  daemon:   stopped (task)");
		}

		// Always try to end the elevator task
		var elevatorTask = FindTask("SeaShell Elevator");
		if (elevatorTask != null)
		{
			Console.WriteLine(RunSchedTask(elevatorTask, "/End")
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
	var seaPath = Path.Combine(installDir, isWindows ? "sea.exe" : "sea");
	if (!File.Exists(seaPath))
	{
		Console.Error.WriteLine("  Not installed. Run 'seashell install' first.");
		return 1;
	}

	if (scheduleArgs.Length == 0)
	{
		// No args → list scheduled scripts
		Console.WriteLine(RunProcess(seaPath, "--schedule-list").Trim());
		return 0;
	}

	// schedule <script.cs> <timing...>
	var allArgs = "--schedule " + string.Join(" ", scheduleArgs.Select(QuoteArg));
	var output = RunProcess(seaPath, allArgs).Trim();
	if (!string.IsNullOrEmpty(output))
		Console.WriteLine(output);
	return 0;
}

int UnscheduleCommand(string[] unschedArgs)
{
	var seaPath = Path.Combine(installDir, isWindows ? "sea.exe" : "sea");
	if (!File.Exists(seaPath))
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

int Uninstall()
{
	if (systemMode && !IsElevatedOrRoot())
	{
		Console.Error.WriteLine("  ERROR: --system requires elevation (gsudo on Windows, sudo on Linux).");
		return 1;
	}

	Console.WriteLine($"{{~}} SeaShell Uninstaller{(systemMode ? " (system)" : "")}");
	Console.WriteLine();

	StopExistingInstances();

	// Remove file associations (Windows, skip for --system)
	if (isWindows && !systemMode)
	{
		var seaPath = Path.Combine(installDir, "sea.exe");
		if (File.Exists(seaPath))
		{
			RunProcess(seaPath, "--unassociate .cs");
			RunProcess(seaPath, "--unassociate .csw");
		}
	}

	// Remove install dir
	if (Directory.Exists(installDir))
	{
		Console.WriteLine($"  Removing {installDir}...");
		try { Directory.Delete(installDir, true); }
		catch (Exception ex) { Console.Error.WriteLine($"  Warning: {ex.Message}"); }
	}

	// Remove from PATH
	RemoveFromPath();

	// Remove staged daemon/cache
	var dataDir = systemMode && isWindows
		? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "seashell")
		: isWindows
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "seashell")
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "seashell");

	if (Directory.Exists(dataDir))
	{
		Console.WriteLine($"  Removing {dataDir}...");
		try { Directory.Delete(dataDir, true); }
		catch (Exception ex) { Console.Error.WriteLine($"  Warning: {ex.Message}"); }
	}

	Console.WriteLine("  {~} Uninstalled.");
	Console.WriteLine("  Run 'dotnet tool uninstall -g SeaShell' to remove the bootstrapper.");
	return 0;
}

// ── Status ──────────────────────────────────────────────────────────

int Status()
{
	Console.WriteLine($"{{~}} SeaShell");
	Console.WriteLine();

	var seaPath = Path.Combine(installDir, isWindows ? "sea.exe" : "sea");
	var seawPath = Path.Combine(installDir, isWindows ? "seaw.exe" : "");
	var installed = File.Exists(seaPath);

	// Installation
	string? installedVersion = null;
	if (installed)
	{
		var verOutput = RunProcess(seaPath, "--version").Trim();
		// Extract version from "{~} SeaShell v0.3.17" → "0.3.17"
		installedVersion = verOutput.Contains('v')
			? verOutput[(verOutput.LastIndexOf('v') + 1)..].Trim()
			: verOutput;

		Console.WriteLine($"  Installed    v{installedVersion}");
		Console.WriteLine($"  Location     {installDir}");
		Console.WriteLine($"  Platform     {GetCurrentRid()}");
		if (isWindows && File.Exists(seawPath))
			Console.WriteLine($"  Window mode  seaw.exe");
	}
	else
	{
		Console.WriteLine("  Installed    no");
		Console.WriteLine($"  Bundled      v{version} ready to install");
	}

	Console.WriteLine($"  Bootstrapper v{version}");
	Console.WriteLine();

	// Daemon / elevator status
	if (installed)
	{
		var statusOutput = RunProcess(seaPath, "--status").Trim();
		if (!string.IsNullOrEmpty(statusOutput))
		{
			// Check if daemon reported as running (output contains "up ")
			var daemonRunning = statusOutput.Contains(", up ");
			foreach (var line in statusOutput.Split('\n'))
			{
				var trimmed = line.Trim();
				if (trimmed.Length > 0)
					Console.WriteLine($"  {trimmed}");
			}

			// If daemon isn't running, check task state directly
			if (!daemonRunning && isWindows)
			{
				var elevatorTask = FindTask("SeaShell Elevator");
				if (elevatorTask != null)
				{
					var state = QueryTaskState(elevatorTask);
					if (state != null)
						Console.WriteLine($"  elevator: task {state.ToLowerInvariant()}");
				}
			}
		}
	}
	else
	{
		Console.WriteLine("  daemon       -");
		Console.WriteLine("  elevator     -");
	}

	// Registered versions from seashell.json
	var manifestPath = Path.Combine(GetDataDir(), "seashell.json");
	if (File.Exists(manifestPath))
	{
		try
		{
			var manifestJson = JsonDocument.Parse(File.ReadAllText(manifestPath));
			if (manifestJson.RootElement.TryGetProperty("installations", out var installations))
			{
				var entries = new List<(string ver, string line)>();
				foreach (var prop in installations.EnumerateObject())
				{
					var ver = prop.Name;
					var installedAt = prop.Value.TryGetProperty("installedAt", out var at) ? at.GetString() : null;
					var daemonStatus = CheckComponentHealth(prop.Value, "daemon", "seashell-daemon");
					var elevatorStatus = CheckComponentHealth(prop.Value, "elevator", "seashell-elevator");
					var datePart = installedAt != null ? $"  {installedAt}" : "";
					entries.Add((ver, $"    v{ver}{datePart}  {daemonStatus}  {elevatorStatus}"));
				}

				if (entries.Count > 0)
				{
					Console.WriteLine();
					Console.WriteLine("  Versions:");
					entries.Sort((a, b) =>
					{
						var av = Version.TryParse(a.ver, out var va) ? va : new Version(0, 0);
						var bv = Version.TryParse(b.ver, out var vb) ? vb : new Version(0, 0);
						return bv.CompareTo(av);
					});
					foreach (var entry in entries)
						Console.WriteLine(entry.line);
				}
			}
		}
		catch { /* manifest parse failure — skip versions section */ }
	}

	Console.WriteLine();

	// Update availability
	if (installed && installedVersion != null && installedVersion != version)
		Console.WriteLine($"  Update: v{installedVersion} -> v{version}  (run 'seashell update')");
	else if (installed)
		Console.WriteLine("  Up to date");
	else
		Console.WriteLine("  Run 'seashell install' to get started");

	return 0;
}

// ── Daemon staging ─────────────────────────────────────────────────
// Stage daemon + elevator from the install directory to versioned
// data directories, and write seashell.json manifest so the Invoker
// can find the correct daemon by version lookup.

void StageBinariesAndWriteManifest(string sourceDir)
{
	var dataDir = GetDataDir(); // matches SeaShellPaths.DataDir logic
	var manifestPath = Path.Combine(dataDir, "seashell.json");
	Directory.CreateDirectory(dataDir);

	// Stage daemon
	string? daemonStagedDir = null;
	string? daemonHash = null;
	var daemonExe = Path.Combine(sourceDir, isWindows ? "seashell-daemon.exe" : "seashell-daemon");
	var daemonDll = Path.Combine(sourceDir, "seashell-daemon.dll");
	if (File.Exists(daemonExe) || File.Exists(daemonDll))
	{
		(daemonStagedDir, daemonHash) = StageBinary(sourceDir, "daemon", dataDir);
		Console.WriteLine($"  Daemon staged: {daemonHash[..8]}");
	}

	// Stage elevator
	string? elevatorStagedDir = null;
	string? elevatorHash = null;
	var elevatorExe = Path.Combine(sourceDir, isWindows ? "seashell-elevator.exe" : "seashell-elevator");
	var elevatorDll = Path.Combine(sourceDir, "seashell-elevator.dll");
	if (File.Exists(elevatorExe) || File.Exists(elevatorDll))
	{
		(elevatorStagedDir, elevatorHash) = StageBinary(sourceDir, "elevator", dataDir);
		Console.WriteLine($"  Elevator staged: {elevatorHash[..8]}");
	}

	// Read existing manifest
	var manifest = ReadManifest(manifestPath);

	// Clean up old version entries (where daemon is not running)
	var identity = Environment.UserName.ToLowerInvariant();
	var keysToRemove = new System.Collections.Generic.List<string>();
	foreach (var kvp in manifest)
	{
		if (kvp.Key == version) continue; // don't touch current version
		// Check if daemon is running by probing the socket
		var entry = kvp.Value;
		var address = entry.TryGetValue("daemon.address", out var addr) ? addr : null;
		if (address != null && IsSocketResponding(address))
			continue; // leave running daemons alone

		// Not running — clean up staged dirs
		if (entry.TryGetValue("daemon.path", out var dPath) && Directory.Exists(dPath))
			try { Directory.Delete(dPath, true); } catch { }
		if (entry.TryGetValue("elevator.path", out var ePath) && Directory.Exists(ePath))
			try { Directory.Delete(ePath, true); } catch { }

		keysToRemove.Add(kvp.Key);
	}
	foreach (var key in keysToRemove)
		manifest.Remove(key);

	// Clean orphaned staged directories not referenced by any manifest entry
	var referencedPaths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
	if (daemonStagedDir != null) referencedPaths.Add(daemonStagedDir);
	if (elevatorStagedDir != null) referencedPaths.Add(elevatorStagedDir);
	foreach (var kvp in manifest)
	{
		if (kvp.Value.TryGetValue("daemon.path", out var dp) && dp != null) referencedPaths.Add(dp);
		if (kvp.Value.TryGetValue("elevator.path", out var ep) && ep != null) referencedPaths.Add(ep);
	}
	foreach (var component in new[] { "daemon", "elevator" })
	{
		var componentDir = Path.Combine(dataDir, component);
		if (!Directory.Exists(componentDir)) continue;
		foreach (var dir in Directory.GetDirectories(componentDir))
		{
			if (!referencedPaths.Contains(dir))
			{
				try { Directory.Delete(dir, true); } catch { }
			}
		}
	}

	// Write current version entry
	var socketAddress = GetDaemonSocketAddress(identity, version);
	var entry2 = new System.Collections.Generic.Dictionary<string, string?>
	{
		["installedAt"] = DateTime.Now.ToString("yyyy.MMdd.HHmm"),
		["rid"] = GetCurrentRid(),
	};
	if (daemonStagedDir != null)
	{
		entry2["daemon.hash"] = daemonHash;
		entry2["daemon.path"] = daemonStagedDir;
		entry2["daemon.address"] = socketAddress;
	}
	if (elevatorStagedDir != null)
	{
		entry2["elevator.hash"] = elevatorHash;
		entry2["elevator.path"] = elevatorStagedDir;
	}
	manifest[version] = entry2;

	WriteManifest(manifestPath, manifest);
}

(string stagedDir, string hash) StageBinary(string sourceDir, string componentName, string dataDir)
{
	var hash = ComputeDirHash(sourceDir);
	var stageDir = Path.Combine(dataDir, componentName, hash);

	if (!Directory.Exists(stageDir))
	{
		Directory.CreateDirectory(stageDir);
		foreach (var file in Directory.GetFiles(sourceDir))
		{
			var dest = Path.Combine(stageDir, Path.GetFileName(file));
			try { File.Copy(file, dest); } catch { }
		}

		// Set execute bits on Linux
		if (!isWindows)
		{
			var apphost = Path.Combine(stageDir, componentName == "daemon" ? "seashell-daemon" : "seashell-elevator");
			if (File.Exists(apphost))
				try { File.SetUnixFileMode(apphost, File.GetUnixFileMode(apphost) | UnixFileMode.UserExecute); } catch { }
		}

		// Generate .runtimeconfig.dev.json with NuGet probing path
		var nugetCache = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
		foreach (var rc in Directory.GetFiles(stageDir, "*.runtimeconfig.json"))
		{
			var devJson = Path.ChangeExtension(rc, null) + ".dev.json";
			if (!File.Exists(devJson))
			{
				var content = $$"""
				{
				  "runtimeOptions": {
				    "additionalProbingPaths": [
				      "{{nugetCache.Replace("\\", "\\\\")}}"
				    ]
				  }
				}
				""";
				File.WriteAllText(devJson, content);
			}
		}
	}

	return (stageDir, hash);
}

string ComputeDirHash(string dir)
{
	var sb = new System.Text.StringBuilder();
	try
	{
		foreach (var f in Directory.GetFiles(dir, "*.dll").OrderBy(f => f))
		{
			var name = System.Reflection.AssemblyName.GetAssemblyName(f);
			sb.Append(name.FullName);
		}
	}
	catch { }
	var bytes = System.Security.Cryptography.SHA256.HashData(
		System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
	return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
}

string GetDaemonSocketAddress(string identity, string ver)
{
	if (isWindows)
		return $"seashell-{ver}-{identity}";
	// Linux: use XDG_RUNTIME_DIR if available, otherwise /tmp
	var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
	var dir = !string.IsNullOrEmpty(xdg) ? xdg : "/tmp";
	return Path.Combine(dir, $"seashell-{ver}-{identity}.sock");
}

bool IsSocketResponding(string address)
{
	// Quick check: does the socket file exist (Linux) or named pipe (Windows)?
	if (!isWindows)
		return File.Exists(address);
	// Windows named pipes don't have a simple file check; assume not running
	// if we're cleaning up old entries during install
	return false;
}

// ── Manifest I/O (standalone — bootstrapper doesn't reference Invoker) ──

System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string?>> ReadManifest(string path)
{
	var result = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string?>>();
	if (!File.Exists(path)) return result;

	try
	{
		var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
		if (json.RootElement.TryGetProperty("installations", out var installations))
		{
			foreach (var prop in installations.EnumerateObject())
			{
				var entry = new System.Collections.Generic.Dictionary<string, string?>();
				FlattenJson(prop.Value, "", entry);
				result[prop.Name] = entry;
			}
		}
	}
	catch { }
	return result;
}

void FlattenJson(System.Text.Json.JsonElement element, string prefix, System.Collections.Generic.Dictionary<string, string?> dict)
{
	if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
	{
		foreach (var prop in element.EnumerateObject())
		{
			var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
			FlattenJson(prop.Value, key, dict);
		}
	}
	else
	{
		dict[prefix] = element.ToString();
	}
}

void WriteManifest(string path, System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string?>> manifest)
{
	// Write in the same nested JSON format that ServiceManifest expects
	var options = new System.Text.Json.JsonWriterOptions { Indented = true };
	using var stream = File.Create(path);
	using var writer = new System.Text.Json.Utf8JsonWriter(stream, options);

	writer.WriteStartObject();
	writer.WritePropertyName("installations");
	writer.WriteStartObject();

	foreach (var (ver, entries) in manifest)
	{
		writer.WritePropertyName(ver);
		writer.WriteStartObject();

		// Group flat keys into nested objects
		var written = new System.Collections.Generic.HashSet<string>();
		foreach (var (key, value) in entries)
		{
			var parts = key.Split('.', 2);
			if (parts.Length == 2)
			{
				var objName = parts[0];
				if (written.Add(objName))
				{
					writer.WritePropertyName(objName);
					writer.WriteStartObject();
					// Write all keys under this object
					foreach (var (k2, v2) in entries)
					{
						if (k2.StartsWith(objName + "."))
						{
							writer.WritePropertyName(k2[(objName.Length + 1)..]);
							if (v2 == null) writer.WriteNullValue();
							else writer.WriteStringValue(v2);
						}
					}
					writer.WriteEndObject();
				}
			}
			else
			{
				writer.WritePropertyName(key);
				if (value == null) writer.WriteNullValue();
				else writer.WriteStringValue(value);
			}
		}

		writer.WriteEndObject();
	}

	writer.WriteEndObject();
	writer.WriteEndObject();
}

// ── Helpers ─────────────────────────────────────────────────────────

string GetDataDir()
{
	// Must match SeaShellPaths.DataDir logic exactly
	var env = Environment.GetEnvironmentVariable("SEASHELL_DATA");
	if (!string.IsNullOrEmpty(env)) return env;

	if (isWindows)
	{
		// --system → ProgramData; SYSTEM/LocalService/NetworkService → ProgramData; user → LocalAppData
		if (systemMode)
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "seashell");
		try
		{
			using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
			var sid = identity.User?.Value;
			if (sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
				return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "seashell");
		}
		catch { }
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "seashell");
	}

	// Linux: root → /var/lib/seashell, user → ~/.local/share/seashell
	try
	{
		[DllImport("libc", EntryPoint = "geteuid")]
		static extern uint GetEuid();
		if (GetEuid() == 0) return "/var/lib/seashell";
	}
	catch { }

	var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
	if (!string.IsNullOrEmpty(xdg)) return Path.Combine(xdg, "seashell");
	return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "seashell");
}

string GetInstallDir()
{
	if (isWindows)
		return Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"seashell", "bin");

	return Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".local", "share", "seashell", "bin");
}

string GetCurrentRid()
{
	if (isWindows) return "win-x64";
	// Check for musl (Alpine)
	try
	{
		var lddOutput = RunProcess("ldd", "--version");
		if (lddOutput.Contains("musl", StringComparison.OrdinalIgnoreCase))
			return "linux-musl-x64";
	}
	catch { }
	return "linux-x64";
}

void StopExistingInstances()
{
	var seaPath = Path.Combine(installDir, isWindows ? "sea.exe" : "sea");
	if (File.Exists(seaPath))
	{
		Console.WriteLine("  Stopping running instances...");
		try { RunProcess(seaPath, "--daemon-stop"); } catch { }
	}

	if (isWindows)
	{
		// Kill any remaining
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
	if (isWindows)
	{
		var target = systemMode ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;
		var label = systemMode ? "machine" : "user";
		var existingPath = Environment.GetEnvironmentVariable("PATH", target) ?? "";
		if (!existingPath.Contains(installDir, StringComparison.OrdinalIgnoreCase))
		{
			Environment.SetEnvironmentVariable("PATH", $"{installDir};{existingPath}", target);
			Console.WriteLine($"  Added {installDir} to {label} PATH");
			Console.WriteLine("  NOTE: Restart your terminal for PATH changes to take effect.");
		}
	}
	else
	{
		// Symlink into ~/.local/bin (typically already on PATH)
		var binDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".local", "bin");
		Directory.CreateDirectory(binDir);

		var seaLink = Path.Combine(binDir, "sea");
		var seaTarget = Path.Combine(installDir, "sea");
		try
		{
			if (File.Exists(seaLink)) File.Delete(seaLink);
			File.CreateSymbolicLink(seaLink, seaTarget);
			Console.WriteLine($"  Symlinked sea → {binDir}/sea");
		}
		catch (Exception ex) { Console.Error.WriteLine($"  Warning: symlink failed: {ex.Message}"); }
	}
}

void RemoveFromPath()
{
	if (isWindows)
	{
		var target = systemMode ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;
		var label = systemMode ? "machine" : "user";
		var existingPath = Environment.GetEnvironmentVariable("PATH", target) ?? "";
		var parts = existingPath.Split(';')
			.Where(p => !p.Equals(installDir, StringComparison.OrdinalIgnoreCase))
			.ToArray();
		var newPath = string.Join(';', parts);
		if (newPath != existingPath)
		{
			Environment.SetEnvironmentVariable("PATH", newPath, target);
			Console.WriteLine($"  Removed from {label} PATH");
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

// ── Task Scheduler helpers ─────────────────────────────────────────

/// <summary>
/// Check health of a daemon/elevator staging directory.
/// Returns a summary like "daemon: ok" or "daemon: BROKEN (missing 3 files)".
/// </summary>
string CheckComponentHealth(JsonElement versionEntry, string componentName, string binaryBaseName)
{
	var label = componentName;
	if (!versionEntry.TryGetProperty(componentName, out var comp))
		return $"{label}: -";

	var path = comp.TryGetProperty("path", out var p) ? p.GetString() : null;
	if (path == null || !Directory.Exists(path))
		return $"{label}: not staged";

	// Critical files that must exist for the component to start
	var required = componentName == "daemon"
		? new[] { $"{binaryBaseName}.dll", $"{binaryBaseName}.runtimeconfig.json",
		          $"{binaryBaseName}.deps.json", "SeaShell.Engine.dll", "SeaShell.Common.dll", "Serilog.dll" }
		: new[] { $"{binaryBaseName}.dll", $"{binaryBaseName}.runtimeconfig.json" };

	var missing = required.Count(f => !File.Exists(Path.Combine(path, f)));
	if (missing > 0)
	{
		var taskField = comp.TryGetProperty("scheduledTask", out var t) ? t.GetString() : null;
		var taskNote = taskField != null ? " task: stale" : "";
		return $"{label}: BROKEN (missing {missing} files){taskNote}";
	}

	return $"{label}: ok";
}

/// <summary>
/// Find a SeaShell task by component prefix (e.g. "SeaShell Daemon").
/// Returns the full task path or null if not found.
/// Matches the current user's tasks regardless of version.
/// </summary>
string? FindTask(string componentPrefix)
{
	if (!isWindows) return null;
	try
	{
		var psi = new ProcessStartInfo
		{
			FileName = "schtasks.exe",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("/Query");
		psi.ArgumentList.Add("/TN");
		psi.ArgumentList.Add("\\SeaShell\\");
		psi.ArgumentList.Add("/FO");
		psi.ArgumentList.Add("CSV");
		psi.ArgumentList.Add("/NH");

		using var proc = Process.Start(psi)!;
		var output = proc.StandardOutput.ReadToEnd();
		proc.WaitForExit(5_000);
		if (proc.ExitCode != 0) return null;

		var user = Environment.UserName;
		var prefix = $"{componentPrefix} ({user})";

		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = line.Split(',');
			if (parts.Length < 1) continue;
			var taskPath = parts[0].Trim('"', ' ', '\r');
			var taskName = taskPath.StartsWith("\\SeaShell\\")
				? taskPath["\\SeaShell\\".Length..]
				: taskPath;

			if (taskName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return taskPath;
		}
		return null;
	}
	catch { return null; }
}

/// <summary>Run a schtasks action (/Run or /End) on a task by full path.</summary>
bool RunSchedTask(string taskPath, string action)
{
	try
	{
		var psi = new ProcessStartInfo
		{
			FileName = "schtasks.exe",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add(action);
		psi.ArgumentList.Add("/TN");
		psi.ArgumentList.Add(taskPath);

		using var proc = Process.Start(psi)!;
		proc.WaitForExit(5_000);
		return proc.ExitCode == 0;
	}
	catch { return false; }
}

/// <summary>Query task state (e.g. "Running", "Ready"). Returns null if task not found.</summary>
string? QueryTaskState(string taskPath)
{
	try
	{
		var psi = new ProcessStartInfo
		{
			FileName = "schtasks.exe",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("/Query");
		psi.ArgumentList.Add("/TN");
		psi.ArgumentList.Add(taskPath);
		psi.ArgumentList.Add("/FO");
		psi.ArgumentList.Add("CSV");
		psi.ArgumentList.Add("/NH");

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
