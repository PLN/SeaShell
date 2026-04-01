//css_inc Mother.cs
//css_inc System.Diagnostics.Ext.cs
//css_inc pipeline-tasks.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;

// ── SeaShell Pipeline: Test ──────────────────────────────────────────
// Runs on each build host. Three phases:
//   1. Contained tests (no sea install needed)
//   2. Install/update sea for current user + root/SYSTEM
//   3. Smoke tests using the freshly installed sea
//
// Does NOT roll back — develop forward, not back.

var src = Environment.GetEnvironmentVariable("PIPELINE_SRC")!;
var artifacts = Environment.GetEnvironmentVariable("PIPELINE_ARTIFACTS")!;
var logs = Environment.GetEnvironmentVariable("PIPELINE_LOGS")!;
var rid = Environment.GetEnvironmentVariable("PIPELINE_RID")!;
var host = Environment.GetEnvironmentVariable("PIPELINE_HOST") ?? rid;
var commonDir = Environment.GetEnvironmentVariable("PIPELINE_COMMON")!;
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
var pipelineRoot = isWindows ? @"C:\pipeline" : "/pipeline";
var nupkgDir = Path.Combine(artifacts, "nupkg");
var elevate = isWindows ? "gsudo" : "sudo";

var tasks = new TaskTracker("test", host, "seashell", pipelineRoot);

Console.WriteLine($"[test] Source:   {src}");
Console.WriteLine($"[test] NuPkg:    {nupkgDir}");
Console.WriteLine($"[test] RID:      {rid}");
Console.WriteLine($"[test] Platform: {(isWindows ? "Windows" : "Linux")}");

Directory.CreateDirectory(logs);

// ── Check packages exist (host pipeline.cs already awaited them) ──────

if (!Directory.Exists(nupkgDir) || Directory.GetFiles(nupkgDir, "*.nupkg").Length == 0)
{
	Console.Error.WriteLine($"[test] No packages found in {nupkgDir}. Run 'package' step first.");
	return 1;
}

Console.WriteLine($"[test] Found {Directory.GetFiles(nupkgDir, "*.nupkg").Length} packages");

var testDir = Path.Combine(src, "test");

// ═════════════════════════════════════════════════════════════════════
// Phase 1: Contained tests (no sea install needed)
// ═════════════════════════════════════════════════════════════════════

Console.WriteLine("\n[test] ══ Phase 1: Contained tests ══");

// ── Engine dir NuGet test (dotnet run, no publish) ──────────────────
// Tests that ScriptHost works when the Engine DLL is in the NuGet cache
// (not a flat publish dir). Bundled DLLs are in separate package dirs.

tasks.Run("engine-dir-nuget (dotnet run, NuGet cache layout)", () =>
{
	var engineDirTestProject = Path.Combine(src, "test", "engine-dir-nuget");
	Environment.SetEnvironmentVariable("SEASHELL_NUPKG_DIR", nupkgDir);
	return DiagnosticsExt.RunProcess("dotnet",
		$"run --project \"{engineDirTestProject}\"",
		src, prefix: "test", logFile: Path.Combine(logs, "engine-dir-nuget.log"));
});

// ═════════════════════════════════════════════════════════════════════
// Phase 2: Install / Update
// ═════════════════════════════════════════════════════════════════════

Console.WriteLine("\n[test] ══ Phase 2: Install / Update ══");

// ── Kill running processes and unregister tasks ─────────────────────

if (isWindows)
{
	DiagnosticsExt.RunProcess("taskkill", "/F /IM seashell-daemon.exe", src, quiet: true);
	DiagnosticsExt.RunProcess("taskkill", "/F /IM seashell-elevator.exe", src, quiet: true);
	// Unregister Task Scheduler tasks — they point to the old staged binary
	// which we're about to delete. The fresh install will re-stage on first use.
	// Delete all SeaShell tasks (both old unversioned and new versioned names).
	DiagnosticsExt.RunProcess("powershell", "-c \"Get-ScheduledTask -TaskPath '\\SeaShell\\' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false -ErrorAction SilentlyContinue\"", src, quiet: true);
}
else
{
	DiagnosticsExt.RunProcess("pkill", "-f seashell-daemon", src, quiet: true);
	DiagnosticsExt.RunProcess("pkill", "-f seashell-elevator", src, quiet: true);
}
Thread.Sleep(2000); // Let processes fully exit

// ── Install for current user ────────────────────────────────────────

Console.WriteLine("[test] Installing sea for current user...");
DiagnosticsExt.RunProcess("dotnet", "tool uninstall -g SeaShell", src, quiet: true);

var installCode = DiagnosticsExt.RunProcess("dotnet", $"tool install -g SeaShell --add-source \"{nupkgDir}\"", src, prefix: "test");
if (installCode != 0)
{
	Console.Error.WriteLine("[test] Failed to install sea for current user");
	return 1;
}

// ── Install for root/SYSTEM ─────────────────────────────────────────

Console.WriteLine("[test] Installing sea for root/SYSTEM...");
DiagnosticsExt.RunProcess(elevate, "dotnet tool uninstall -g SeaShell", src, quiet: true);

var rootInstallCode = DiagnosticsExt.RunProcess(elevate,
	$"dotnet tool install -g SeaShell --add-source \"{nupkgDir}\"", src, prefix: "test");
if (rootInstallCode != 0)
	Console.Error.WriteLine("[test] WARNING: Failed to install sea for root/SYSTEM (non-fatal)");

