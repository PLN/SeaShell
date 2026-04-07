// ── Engine Dir NuGet Test ────────────────────────────────────────────
//
// Tests that ScriptHost works when consumed via NuGet in a `dotnet run`
// scenario (no publish). In this layout, bundled DLLs (Invoker, Ipc,
// Protocol, Script, MessagePack) are in the NuGet cache — not adjacent.
//
// v0.3: Host no longer references Engine. This test verifies that the
// Invoker-based Host works correctly from a NuGet cache layout.
//
// Exit 0 = pass, non-zero = fail.

using System;
using System.Diagnostics;
using SeaShell.Host;

var testName = "engine-dir-nuget";
Console.WriteLine($"[{testName}] Running test...");

// Verify Host is loaded from NuGet cache (not a flat publish dir)
var hostLocation = typeof(ScriptHost).Assembly.Location;
Console.WriteLine($"[{testName}]   Host location: {hostLocation}");

var host = new ScriptHost();

var snippet = """
	using System;
	using SeaShell;

	var ok = true;

	// 1. SeaShell.Script.dll (bundled)
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

	// 2. MessagePack.dll (bundled, transitive via Ipc)
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
		foreach (var check in new[] { "CHECK-SCRIPT: OK", "CHECK-MSGPACK: OK" })
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

// Cleanup
var daemons = Process.GetProcessesByName("seashell-daemon");
foreach (var d in daemons)
	try { d.Kill(); } catch { }

if (passed)
{
	Console.WriteLine($"[{testName}] PASS: Bundled DLLs resolved from NuGet cache layout");
	return 0;
}
else
{
	Console.Error.WriteLine($"[{testName}] FAIL");
	return 1;
}
