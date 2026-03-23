// ── Host-in-Host Regression Test ─────────────────────────────────────
//
// A sea script that references SeaShell.Host and uses ScriptHost to
// compile and run ANOTHER script snippet. This is the "plans within
// plans" scenario:
//
//   sea (Engine) compiles this script
//     → this script uses ScriptHost (Engine) to compile a snippet
//       → the snippet uses Sea.* APIs (SeaShell.Script)
//
// If CS1704 is present at any level, compilation fails.
// If the Engine can't find SeaShell.Script.dll, the snippet fails.
// Exit 0 = pass, non-zero = fail.

//sea_nuget seashell.host

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Host;

var testName = "host-in-host";
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

// ── Pre-test: check for clean state ──────────────────────────────────

Console.WriteLine($"[{testName}] Pre-test checks...");

var daemons = Process.GetProcessesByName("seashell-daemon");
if (daemons.Length > 0)
	Console.WriteLine($"[{testName}]   WARNING: {daemons.Length} seashell-daemon process(es) already running (PIDs: {string.Join(", ", daemons.Select(p => p.Id))})");

// ── Test ─────────────────────────────────────────────────────────────

Console.WriteLine($"[{testName}] Running test...");

var host = new ScriptHost();

var snippet = @"
using System;
using SeaShell;
Console.WriteLine($""  Inner Sea.ShutdownToken: {Sea.ShutdownToken.CanBeCanceled}"");
Console.WriteLine($""  Inner PID: {Environment.ProcessId}"");
Console.WriteLine(""  Inner: OK"");
";

Console.WriteLine($"[{testName}]   Compiling snippet via ScriptHost...");

var passed = true;
try
{
	var result = await host.RunSnippetAsync(snippet);

	Console.WriteLine($"[{testName}]   Snippet exit code: {result.ExitCode}");
	if (!string.IsNullOrWhiteSpace(result.StandardOutput))
		Console.WriteLine($"[{testName}]   Snippet stdout: {result.StandardOutput.Trim()}");
	if (!string.IsNullOrWhiteSpace(result.StandardError))
		Console.Error.WriteLine($"[{testName}]   Snippet stderr: {result.StandardError.Trim()}");

	if (!result.Success)
	{
		Console.Error.WriteLine($"[{testName}]   Snippet failed (exit {result.ExitCode})");
		passed = false;
	}
	else if (!result.StandardOutput.Contains("Inner: OK"))
	{
		Console.Error.WriteLine($"[{testName}]   Snippet did not produce expected output");
		passed = false;
	}
}
catch (Exception ex)
{
	Console.Error.WriteLine($"[{testName}]   ScriptHost exception: {ex.Message}");
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
	Console.WriteLine($"[{testName}] PASS: Engine compiled snippet inside Host consumer");
	return 0;
}
else
{
	Console.Error.WriteLine($"[{testName}] FAIL");
	return 1;
}