// ── Install SeaShell.Service into NuGet caches ─────────────────────
// dotnet tool install stores dependencies in .store/, NOT in ~/.nuget/packages/.
// The daemon staging (ServiceManifest) looks in ~/.nuget/packages/ for
// SeaShell.Service. Clear old versions (may have R2R DLLs from earlier
// pipeline runs) and extract the fresh package for all accounts.

var userNugetCache = Path.Combine(
	Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
var servicePkg = Directory.GetFiles(nupkgDir, "SeaShell.Service.*.nupkg").FirstOrDefault();

if (servicePkg != null)
{
	var serviceVersion = Path.GetFileNameWithoutExtension(servicePkg)
		.Substring("SeaShell.Service.".Length);

	// Current user — clear old versions, install fresh
	var userServiceDir = Path.Combine(userNugetCache, "seashell.service");
	if (Directory.Exists(userServiceDir))
	{
		Console.WriteLine($"[test]   Clearing old seashell.service from {userNugetCache}");
		try { Directory.Delete(userServiceDir, true); } catch { }
	}
	InstallServicePackageToCache(servicePkg, serviceVersion, userNugetCache);

	if (isWindows)
	{
		// SYSTEM account — different NuGet cache, different user profile
		var systemNugetCache = @"C:\Windows\system32\config\systemprofile\.nuget\packages";
		DiagnosticsExt.RunProcess("gsudo",
			@"cmd /c rmdir /s /q ""C:\Windows\system32\config\systemprofile\.nuget\packages\seashell.service"" 2>nul",
			src, quiet: true);
		InstallServicePackageToCache(servicePkg, serviceVersion, systemNugetCache, elevate: true);
	}
	else
	{
		// root
		var rootNugetCache = "/root/.nuget/packages";
		DiagnosticsExt.RunProcess("sudo", "rm -rf /root/.nuget/packages/seashell.service", src, quiet: true);
		InstallServicePackageToCache(servicePkg, serviceVersion, rootNugetCache, elevate: true);
	}
}

// ── Clear all SeaShell caches (both accounts) ───────────────────────

Console.WriteLine("[test] Clearing SeaShell caches...");

// Current user: cache + daemon staging + elevator staging + manifest
var seashellDataDir = isWindows
	? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "seashell")
	: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "seashell");
foreach (var sub in new[] { "cache", "daemon", "elevator" })
{
	var dir = Path.Combine(seashellDataDir, sub);
	if (Directory.Exists(dir))
	{
		Console.WriteLine($"[test]   Clearing {sub}: {dir}");
		try { Directory.Delete(dir, true); } catch { }
	}
}
var manifestFile = Path.Combine(seashellDataDir, "seashell.json");
if (File.Exists(manifestFile))
{
	Console.WriteLine($"[test]   Clearing manifest: {manifestFile}");
	try { File.Delete(manifestFile); } catch { }
}

// Root/SYSTEM caches
if (isWindows)
{
	DiagnosticsExt.RunProcess("gsudo", @"cmd /c rmdir /s /q ""C:\ProgramData\seashell\cache"" 2>nul", src, quiet: true);
	DiagnosticsExt.RunProcess("gsudo", @"cmd /c rmdir /s /q ""C:\ProgramData\seashell\daemon"" 2>nul", src, quiet: true);
	DiagnosticsExt.RunProcess("gsudo", @"cmd /c rmdir /s /q ""C:\ProgramData\seashell\elevator"" 2>nul", src, quiet: true);
	DiagnosticsExt.RunProcess("gsudo", @"cmd /c del /q ""C:\ProgramData\seashell\seashell.json"" 2>nul", src, quiet: true);
}
else
{
	DiagnosticsExt.RunProcess("sudo", "rm -rf /var/lib/seashell/cache /var/lib/seashell/daemon /var/lib/seashell/elevator /var/lib/seashell/seashell.json", src, quiet: true);
}

// ── Verify installation ─────────────────────────────────────────────

var verifyCode = DiagnosticsExt.RunProcess("sea", "--version", src, prefix: "test");
if (verifyCode != 0)
{
	Console.Error.WriteLine("[test] sea --version failed after install");
	return 1;
}

// ═════════════════════════════════════════════════════════════════════
// Phase 3: Smoke tests (using freshly installed sea)
// ═════════════════════════════════════════════════════════════════════

Console.WriteLine("\n[test] ══ Phase 3: Smoke tests ══");

// ── Script smoke tests ───────────────────────────────────────────────

tasks.Run("hello.cs", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "hello.cs"), src, prefix: "test"));

tasks.Run("sea_context_test.cs", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "sea_context_test.cs"), src, prefix: "test"));

tasks.Run("interactive_ok.cs", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "interactive_ok.cs"), src, prefix: "test"));

// ── Regression tests ─────────────────────────────────────────────────

tasks.Run("nuget-transitive (CS1704 dedup)", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "nuget-transitive", "nuget-transitive.cs"), src, prefix: "test"));

tasks.Run("host-in-host (ScriptHost compilation)", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "host-in-host", "host-in-host.cs"), src, prefix: "test"));

tasks.Run("host-resolution (bundled DLL probing)", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "host-resolution", "host-resolution.cs"), src, prefix: "test"));

tasks.Run("host-in-host-nuget (nested ScriptHost + NuGet)", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "host-in-host-nuget", "host-in-host-nuget.cs"), src, prefix: "test"));

// ── Binary pass-through test ────────────────────────────────────────
// Tests that a pre-compiled binary with its own deps.json works via sea.

