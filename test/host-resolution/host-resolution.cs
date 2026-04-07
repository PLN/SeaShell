// ── Host Resolution Test ─────────────────────────────────────────────
//
// Verifies that bundled DLLs (MessagePack, SeaShell.Ipc, SeaShell.Script)
// resolve correctly when a ScriptHost consumer compiles and runs a script.
//
// This catches the CSA bug: deps.json listed bundled assemblies as
// type "project" entries, causing the .NET host to try NuGet cache
// resolution for DLLs that only exist bundled inside the host package.
//
// Two verification points:
//   1. Sea.ShutdownToken works         → SeaShell.Script.dll resolved
//   2. MessagePackSerializer type loads → MessagePack.dll resolved
//
// Exit 0 = pass, non-zero = fail.

//sea_nuget seashell.host

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Host;

var testName = "host-resolution";
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

Console.WriteLine($"[{testName}] Running test...");

var host = new ScriptHost();

// The inner snippet verifies all three resolution paths
var snippet = """
	using System;
	using SeaShell;

	var ok = true;

	// 1. SeaShell.Script.dll — bundled, must resolve from app dir or engine dir
	try
	{
		var canCancel = Sea.ShutdownToken.CanBeCanceled;
		Console.WriteLine("  CHECK-SCRIPT: OK");
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"  CHECK-SCRIPT: FAIL ({ex.GetType().Name}: {ex.Message})");
		ok = false;
	}

	// 2. MessagePack.dll — bundled (transitive via Ipc), must resolve
	try
	{
		var type = Type.GetType("MessagePack.MessagePackSerializer, MessagePack");
		if (type != null)
			Console.WriteLine("  CHECK-MSGPACK: OK");
		else
		{
			Console.Error.WriteLine("  CHECK-MSGPACK: FAIL (type not found)");
			ok = false;
		}
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"  CHECK-MSGPACK: FAIL ({ex.GetType().Name}: {ex.Message})");
		ok = false;
	}

	return ok ? 0 : 1;
	""";

var passed = true;
try
{
	Console.WriteLine($"[{testName}]   Compiling snippet via ScriptHost...");
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
		// Verify all three checks passed
		var output = result.StandardOutput;
		foreach (var check in new[] { "CHECK-SCRIPT: OK", "CHECK-MSGPACK: OK" })
		{
			if (!output.Contains(check))
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

// Cleanup
var daemons = Process.GetProcessesByName("seashell-daemon");
if (daemons.Length > 0)
{
	foreach (var d in daemons)
		try { d.Kill(); } catch { }
}

if (passed)
{
	Console.WriteLine($"[{testName}] PASS: All bundled DLL and NuGet resolution checks passed");
	return 0;
}
else
{
	Console.Error.WriteLine($"[{testName}] FAIL");
	return 1;
}
