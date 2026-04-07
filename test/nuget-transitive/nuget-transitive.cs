// ── CS1704 Regression Test: NuGet transitive dependency on SeaShell.Script ──
//
// This script references SeaShell.Host via //sea_nuget. Host bundles
// SeaShell.Script.dll. The Engine also auto-injects SeaShell.Script.dll
// into every compiled script. Without dedup in CollectReferences(), Roslyn
// sees two copies → CS1704: duplicate assembly 'SeaShell.Script'.
//
// If this script compiles and runs, the dedup works.
// Exit 0 = pass, non-zero = fail.

//sea_nuget seashell.host

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using SeaShell;

var testName = "nuget-transitive";
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

// ── Pre-test: check for clean state ──────────────────────────────────

Console.WriteLine($"[{testName}] Pre-test checks...");

var daemons = Process.GetProcessesByName("seashell-daemon");
if (daemons.Length > 0)
	Console.WriteLine($"[{testName}]   WARNING: {daemons.Length} seashell-daemon process(es) already running (PIDs: {string.Join(", ", daemons.Select(p => p.Id))})");

// ── Test ─────────────────────────────────────────────────────────────

Console.WriteLine($"[{testName}] Running test...");

var passed = true;

// Verify Sea APIs are accessible (proves SeaShell.Script is loaded, no CS1704)
try
{
	var canBeCanceled = Sea.ShutdownToken.CanBeCanceled;
	Console.WriteLine($"[{testName}]   Sea.ShutdownToken.CanBeCanceled = {canBeCanceled}");
	Console.WriteLine($"[{testName}]   Sea.ScriptPath = {Sea.ScriptPath}");
}
catch (Exception ex)
{
	Console.Error.WriteLine($"[{testName}]   Sea API access failed: {ex.Message}");
	passed = false;
}

// Verify Host types are accessible (proves SeaShell.Host is loaded)
try
{
	var hostType = typeof(SeaShell.Host.ScriptHost);
	Console.WriteLine($"[{testName}]   ScriptHost type: {hostType.FullName}");
}
catch (Exception ex)
{
	Console.Error.WriteLine($"[{testName}]   Host type access failed: {ex.Message}");
	passed = false;
}

// ── Post-test: cleanup ───────────────────────────────────────────────

Console.WriteLine($"[{testName}] Post-test cleanup...");

daemons = Process.GetProcessesByName("seashell-daemon");
if (daemons.Length > 0)
{
	Console.WriteLine($"[{testName}]   Killing {daemons.Length} seashell-daemon process(es)");
	foreach (var d in daemons)
		try { d.Kill(); } catch { }
}

// ── Result ───────────────────────────────────────────────────────────

if (passed)
{
	Console.WriteLine($"[{testName}] PASS: no CS1704, both Sea and Host types accessible");
	return 0;
}
else
{
	Console.Error.WriteLine($"[{testName}] FAIL");
	return 1;
}