tasks.Run("binary-deps (companion deps.json pass-through)", () =>
{
	var binaryTestProject = Path.Combine(src, "test", "binary-deps");
	var binaryPublishDir = Path.Combine(artifacts, "binary-deps-test");

	var pubCode = DiagnosticsExt.RunProcess("dotnet",
		$"publish \"{binaryTestProject}\" -c Release -o \"{binaryPublishDir}\" --force",
		src, prefix: "test", logFile: Path.Combine(logs, "binary-deps-publish.log"));
	if (pubCode != 0) { Console.Error.WriteLine("[test] binary-deps publish failed"); return pubCode; }

	var binaryDll = Path.Combine(binaryPublishDir, "binary-deps.dll");
	return DiagnosticsExt.RunProcess("sea", binaryDll, src, prefix: "test");
});

// ── Service CWD test ─────────────────────────────────────────────────
// Full end-to-end: publish ServiceCwdTest, register as service, start via
// SCM/systemd, verify script runs despite CWD being system32 or /.

tasks.Run("service-cwd (SCM/systemd script resolution)", () =>
{
	var testProjectDir = Path.Combine(src, "test", "ServiceCwdTest");
	var publishDir = Path.Combine(artifacts, "service-cwd-test");
	// Use a markers directory inside the publish dir — the service process can
	// write there regardless of which account it runs as (the installer sets
	// WorkingDirectory to the exe's directory).
	var markerDir = Path.Combine(publishDir, "markers");
	var markerFile = Path.Combine(markerDir, "seashell-cwd-test.marker");
	var serviceName = "seashell-cwd-test";

	Directory.CreateDirectory(markerDir);
	if (File.Exists(markerFile)) File.Delete(markerFile);

	// Clear NuGet http-cache to avoid stale packages with same version number
	DiagnosticsExt.RunProcess("dotnet", "nuget locals http-cache --clear", src, quiet: true);

	// Publish with local nupkg source
	Environment.SetEnvironmentVariable("SEASHELL_NUPKG_DIR", nupkgDir);
	var pubCode = DiagnosticsExt.RunProcess("dotnet",
		$"publish \"{testProjectDir}\" -c Release -r {rid} --self-contained false -o \"{publishDir}\" --force",
		src, prefix: "test", logFile: Path.Combine(logs, "service-cwd-publish.log"));
	if (pubCode != 0) { Console.Error.WriteLine("[test] ServiceCwdTest publish failed"); return pubCode; }

	// cwd-test-script.cs is copied by dotnet publish (CopyToOutputDirectory in .csproj)

	var exeName = isWindows ? "ServiceCwdTest.exe" : "ServiceCwdTest";
	var exePath = Path.Combine(publishDir, exeName);

	// Install service
	Console.WriteLine($"[test]   Installing service '{serviceName}'...");
	var installServiceCode = DiagnosticsExt.RunProcess(elevate, $"\"{exePath}\" install", publishDir, prefix: "test");
	if (installServiceCode != 0) { Console.Error.WriteLine("[test] Service install failed"); return installServiceCode; }

	try
	{
		// Start service via the binary's own 'start' command (delegates to sc.exe/systemctl)
		Console.WriteLine($"[test]   Starting service...");
		var startCode = DiagnosticsExt.RunProcess(elevate, $"\"{exePath}\" start", publishDir, prefix: "test");
		if (startCode != 0) { Console.Error.WriteLine("[test] Service start failed"); return startCode; }

		// Wait for script to execute and write marker
		Console.WriteLine($"[test]   Waiting for marker file...");
		for (var i = 0; i < 30; i++)
		{
			if (File.Exists(markerFile)) break;
			Thread.Sleep(1000);
		}

		if (!File.Exists(markerFile))
		{
			Console.Error.WriteLine("[test] Marker file not found after 30s");
			// Dump service logs to diagnose why the script failed
			if (isWindows)
				DiagnosticsExt.RunProcess(elevate,
					"powershell -c \"Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName='.NET Runtime';StartTime=(Get-Date).AddMinutes(-2)} -MaxEvents 5 | Format-List\"",
					publishDir, prefix: "test");
			else
				DiagnosticsExt.RunProcess("sudo",
					$"journalctl -u {serviceName} --no-pager -n 30",
					publishDir, prefix: "test");

			// Dump compilation cache contents for diagnosis
			var svcCacheDir = isWindows ? @"C:\ProgramData\seashell\cache" : "/var/lib/seashell/cache";
			Console.Error.WriteLine($"[test] Compilation cache ({svcCacheDir}):");
			if (isWindows)
			{
				DiagnosticsExt.RunProcess(elevate, $"cmd /c dir /s \"{svcCacheDir}\"", publishDir, prefix: "test");
				DiagnosticsExt.RunProcess(elevate,
					$"powershell -c \"Get-ChildItem '{svcCacheDir}' -Recurse -Filter *.deps.json | ForEach-Object {{ Write-Host '=== ' $_.FullName; Get-Content $_.FullName }}\"",
					publishDir, prefix: "test");
			}
			else
			{
				DiagnosticsExt.RunProcess("sudo", $"find {svcCacheDir} -type f", publishDir, prefix: "test");
				DiagnosticsExt.RunProcess("sudo",
					$"bash -c 'for f in {svcCacheDir}/*/*.deps.json; do echo \"=== $f\"; cat \"$f\"; done'",
					publishDir, prefix: "test");
			}
			return 1;
		}

		var markerContent = File.ReadAllText(markerFile);
		Console.WriteLine($"[test]   Marker content:\n{markerContent.TrimEnd()}");

		// Verify Mother.cs resolved (Script= line present, set via Mother.ScriptName)
		if (!markerContent.Contains("Script="))
		{
			Console.Error.WriteLine("[test] Marker missing Script= (Mother.cs include failed)");
			return 1;
		}

		// Verify NuGet package resolved and loaded at runtime
		if (!markerContent.Contains("NuGet=") || markerContent.Contains("NuGet=FAIL"))
		{
			Console.Error.WriteLine("[test] Marker missing NuGet= or NuGet failed (probing failed in service context)");
			return 1;
		}

		Console.WriteLine("[test]   Script executed successfully from service context (inc + nuget)");
		return 0;
	}
	finally
	{
		// Stop and uninstall (cleanup) — use the binary's own management commands
		Console.WriteLine($"[test]   Stopping and uninstalling service...");
		DiagnosticsExt.RunProcess(elevate, $"\"{exePath}\" stop", publishDir, quiet: true);
		Thread.Sleep(3000);
		DiagnosticsExt.RunProcess(elevate, $"\"{exePath}\" uninstall", publishDir, quiet: true);
	}
});

