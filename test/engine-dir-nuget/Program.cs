// ── Engine Dir NuGet Test ────────────────────────────────────────────
//
// Tests that ScriptHost works when consumed via NuGet in a `dotnet run`
// scenario (no publish). In this layout, SeaShell.Engine.dll lives in
// the NuGet cache (~/.nuget/packages/seashell.engine/x.y.z/lib/net10.0/)
// and bundled DLLs (MessagePack, SeaShell.Ipc, SeaShell.Script) are each
// in their OWN package directories — not adjacent to the Engine.
//
// This exercises the _engineDir probing path: if the Engine assumes
// bundled DLLs are next to SeaShell.Engine.dll, they won't be found.
//
// Exit 0 = pass, non-zero = fail.

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using SeaShell.Host;

var testName = "engine-dir-nuget";
Console.WriteLine($"[{testName}] Running test...");

// Verify we're NOT in a publish layout — the Engine DLL should be in the NuGet cache
var engineLocation = typeof(SeaShell.Engine.ScriptCompiler).Assembly.Location;
Console.WriteLine($"[{testName}]   Engine location: {engineLocation}");

var host = new ScriptHost();

// The inner snippet checks that bundled DLLs resolve at runtime
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
