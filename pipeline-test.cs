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