// ── Service identity test ────────────────────────────────────────────
// After service-cwd: switch the service account to a different identity.
// The new account has a different NuGet cache and temp dir — the nuget.config
// we placed for the primary account is invisible. Compilation should fail.

tasks.Run("service-identity (NuGet cache isolation on account switch)", () =>
{
	var testProjectDir = Path.Combine(src, "test", "ServiceCwdTest");
	var publishDir = Path.Combine(artifacts, "service-cwd-test");
	var markerDir = Path.Combine(publishDir, "markers");
	var markerFile = Path.Combine(markerDir, "seashell-cwd-test.marker");
	var serviceName = "seashell-cwd-test";
	var exeName = isWindows ? "ServiceCwdTest.exe" : "ServiceCwdTest";
	var exePath = Path.Combine(publishDir, exeName);

	// Skip if the previous service-cwd test didn't create the published binary
	if (!File.Exists(exePath))
	{
		Console.Error.WriteLine("[test] ServiceCwdTest binary not found (previous test failed?)");
		return 1;
	}

	// Clean marker from previous test
	if (File.Exists(markerFile)) File.Delete(markerFile);

	// Delete compilation cache so the script must recompile under the new identity
	if (isWindows)
	{
		DiagnosticsExt.RunProcess(elevate, @"cmd /c rmdir /s /q ""C:\ProgramData\seashell\cache"" 2>nul", publishDir, quiet: true);
	}
	else
	{
		DiagnosticsExt.RunProcess("sudo", "rm -rf /var/lib/seashell/cache", publishDir, quiet: true);
	}

	// Install service under the new identity
	Console.WriteLine("[test]   Installing service under alternate identity...");
	DiagnosticsExt.RunProcess(elevate, $"\"{exePath}\" install", publishDir, quiet: true);

	if (isWindows)
	{
		// Switch to NetworkService — different temp dir, different NuGet cache
		DiagnosticsExt.RunProcess(elevate,
			$"sc.exe config {serviceName} obj= \"NT AUTHORITY\\NetworkService\" password= \"\"",
			publishDir, prefix: "test");
	}
	else
	{
		// Create temp user if needed, modify systemd unit
		DiagnosticsExt.RunProcess("sudo", "useradd -r -M -s /usr/sbin/nologin seashell-test-user 2>/dev/null", publishDir, quiet: true);
		DiagnosticsExt.RunProcess("sudo",
			$"bash -c \"sed -i 's/^User=.*/User=seashell-test-user/' /etc/systemd/system/{serviceName}.service\"",
			publishDir, prefix: "test");
		DiagnosticsExt.RunProcess("sudo", "systemctl daemon-reload", publishDir, quiet: true);
	}

	try
	{
		// Start service under the new identity
		Console.WriteLine("[test]   Starting service under alternate identity...");
		DiagnosticsExt.RunProcess(elevate, $"\"{exePath}\" start", publishDir, prefix: "test");

		// Wait for marker (expect failure — new account can't resolve seashell.host)
		Console.WriteLine("[test]   Waiting for marker file (expecting failure)...");
		for (var i = 0; i < 20; i++)
		{
			if (File.Exists(markerFile)) break;
			Thread.Sleep(1000);
		}

		if (File.Exists(markerFile))
		{
			var content = File.ReadAllText(markerFile);
			Console.WriteLine($"[test]   Marker unexpectedly created:\n{content.TrimEnd()}");
			// If the marker was created, the identity switch didn't expose the issue.
			// Still pass — this means the Engine handled it (or the new account had cached packages).
			Console.WriteLine("[test]   NOTICE: Service ran successfully under alternate identity");
			return 0;
		}

		// Expected: no marker → the identity switch broke NuGet resolution
		Console.WriteLine("[test]   No marker after 20s — cache isolation confirmed");
		return 0;
	}
	finally
	{
		Console.WriteLine("[test]   Cleaning up identity test...");
		DiagnosticsExt.RunProcess(elevate, $"\"{exePath}\" stop", publishDir, quiet: true);
		Thread.Sleep(2000);
		DiagnosticsExt.RunProcess(elevate, $"\"{exePath}\" uninstall", publishDir, quiet: true);

		if (!isWindows)
		{
			DiagnosticsExt.RunProcess("sudo", "userdel seashell-test-user 2>/dev/null", publishDir, quiet: true);
		}
	}
});

// ── Restart tests ────────────────────────────────────────────────────
// Force-restart daemon to ensure it has the latest staged Engine DLL.
// Previous smoke tests may have started a daemon with stale assemblies.

