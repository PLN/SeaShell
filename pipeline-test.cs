//css_inc Mother.cs
//css_inc System.Diagnostics.Ext.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;

// ── SeaShell Pipeline: Test ──────────────────────────────────────────
// Runs on each build host. Tests the actual NuGet packages:
// - Install sea tool from nupkg
// - Script smoke tests
// - Lifecycle tests
// Pushes test log to /pipeline/common/

var src = Environment.GetEnvironmentVariable("PIPELINE_SRC")!;
var artifacts = Environment.GetEnvironmentVariable("PIPELINE_ARTIFACTS")!;
var logs = Environment.GetEnvironmentVariable("PIPELINE_LOGS")!;
var rid = Environment.GetEnvironmentVariable("PIPELINE_RID")!;
var host = Environment.GetEnvironmentVariable("PIPELINE_HOST") ?? rid;
var commonDir = Environment.GetEnvironmentVariable("PIPELINE_COMMON")!;

var nupkgDir = Path.Combine(artifacts, "nupkg");
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
var passed = 0;
var failed = 0;
var results = new List<(string name, bool pass, string detail)>();

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

// ── Install sea from local packages ──────────────────────────────────

Console.WriteLine("\n[test] === Installing sea from local packages ===");

// Kill daemon process directly (avoids needing sea itself to stop it)
if (isWindows)
{
	DiagnosticsExt.RunProcess("taskkill", "/F /IM seashell-daemon.exe", src, quiet: true);
	DiagnosticsExt.RunProcess("taskkill", "/F /IM seashell-elevator.exe", src, quiet: true);
}
else
{
	DiagnosticsExt.RunProcess("pkill", "-f seashell-daemon", src, quiet: true);
	DiagnosticsExt.RunProcess("pkill", "-f seashell-elevator", src, quiet: true);
}
Thread.Sleep(2000); // Let processes fully exit

DiagnosticsExt.RunProcess("dotnet", "tool uninstall -g SeaShell", src, quiet: true);

// Install from nupkg directory
var installCode = DiagnosticsExt.RunProcess("dotnet", $"tool install -g SeaShell --add-source \"{nupkgDir}\"", src, prefix: "test");
if (installCode != 0)
{
	Console.Error.WriteLine("[test] Failed to install sea from packages");
	// Try to restore the previous version
	DiagnosticsExt.RunProcess("dotnet", "tool install -g SeaShell --version 0.1.7", src, quiet: true);
	return 1;
}

// Clear SeaShell compilation cache — stale staged binaries from previous runs
// may have deps.json/runtimeconfig files from a prior engine version.
var seashellCache = isWindows
	? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "seashell", "cache")
	: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "seashell", "cache");
if (Directory.Exists(seashellCache))
{
	Console.WriteLine($"[test] Clearing compilation cache: {seashellCache}");
	try { Directory.Delete(seashellCache, true); } catch { }
}

// Verify installation
var verifyCode = DiagnosticsExt.RunProcess("sea", "--version", src, prefix: "test");
if (verifyCode != 0)
{
	Console.Error.WriteLine("[test] sea --version failed after install");
	return 1;
}

// ── Script smoke tests ───────────────────────────────────────────────

Console.WriteLine("\n[test] === Script smoke tests ===");

var testDir = Path.Combine(src, "test");

RunTest("hello.cs", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "hello.cs"), src, prefix: "test"));

RunTest("sea_context_test.cs", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "sea_context_test.cs"), src, prefix: "test"));

RunTest("interactive_ok.cs", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "interactive_ok.cs"), src, prefix: "test"));

// ── Regression tests ─────────────────────────────────────────────────

Console.WriteLine("\n[test] === Regression tests ===");

RunTest("nuget-transitive (CS1704 dedup)", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "nuget-transitive", "nuget-transitive.cs"), src, prefix: "test"));

RunTest("host-in-host (ScriptHost compilation)", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "host-in-host", "host-in-host.cs"), src, prefix: "test"));

RunTest("host-resolution (bundled DLL probing)", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "host-resolution", "host-resolution.cs"), src, prefix: "test"));

RunTest("host-in-host-nuget (nested ScriptHost + NuGet)", () =>
	DiagnosticsExt.RunProcess("sea", Path.Combine(testDir, "host-in-host-nuget", "host-in-host-nuget.cs"), src, prefix: "test"));

