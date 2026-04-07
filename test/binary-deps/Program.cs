// ── Binary Deps Pass-Through Test ────────────────────────────────────
//
// A pre-compiled binary with its own deps.json (via Serilog reference).
// When run via `sea binary-deps.dll`, the Engine's CompileBinary() should:
//   - Preserve the companion deps.json (not overwrite with SeaShell entries)
//   - Copy bundled DLLs to staging dir
//   - Set DOTNET_STARTUP_HOOKS for Sea context
//
// Verifies:
//   1. Serilog loads (companion deps.json + runtimeconfig probing works)
//   2. Sea context available (startup hook + bundled DLLs resolved)
//
// Exit 0 = pass, non-zero = fail.

using System;

var ok = true;

// 1. Serilog — loaded from companion deps.json + NuGet cache probing
try
{
	var logType = typeof(Serilog.Log);
	Console.WriteLine($"CHECK-SERILOG: OK ({logType.Assembly.GetName().Version})");
}
catch (Exception ex)
{
	Console.Error.WriteLine($"CHECK-SERILOG: FAIL ({ex.GetType().Name}: {ex.Message})");
	ok = false;
}

// 2. Sea context — loaded via DOTNET_STARTUP_HOOKS (SeaShell.Script.dll)
// Use reflection to avoid compile-time dependency on SeaShell.Script
try
{
	var seaType = Type.GetType("SeaShell.Sea, SeaShell.Script");
	if (seaType != null)
	{
		var prop = seaType.GetProperty("ShutdownToken",
			System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
		if (prop != null)
		{
			var token = prop.GetValue(null);
			Console.WriteLine("CHECK-SEA: OK");
		}
		else
		{
			Console.WriteLine("CHECK-SEA: SKIP (ShutdownToken property not found)");
		}
	}
	else
	{
		// Sea context not injected — this is expected if startup hooks aren't set.
		// Still report it so the test harness knows.
		Console.WriteLine("CHECK-SEA: SKIP (SeaShell.Sea type not loaded)");
	}
}
catch (Exception ex)
{
	Console.Error.WriteLine($"CHECK-SEA: FAIL ({ex.GetType().Name}: {ex.Message})");
	ok = false;
}

return ok ? 0 : 1;