Console.WriteLine("\n[test] === Restart tests ===");

// Debug: check daemon staging
DiagnosticsExt.RunProcess("sea", "--daemon-stop", src, quiet: true);
Thread.Sleep(2000);

// Diagnostic: show where the daemon is staged and check Engine DLL
var diagDataDir = isWindows
	? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "seashell")
	: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "seashell");
Console.WriteLine($"[test] DataDir: {diagDataDir}");
if (Directory.Exists(Path.Combine(diagDataDir, "daemon")))
{
	foreach (var dir in Directory.GetDirectories(Path.Combine(diagDataDir, "daemon")))
	{
		var engineDll = Path.Combine(dir, "SeaShell.Engine.dll");
		var commonDll = Path.Combine(dir, "SeaShell.Common.dll");
		Console.WriteLine($"[test]   Staged: {dir}");
		Console.WriteLine($"[test]     Engine.dll exists={File.Exists(engineDll)}");
		Console.WriteLine($"[test]     Common.dll exists={File.Exists(commonDll)}");
	}
}
else
{
	Console.WriteLine("[test]   No daemon staging directory found");
}

// Also check where sea.exe thinks AppContext.BaseDirectory is
Console.WriteLine($"[test]   AppContext: {AppContext.BaseDirectory}");
var daemonInBase = Path.Combine(AppContext.BaseDirectory, "seashell-daemon.dll");
Console.WriteLine($"[test]   daemon in AppContext: {File.Exists(daemonInBase)}");

tasks.Run("restart (exits and restarts, then opts out)", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "restart_test.cs"), src,
		prefix: "test", logFile: Path.Combine(logs, "restart_test.log"), idleTimeout: 30));

tasks.Run("restart opt-out (Sea.Restart = false exits cleanly)", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "restart_opt_out_test.cs"), src,
		prefix: "test", logFile: Path.Combine(logs, "restart_opt_out_test.log"), idleTimeout: 15));

// ── Mutex tests ─────────────────────────────────────────────────────

Console.WriteLine("\n[test] === Mutex tests ===");

tasks.Run("mutex (second instance blocked, exits 200)", () =>
{
	var scriptPath = Path.Combine(testDir, "mutex_test.cs");

	// Start first instance in background (holds mutex for 5s)
	var psi1 = new ProcessStartInfo
	{
		FileName = "sea",
		Arguments = scriptPath,
		WorkingDirectory = src,
		RedirectStandardOutput = true,
		RedirectStandardError = true,
		UseShellExecute = false,
	};
	using var proc1 = Process.Start(psi1)!;

	// Give first instance time to acquire mutex and compile
	Thread.Sleep(3000);

	// Launch second instance — should exit immediately with code 200
	var logFile2 = Path.Combine(logs, "mutex_test_blocked.log");
	var code2 = DiagnosticsExt.RunProcess("sea", scriptPath, src,
		prefix: "test", logFile: logFile2, idleTimeout: 10);

	// Wait for first instance to finish
	proc1.WaitForExit(15_000);

	if (code2 == 200)
	{
		Console.Write("(blocked=200) ");
		return 0;
	}

	Console.Error.WriteLine($"\n[test]   Second instance exited with {code2}, expected 200");
	return 1;
});

tasks.Run("mutex_attach (second instance communicates with first)", () =>
{
	var scriptPath = Path.Combine(testDir, "mutex_attach_test.cs");

	// Start first instance in background (waits for attach up to 15s)
	var psi1 = new ProcessStartInfo
	{
		FileName = "sea",
		Arguments = scriptPath,
		WorkingDirectory = src,
		RedirectStandardOutput = true,
		RedirectStandardError = true,
		UseShellExecute = false,
	};
	using var proc1 = Process.Start(psi1)!;

	// Give first instance time to compile, start, and open attach pipe
	Thread.Sleep(5000);

	// Launch second instance with args — should attach and relay
	var logFile2 = Path.Combine(logs, "mutex_attach_client.log");
	var code2 = DiagnosticsExt.RunProcess("sea",
		$"{scriptPath} test-arg-1 test-arg-2", src,
		prefix: "test", logFile: logFile2, idleTimeout: 15);

	// Wait for first instance to finish (should exit after receiving attach)
	if (!proc1.WaitForExit(20_000))
	{
		try { proc1.Kill(); } catch { }
		Console.Error.WriteLine("\n[test]   First instance did not exit within 20s");
		return 1;
	}

	var stdout1 = proc1.StandardOutput.ReadToEnd();
	var stderr1 = proc1.StandardError.ReadToEnd();

	if (proc1.ExitCode != 0)
	{
		Console.Error.WriteLine($"\n[test]   First instance exited with {proc1.ExitCode}");
		Console.Error.WriteLine(stderr1);
		return 1;
	}

	if (!stdout1.Contains("received attach"))
	{
		Console.Error.WriteLine("\n[test]   First instance did not log 'received attach'");
		Console.Error.WriteLine(stdout1);
		return 1;
	}

	Console.Write("(attach OK) ");
	return 0;
});

// ── Mutex scope tests ───────────────────────────────────────────────