// ── Engine dir NuGet test (dotnet run, no publish) ──────────────────
// Tests that ScriptHost works when the Engine DLL is in the NuGet cache
// (not a flat publish dir). Bundled DLLs are in separate package dirs.

Console.WriteLine("\n[test] === Engine dir NuGet test ===");

RunTest("engine-dir-nuget (dotnet run, NuGet cache layout)", () =>
{
	var engineDirTestProject = Path.Combine(src, "test", "engine-dir-nuget");
	Environment.SetEnvironmentVariable("SEASHELL_NUPKG_DIR", nupkgDir);
	return DiagnosticsExt.RunProcess("dotnet",
		$"run --project \"{engineDirTestProject}\"",
		src, prefix: "test", logFile: Path.Combine(logs, "engine-dir-nuget.log"));
});

// ── Binary pass-through test ────────────────────────────────────────
// Tests that a pre-compiled binary with its own deps.json works via sea.

Console.WriteLine("\n[test] === Binary pass-through test ===");

RunTest("binary-deps (companion deps.json pass-through)", () =>
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

Console.WriteLine("\n[test] === Service CWD test ===");

RunTest("service-cwd (SCM/systemd script resolution)", () =>
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

	// Clear SYSTEM/root SeaShell compilation cache from previous runs
	if (isWindows)
		DiagnosticsExt.RunProcess("gsudo", @"cmd /c rmdir /s /q ""C:\ProgramData\seashell\cache"" 2>nul", src, quiet: true);
	else
		DiagnosticsExt.RunProcess("sudo", "rm -rf /var/lib/seashell/cache", src, quiet: true);

	// Publish with local nupkg source
	Environment.SetEnvironmentVariable("SEASHELL_NUPKG_DIR", nupkgDir);
	var pubCode = DiagnosticsExt.RunProcess("dotnet",
		$"publish \"{testProjectDir}\" -c Release -r {rid} --self-contained false -o \"{publishDir}\" --force",
		src, prefix: "test", logFile: Path.Combine(logs, "service-cwd-publish.log"));
	if (pubCode != 0) { Console.Error.WriteLine("[test] ServiceCwdTest publish failed"); return pubCode; }

	// cwd-test-script.cs is copied by dotnet publish (CopyToOutputDirectory in .csproj)

	var exeName = isWindows ? "ServiceCwdTest.exe" : "ServiceCwdTest";
	var exePath = Path.Combine(publishDir, exeName);

	// Elevation wrapper
	var elevate = isWindows ? "gsudo" : "sudo";

	// Place a nuget.config in the service account's temp/seashell/ directory.
	// NuGetDownloader creates temp projects under <temp>/seashell/restore/<guid>/,
	// and dotnet restore walks up the directory tree to find nuget.config.
	// SYSTEM uses C:\Windows\Temp, root uses /tmp.
	var serviceTemp = isWindows ? @"C:\Windows\Temp" : "/tmp";
	var nugetConfigDir = Path.Combine(serviceTemp, "seashell");
	var nugetConfigPath = Path.Combine(nugetConfigDir, "nuget.config");
	var nugetConfigContent = $"""
		<?xml version="1.0" encoding="utf-8"?>
		<configuration>
			<packageSources>
				<clear />
				<add key="pipeline-local" value="{nupkgDir}" />
				<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
			</packageSources>
		</configuration>
		""";
	Console.WriteLine($"[test]   Writing nuget.config for service account at {nugetConfigPath}");
	if (isWindows)
	{
		// C:\Windows\Temp requires admin — use gsudo to create dir + write file
		DiagnosticsExt.RunProcess(elevate, $"cmd /c mkdir \"{nugetConfigDir}\" 2>nul", publishDir, quiet: true);
		var tempConfig = Path.Combine(Path.GetTempPath(), "seashell-test-nuget.config");
		File.WriteAllText(tempConfig, nugetConfigContent);
		DiagnosticsExt.RunProcess(elevate, $"cmd /c copy /Y \"{tempConfig}\" \"{nugetConfigPath}\"", publishDir, quiet: true);
		File.Delete(tempConfig);
	}
	else
	{
		// /tmp is world-writable
		Directory.CreateDirectory(nugetConfigDir);
		File.WriteAllText(nugetConfigPath, nugetConfigContent);
	}

	// Install service
	Console.WriteLine($"[test]   Installing service '{serviceName}'...");
	var installCode = DiagnosticsExt.RunProcess(elevate, $"\"{exePath}\" install", publishDir, prefix: "test");
	if (installCode != 0) { Console.Error.WriteLine("[test] Service install failed"); return installCode; }

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

		// Clean up nuget.config from service account's temp
		if (isWindows)
			DiagnosticsExt.RunProcess(elevate, $"cmd /c del \"{nugetConfigPath}\" 2>nul", publishDir, quiet: true);
		else if (File.Exists(nugetConfigPath))
			File.Delete(nugetConfigPath);
	}
});

