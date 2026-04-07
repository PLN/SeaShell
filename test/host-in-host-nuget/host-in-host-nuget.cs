// ── Host-in-Host NuGet Regression Test ───────────────────────────────
//
// Like host-in-host, but the INNER snippet also uses NuGet. This tests
// whether the inner ScriptCompiler's _engineDir can resolve both bundled
// DLLs and NuGet packages when the outer script loaded the Engine from
// the NuGet cache.
//
//   sea (Engine) compiles this script
//     → this script has //sea_nuget seashell.host (Engine from NuGet)
//     → this script uses ScriptHost to compile a snippet
//       → the snippet has //sea_nuget Humanizer.Core
//       → the snippet calls Sea.ShutdownToken + "test".Humanize()
//
// If the inner Engine's _engineDir diverges from the outer, bundled DLLs
// or NuGet packages may fail to resolve in the inner subprocess.
// Exit 0 = pass, non-zero = fail.

//sea_nuget seashell.host

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Host;

var testName = "host-in-host-nuget";
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

Console.WriteLine($"[{testName}] Pre-test checks...");

var daemons = Process.GetProcessesByName("seashell-daemon");
if (daemons.Length > 0)
	Console.WriteLine($"[{testName}]   WARNING: {daemons.Length} seashell-daemon process(es) already running (PIDs: {string.Join(", ", daemons.Select(p => p.Id))})");

Console.WriteLine($"[{testName}] Running test...");

var host = new ScriptHost();

// Inner snippet uses a NuGet package AND Sea context
var snippet = """
	//sea_nuget Humanizer.Core
	using System;
	using SeaShell;
	using Humanizer;

	var ok = true;

	// 1. Sea context (bundled DLLs must resolve in inner subprocess)
	try
	{
		var canCancel = Sea.ShutdownToken.CanBeCanceled;
		Console.WriteLine("  CHECK-SEA: OK");
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"  CHECK-SEA: FAIL ({ex.GetType().Name}: {ex.Message})");
		ok = false;
	}

	// 2. NuGet package (inner Engine must resolve from NuGet cache)
	try
	{
		var result = "hello_world".Humanize();
		Console.WriteLine($"  CHECK-NUGET: OK ({result})");
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"  CHECK-NUGET: FAIL ({ex.GetType().Name}: {ex.Message})");
		ok = false;
	}

	return ok ? 0 : 1;
	""";

Console.WriteLine($"[{testName}]   Compiling snippet with NuGet via nested ScriptHost...");

var passed = true;
try
{
	var result = await host.RunSnippetAsync(snippet);

	Console.WriteLine($"[{testName}]   Snippet exit code: {result.ExitCode}");
	if (!string.IsNullOrWhiteSpace(result.StandardOutput))
		Console.WriteLine($"[{testName}]   stdout:\n{result.StandardOutput.TrimEnd()}");
	if (!string.IsNullOrWhiteSpace(result.StandardError))
		Console.Error.WriteLine($"[{testName}]   stderr:\n{result.StandardError.TrimEnd()}");

	if (!result.Success)
	{
		Console.Error.WriteLine($"[{testName}]   Snippet failed (exit {result.ExitCode})");
		passed = false;
	}
	else
	{
		foreach (var check in new[] { "CHECK-SEA: OK", "CHECK-NUGET: OK" })
		{
			if (!result.StandardOutput.Contains(check))
			{
				Console.Error.WriteLine($"[{testName}]   Missing: {check}");
				passed = false;
			}
		}
	}
}
catch (Exception ex)
{
	Console.Error.WriteLine($"[{testName}]   ScriptHost exception: {ex.Message}");
	passed = false;
}

Console.WriteLine($"[{testName}] Post-test cleanup...");
daemons = Process.GetProcessesByName("seashell-daemon");
if (daemons.Length > 0)
{
	Console.WriteLine($"[{testName}]   Killing {daemons.Length} seashell-daemon process(es)");
	foreach (var d in daemons)
		try { d.Kill(); } catch { }
}

if (passed)
{
	Console.WriteLine($"[{testName}] PASS: Nested ScriptHost with NuGet resolved all deps");
	return 0;
}
else
{
	Console.Error.WriteLine($"[{testName}] FAIL");
	return 1;
}
