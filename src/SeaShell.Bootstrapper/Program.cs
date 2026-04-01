using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

// ── SeaShell Bootstrapper ───────────────────────────────────────────
// Installed via: dotnet tool install -g SeaShell
// Command:       seashell install | update | uninstall | status
//
// Extracts embedded per-RID archives to the local install directory,
// adds sea/seaw to PATH, and registers daemon + file associations.

var version = typeof(Program).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
var installDir = GetInstallDir();

if (args.Length == 0)
	return Status();

if (args[0] is "--help" or "-h")
{
	Console.WriteLine($"{{~}} SeaShell v{version}");
	Console.WriteLine();
	Console.WriteLine("Usage: seashell              Show installation status");
	Console.WriteLine("       seashell install      Install or update SeaShell");
	Console.WriteLine("       seashell uninstall    Remove SeaShell binaries and PATH entry");
	Console.WriteLine("       seashell start        Start daemon (+ elevator if elevated)");
	Console.WriteLine("       seashell stop         Stop daemon and elevator");
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

	default:
		Console.Error.WriteLine($"seashell: unknown command '{args[0]}'. Use --help.");
		return 1;
}

// ── Install ─────────────────────────────────────────────────────────

int Install()
{
	Console.WriteLine($"{{~}} SeaShell Installer v{version}");
	Console.WriteLine();

	// 1. Check .NET runtime
	Console.WriteLine($"  Platform:  {GetCurrentRid()}");
	Console.WriteLine($"  Install:   {installDir}");

	// 2. Stop running instances
	StopExistingInstances();

	// 3. Find and extract the matching archive
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
			var path = Path.Combine(installDir, name);
			if (File.Exists(path))
			{
				try { File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute); }
				catch { }
			}
		}
	}

	// 5. Add to PATH
	AddToPath();

	// 6. Verify
	var seaPath = Path.Combine(installDir, isWindows ? "sea.exe" : "sea");
	if (!File.Exists(seaPath))
	{
		Console.Error.WriteLine("  ERROR: sea not found after extraction.");
		return 1;
	}

	var verResult = RunProcess(seaPath, "--version");
	Console.WriteLine($"  {verResult.Trim()}");

	// 7. Register daemon (+ elevator if elevated)
	Console.WriteLine("  Registering daemon...");
	RunProcess(seaPath, "--install-daemon");

	if (isWindows && IsElevated())
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
	else if (isWindows)
	{
		Console.WriteLine("  Elevator skipped (not elevated). Run elevated to register:");
		Console.WriteLine("    seashell install");
	}

	// 8. File associations (Windows)
	if (isWindows)
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
	Console.WriteLine(RunProcess(seaPath, "--daemon-start").Trim());
	if (isWindows && IsElevated())
	{
		Console.Write("  Starting elevator... ");
		RunProcess(seaPath, "--start"); // starts registered tasks including elevator
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
	Console.WriteLine(RunProcess(seaPath, "--daemon-stop").Trim());
	return 0;
}

// ── Uninstall ───────────────────────────────────────────────────────

int Uninstall()
{
	Console.WriteLine($"{{~}} SeaShell Uninstaller");
	Console.WriteLine();

	StopExistingInstances();

	// Remove file associations (Windows)
	if (isWindows)
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
	var dataDir = isWindows
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
			foreach (var line in statusOutput.Split('\n'))
			{
				var trimmed = line.Trim();
				if (trimmed.Length > 0)
					Console.WriteLine($"  {trimmed}");
			}
		}
	}
	else
	{
		Console.WriteLine("  daemon       -");
		Console.WriteLine("  elevator     -");
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

// ── Helpers ─────────────────────────────────────────────────────────

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
		var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
		if (!userPath.Contains(installDir, StringComparison.OrdinalIgnoreCase))
		{
			Environment.SetEnvironmentVariable("PATH", $"{installDir};{userPath}", EnvironmentVariableTarget.User);
			Console.WriteLine($"  Added {installDir} to user PATH");
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
		var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
		var parts = userPath.Split(';')
			.Where(p => !p.Equals(installDir, StringComparison.OrdinalIgnoreCase))
			.ToArray();
		var newPath = string.Join(';', parts);
		if (newPath != userPath)
		{
			Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.User);
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