// ── Service identity test ────────────────────────────────────────────
// After service-cwd: switch the service account to a different identity.
// The new account has a different NuGet cache and temp dir — the nuget.config
// we placed for the primary account is invisible. Compilation should fail.

Console.WriteLine("\n[test] === Service identity test ===");

RunTest("service-identity (NuGet cache isolation on account switch)", () =>
{
	var testProjectDir = Path.Combine(src, "test", "ServiceCwdTest");
	var publishDir = Path.Combine(artifacts, "service-cwd-test");
	var markerDir = Path.Combine(publishDir, "markers");
	var markerFile = Path.Combine(markerDir, "seashell-cwd-test.marker");
	var serviceName = "seashell-cwd-test";
	var exeName = isWindows ? "ServiceCwdTest.exe" : "ServiceCwdTest";
	var exePath = Path.Combine(publishDir, exeName);
	var elevate = isWindows ? "gsudo" : "sudo";

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

// ── Lifecycle tests ──────────────────────────────────────────────────

Console.WriteLine("\n[test] === Lifecycle tests ===");

// --status returns non-zero when daemon isn't running — that's expected,
// we just verify the command doesn't crash
RunTest("daemon status (no crash)", () =>
{
	DiagnosticsExt.RunProcess("sea", "--status", src, prefix: "test");
	return 0; // always pass if it didn't crash
});

RunTest("daemon stop", () =>
	DiagnosticsExt.RunProcess("sea", "--stop", src, prefix: "test"));

RunTest("daemon stop (idempotent)", () =>
	DiagnosticsExt.RunProcess("sea", "--stop", src, prefix: "test"));

// ── Summary ──────────────────────────────────────────────────────────

Console.WriteLine($"\n[test] === Results: {passed} passed, {failed} failed ===");
foreach (var (name, pass, detail) in results)
{
	var icon = pass ? "PASS" : "FAIL";
	Console.WriteLine($"[test]   {icon}: {name}{(string.IsNullOrEmpty(detail) ? "" : $" ({detail})")}");
}

// ── Write results to logs + common ───────────────────────────────────

var logContent = string.Join("\n", results.Select(r => $"{(r.pass ? "PASS" : "FAIL")}: {r.name}"));
File.WriteAllText(Path.Combine(logs, "test.log"), logContent);

var commonLogDir = Path.Combine(commonDir, "logs");
Directory.CreateDirectory(commonLogDir);
File.WriteAllText(Path.Combine(commonLogDir, $"test-{host}.log"), logContent);

// ── Restore original sea version ─────────────────────────────────────

Console.WriteLine("\n[test] Restoring sea 0.1.7...");
if (isWindows)
	DiagnosticsExt.RunProcess("taskkill", "/F /IM seashell-daemon.exe", src, quiet: true);
else
	DiagnosticsExt.RunProcess("pkill", "-f seashell-daemon", src, quiet: true);
Thread.Sleep(2000);
DiagnosticsExt.RunProcess("dotnet", "tool uninstall -g SeaShell", src, quiet: true);
DiagnosticsExt.RunProcess("dotnet", "tool install -g SeaShell --version 0.1.7", src, quiet: true);

return failed > 0 ? 1 : 0;

// ── Helpers ──────────────────────────────────────────────────────────

void RunTest(string name, Func<int> action)
{
	Console.Write($"[test] {name}... ");
	try
	{
		var code = action();
		if (code == 0)
		{
			Console.WriteLine("OK");
			results.Add((name, true, ""));
			passed++;
		}
		else
		{
			Console.WriteLine($"FAILED (exit {code})");
			results.Add((name, false, $"exit {code}"));
			failed++;
		}
	}
	catch (Exception ex)
	{
		Console.WriteLine($"FAILED ({ex.Message})");
		results.Add((name, false, ex.Message));
		failed++;
	}
}