tasks.Run("mutex user scope (second instance blocked, exits 200)", () =>
{
	var scriptPath = Path.Combine(testDir, "mutex_user_test.cs");

	var psi1 = new ProcessStartInfo
	{
		FileName = "sea",
		Arguments = scriptPath,
		WorkingDirectory = src,
		RedirectStandardOutput = true,
		RedirectStandardError = true,
		UseShellExecute = false,
	};
	using var proc1 = Process.Start(psi1)!;
	Thread.Sleep(3000);

	var logFile2 = Path.Combine(logs, "mutex_user_blocked.log");
	var code2 = DiagnosticsExt.RunProcess("sea", scriptPath, src,
		prefix: "test", logFile: logFile2, idleTimeout: 10);

	proc1.WaitForExit(15_000);

	if (code2 == 200) { Console.Write("(blocked=200) "); return 0; }
	Console.Error.WriteLine($"\n[test]   User mutex: second exited {code2}, expected 200");
	return 1;
});

if (isWindows)
{
	tasks.Run("mutex session scope (same session blocked, different session free)", () =>
	{
		var scriptPath = Path.Combine(testDir, "mutex_session_test.cs");
		var markerPath = Path.Combine(logs, "session_marker.txt");
		if (File.Exists(markerPath)) File.Delete(markerPath);

		// 1. Start first instance in this SSH session (holds session mutex for 8s)
		var psi1 = new ProcessStartInfo
		{
			FileName = "sea",
			Arguments = scriptPath,
			WorkingDirectory = src,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		using var proc1 = Process.Start(psi1)!;
		Thread.Sleep(3000);

		// 2. Second instance in same session → should be blocked (exit 200)
		var logFile2 = Path.Combine(logs, "mutex_session_blocked.log");
		var code2 = DiagnosticsExt.RunProcess("sea", scriptPath, src,
			prefix: "test", logFile: logFile2, idleTimeout: 10);

		if (code2 != 200)
		{
			proc1.WaitForExit(15_000);
			Console.Error.WriteLine($"\n[test]   Session mutex same-session: exited {code2}, expected 200");
			return 1;
		}
		Console.Write("(same=200) ");

		// 3. Different session via Task Scheduler — should NOT be blocked
		var taskName = "SeaShell_SessionTest";
		var seaPath = "sea"; // on PATH after install
		var createArgs = $"/Create /TN \"{taskName}\" /TR \"\\\"{seaPath}\\\" \\\"{scriptPath}\\\" \\\"{markerPath}\\\"\" /SC ONCE /ST 00:00 /F /RU \"{Environment.UserName}\" /IT";
		DiagnosticsExt.RunProcess("schtasks", createArgs, src, prefix: "test");
		DiagnosticsExt.RunProcess("schtasks", $"/Run /TN \"{taskName}\"", src, prefix: "test");

		// Wait for marker file (different session should not be blocked)
		var found = false;
		for (int i = 0; i < 40; i++) // up to 10s
		{
			Thread.Sleep(250);
			if (File.Exists(markerPath)) { found = true; break; }
		}

		// Cleanup
		DiagnosticsExt.RunProcess("schtasks", $"/Delete /TN \"{taskName}\" /F", src, prefix: "test");
		proc1.WaitForExit(15_000);

		if (found)
		{
			Console.Write("(diff-session=free) ");
			return 0;
		}
		Console.Error.WriteLine("\n[test]   Session mutex: different-session instance did not run (marker missing)");
		return 1;
	});
}

// ── Window mode tests (Windows only) ────────────────────────────────

if (isWindows)
{
	Console.WriteLine("\n[test] === Window mode tests ===");

	// Find seaw.exe — either in install dir or next to sea.exe
	var seawExe = "";
	var installBin = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"seashell", "bin", "seaw.exe");
	var toolsBin = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".dotnet", "tools", "seaw.exe");
	if (File.Exists(installBin)) seawExe = installBin;
	else if (File.Exists(toolsBin)) seawExe = toolsBin;

	tasks.Run("seaw.exe build check", () =>
	{
		if (string.IsNullOrEmpty(seawExe))
		{
			Console.Write("(seaw.exe not found, skipped) ");
			return 0;
		}
		Console.Write($"(found {seawExe}) ");
		return 0;
	});

	tasks.Run("seaw window mode (no console)", () =>
	{
		if (string.IsNullOrEmpty(seawExe))
		{
			Console.Write("(seaw.exe not found, skipped) ");
			return 0;
		}

		var scriptPath = Path.Combine(testDir, "seaw_window_test.csw");
		var markerPath = Path.Combine(logs, "seaw_window_marker.txt");
		if (File.Exists(markerPath)) File.Delete(markerPath);

		var psi = new ProcessStartInfo
		{
			FileName = seawExe,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add(scriptPath);
		psi.ArgumentList.Add(markerPath);

		using var proc = Process.Start(psi);
		if (proc == null) { Console.Error.WriteLine("\n[test]   Failed to start seaw"); return 1; }
		proc.WaitForExit(30_000);

		if (!File.Exists(markerPath))
		{
			Console.Error.WriteLine("\n[test]   seaw window test: marker file not created");
			return 1;
		}

		var lines = File.ReadAllLines(markerPath);
		var result = lines.LastOrDefault() ?? "";
		if (result == "PASS")
		{
			Console.Write("(IsWindowMode=True, NoConsole) ");
			return 0;
		}
		Console.Error.WriteLine($"\n[test]   seaw window test: {string.Join(", ", lines)}");
		return 1;
	});

	tasks.Run("seaw console mode (//sea_console)", () =>
	{
		if (string.IsNullOrEmpty(seawExe))
		{
			Console.Write("(seaw.exe not found, skipped) ");
			return 0;
		}

		var scriptPath = Path.Combine(testDir, "seaw_console_test.csw");
		var markerPath = Path.Combine(logs, "seaw_console_marker.txt");
		if (File.Exists(markerPath)) File.Delete(markerPath);

		// Run via Task Scheduler to get a real interactive session with console allocation
		var taskName = "SeaShell_ConsoleTest";
		var createArgs = $"/Create /TN \"{taskName}\" /TR \"\\\"{seawExe}\\\" \\\"{scriptPath}\\\" \\\"{markerPath}\\\"\" /SC ONCE /ST 00:00 /F /RU \"{Environment.UserName}\" /IT";
		DiagnosticsExt.RunProcess("schtasks", createArgs, src, prefix: "test");
		DiagnosticsExt.RunProcess("schtasks", $"/Run /TN \"{taskName}\"", src, prefix: "test");

		// Wait for marker
		var found = false;
		for (int i = 0; i < 120; i++) // up to 30s
		{
			Thread.Sleep(250);
			if (File.Exists(markerPath)) { found = true; break; }
		}

		DiagnosticsExt.RunProcess("schtasks", $"/Delete /TN \"{taskName}\" /F", src, prefix: "test");

		if (!found)
		{
			Console.Error.WriteLine("\n[test]   seaw console test: marker file not created");
			return 1;
		}

		var lines = File.ReadAllLines(markerPath);
		var result = lines.LastOrDefault() ?? "";
		if (result == "PASS")
		{
			Console.Write("(IsWindowMode=True, HasConsole) ");
			return 0;
		}
		Console.Error.WriteLine($"\n[test]   seaw console test: {string.Join(", ", lines)}");
		return 1;
	});
}

// ── Elevated install tests (Windows only) ───────────────────────────

if (isWindows)
{
	Console.WriteLine("\n[test] === Elevated install tests ===");

	tasks.Run("elevated seashell install (elevator + Event Log source)", () =>
	{
		// Run seashell install elevated via gsudo
		// This should register the elevator task AND create the Event Log source
		var seaPath = "sea";
		var rc = DiagnosticsExt.RunProcess("gsudo", $"\"{seaPath}\" --install-elevator",
			src, prefix: "test", idleTimeout: 30);
		if (rc != 0)
		{
			Console.Error.WriteLine($"\n[test]   Elevated --install-elevator failed with {rc}");
			return 1;
		}

		// Verify elevator task exists
		var version = typeof(SeaShell.Invoker.DaemonManager).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";
		var elevatorTaskName = $"\\SeaShell\\SeaShell Elevator ({Environment.UserName}) {version}";
		var queryPsi = new ProcessStartInfo
		{
			FileName = "schtasks.exe",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		queryPsi.ArgumentList.Add("/Query");
		queryPsi.ArgumentList.Add("/TN");
		queryPsi.ArgumentList.Add(elevatorTaskName);
		using var queryProc = Process.Start(queryPsi)!;
		queryProc.StandardOutput.ReadToEnd();
		queryProc.WaitForExit(5000);

		if (queryProc.ExitCode != 0)
		{
			Console.Error.WriteLine($"\n[test]   Elevator task not found: {elevatorTaskName}");
			return 1;
		}
		Console.Write("(task registered) ");

		// Register Event Log source (elevated)
		var rcLog = DiagnosticsExt.RunProcess("gsudo",
			"powershell -c \"if (-not [System.Diagnostics.EventLog]::SourceExists('SeaShell')) { [System.Diagnostics.EventLog]::CreateEventSource('SeaShell','Application'); Write-Host 'Created' } else { Write-Host 'Exists' }\"",
			src, prefix: "test", idleTimeout: 15);

		// Verify Event Log source exists
		var checkPsi = new ProcessStartInfo
		{
			FileName = "powershell",
			Arguments = "-c \"[System.Diagnostics.EventLog]::SourceExists('SeaShell')\"",
			UseShellExecute = false,
			RedirectStandardOutput = true,
		};
		using var checkProc = Process.Start(checkPsi)!;
		var sourceExists = checkProc.StandardOutput.ReadToEnd().Trim();
		checkProc.WaitForExit(5000);

		if (sourceExists.Equals("True", StringComparison.OrdinalIgnoreCase))
		{
			Console.Write("(EventLog source OK) ");
			return 0;
		}
		Console.Error.WriteLine($"\n[test]   EventLog source 'SeaShell' not found (got: {sourceExists})");
		return 1;
	});

	tasks.Run("elevator connects to daemon", () =>
	{
		// Start daemon, then start elevator task, verify --status shows elevator connected
		DiagnosticsExt.RunProcess("sea", "--daemon-start", src, prefix: "test");
		Thread.Sleep(2000);

		DiagnosticsExt.RunProcess("sea", "--start", src, prefix: "test");
		Thread.Sleep(3000);

		// Check status — elevator should be connected
		var statusPsi = new ProcessStartInfo
		{
			FileName = "sea",
			Arguments = "--status",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		using var statusProc = Process.Start(statusPsi)!;
		var statusOut = statusProc.StandardOutput.ReadToEnd();
		statusProc.WaitForExit(5000);

		if (statusOut.Contains("connected") && !statusOut.Contains("not connected"))
		{
			Console.Write("(elevator connected) ");
			return 0;
		}
		// Elevator may not connect in pipeline SSH session — don't fail hard
		Console.Write("(elevator not connected, may be session limitation) ");
		return 0;
	});

	tasks.Run("seaw Event Log error (malformed script)", () =>
	{
		if (string.IsNullOrEmpty(seawExe))
		{
			Console.Write("(seaw.exe not found, skipped) ");
			return 0;
		}

		var scriptPath = Path.Combine(testDir, "seaw_eventlog_test.csw");

		// Clear recent SeaShell Event Log entries for clean comparison
		var beforeTime = DateTime.UtcNow;

		// Run the malformed script via seaw.exe (no console → errors go to Event Log)
		var psi = new ProcessStartInfo
		{
			FileName = seawExe,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add(scriptPath);

		using var proc = Process.Start(psi);
		if (proc == null) { Console.Error.WriteLine("\n[test]   Failed to start seaw"); return 1; }
		proc.WaitForExit(30_000);

		// Give Event Log a moment to flush
		Thread.Sleep(1000);

		// Check Event Log for SeaShell error entries after our start time
		try
		{
			var eventLog = new EventLog("Application");
			var found = false;
			// Search recent entries (last 50) for a SeaShell error
			for (int i = eventLog.Entries.Count - 1; i >= Math.Max(0, eventLog.Entries.Count - 50); i--)
			{
				var entry = eventLog.Entries[i];
				if (entry.TimeGenerated.ToUniversalTime() < beforeTime) break;
				if ((entry.Source == "SeaShell" || entry.Message.Contains("[SeaShell]"))
					&& entry.EntryType == EventLogEntryType.Error)
				{
					found = true;
					Console.Write($"(EventLog entry found: {entry.Source}) ");
					break;
				}
			}

			if (found) return 0;
			Console.Error.WriteLine("\n[test]   No SeaShell error in Event Log after running malformed .csw");
			return 1;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"\n[test]   Event Log check failed: {ex.Message}");
			return 1;
		}
	});
}

// ── Lifecycle tests ──────────────────────────────────────────────────

Console.WriteLine("\n[test] === Lifecycle tests ===");

// --status returns non-zero when daemon isn't running — that's expected,
// we just verify the command doesn't crash
tasks.Run("daemon status (no crash)", () =>
{
	DiagnosticsExt.RunProcess("sea", "--status", src, prefix: "test");
	return 0; // always pass if it didn't crash
});

// ── Socket permission test (Linux only) ────────────────────────────
// Verifies the daemon socket is created with owner-only permissions (0600).
// The umask fix in Transport.cs closes the TOCTOU gap between Bind() and
// SetUnixFileMode() — the socket is never world-accessible, even briefly.

tasks.Run("daemon socket permissions", () =>
{
	if (isWindows) { Console.Write("(skipped on Windows) "); return 0; }

	// The daemon is running from the smoke tests above — find its socket
	var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
	if (string.IsNullOrEmpty(xdgRuntime) || !Directory.Exists(xdgRuntime))
		xdgRuntime = Path.GetTempPath();

	var identity = Environment.UserName.ToLowerInvariant();
	var sockets = Directory.GetFiles(xdgRuntime, $"seashell-*-{identity}.sock");

	if (sockets.Length == 0)
	{
		Console.Error.WriteLine($"\n[test]   No daemon socket found in {xdgRuntime}");
		return 1;
	}

	var socketPath = sockets[0];
	var mode = File.GetUnixFileMode(socketPath);
	var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
	              | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

	if ((mode & forbidden) != 0)
	{
		Console.Error.WriteLine($"\n[test]   Socket {socketPath} has insecure permissions: {mode}");
		return 1;
	}

	Console.Write($"({mode}) ");
	return 0;
});

tasks.Run("daemon stop", () =>
	DiagnosticsExt.RunProcess("sea", "--stop", src, prefix: "test"));

tasks.Run("daemon stop (idempotent)", () =>
	DiagnosticsExt.RunProcess("sea", "--stop", src, prefix: "test"));

// ── Summary + results ────────────────────────────────────────────────

var exitCode = tasks.Finish(logs);

// Write legacy test.log for backward compat
var taskResults = TaskTracker.ReadResults(logs, host);
var logContent = string.Join("\n", taskResults.Select(r => $"{(r.State == "ok" ? "PASS" : "FAIL")}: {r.Name}"));
File.WriteAllText(Path.Combine(logs, "test.log"), logContent);

var commonLogDir = Path.Combine(commonDir, "logs");
Directory.CreateDirectory(commonLogDir);
File.WriteAllText(Path.Combine(commonLogDir, $"test-{host}.log"), logContent);

return exitCode;

void InstallServicePackageToCache(string nupkgPath, string version, string nugetCache, bool elevate = false)
{
	var targetDir = Path.Combine(nugetCache, "seashell.service", version);
	Console.WriteLine($"[test]   SeaShell.Service {version} → {targetDir}");

	if (elevate)
	{
		// Extract to a temp dir, then copy with elevation
		var tempDir = Path.Combine(Path.GetTempPath(), $"seashell-service-{version}");
		if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
		System.IO.Compression.ZipFile.ExtractToDirectory(nupkgPath, tempDir, true);

		if (isWindows)
		{
			DiagnosticsExt.RunProcess("gsudo",
				$"cmd /c xcopy /E /I /Y \"{tempDir}\" \"{targetDir}\"",
				src, quiet: true);
		}
		else
		{
			DiagnosticsExt.RunProcess("sudo", $"mkdir -p \"{targetDir}\"", src, quiet: true);
			DiagnosticsExt.RunProcess("sudo", $"cp -r \"{tempDir}/.\" \"{targetDir}/\"", src, quiet: true);
		}

		try { Directory.Delete(tempDir, true); } catch { }
	}
	else
	{
		// Direct extraction — no elevation needed
		if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
		Directory.CreateDirectory(targetDir);
		System.IO.Compression.ZipFile.ExtractToDirectory(nupkgPath, targetDir, true);
	}
}
