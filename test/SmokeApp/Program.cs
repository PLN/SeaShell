using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Host;

// ── Host-in-Host smoke test ──────────────────────────────────────────
// SmokeApp is run by ServiceHost (SmokeService) as a platform service.
// SmokeApp itself uses ScriptHost to compile and run SmokeScript.cs —
// a SeaShell script that exercises the Engine+Script pipeline from inside
// a Host consumer. This is the exact scenario that triggered CS1704
// (duplicate SeaShell.Script assembly) before the fix.

var logDir = Environment.GetEnvironmentVariable("SMOKE_LOG_DIR")
	?? Path.Combine(Path.GetTempPath(), "seashell-smoke");
Directory.CreateDirectory(logDir);

var appLog = Path.Combine(logDir, "smokeapp.log");
var scriptLog = Path.Combine(logDir, "smokescript.log");

Console.WriteLine($"[SmokeApp] Started (PID {Environment.ProcessId})");
Console.WriteLine($"[SmokeApp]   ScriptPath:  {Sea.ScriptPath}");
Console.WriteLine($"[SmokeApp]   IsReload:    {Sea.IsReload}");
Console.WriteLine($"[SmokeApp]   ReloadCount: {Sea.ReloadCount}");
Console.WriteLine($"[SmokeApp]   LogDir:      {logDir}");

var state = Sea.GetReloadStateString();
if (state != null)
	Console.WriteLine($"[SmokeApp]   State:       {state}");

var iteration = 0;

Sea.Reloading += () =>
{
	Console.WriteLine($"[SmokeApp] Reloading event! Saving state...");
	Sea.SetReloadState($"iteration={iteration}");
};

Sea.Stopping += () =>
{
	Console.WriteLine($"[SmokeApp] Stopping event!");
};

// ── Run SmokeScript via ScriptHost ───────────────────────────────────
// The script writes its PID to scriptLog. If CS1704 is present, this
// compilation will fail with "duplicate assembly 'SeaShell.Script'".
var scriptCode = $$"""
	var logFile = @"{{scriptLog.Replace("\"", "\"\"")}}";
	File.AppendAllText(logFile, $"{Environment.ProcessId}\n");
	Console.WriteLine($"[SmokeScript] PID {Environment.ProcessId} wrote to {logFile}");
	""";

using var host = new ScriptHost();
var scriptResult = await host.RunSnippetAsync(scriptCode);
if (!scriptResult.Success)
{
	Console.Error.WriteLine($"[SmokeApp] Script compilation FAILED:\n{scriptResult.Error}");
	return 1;
}
Console.WriteLine($"[SmokeApp] Script ran OK: {scriptResult.Output?.Trim()}");

// ── Heartbeat loop ───────────────────────────────────────────────────
while (!Sea.ShutdownToken.IsCancellationRequested)
{
	iteration++;
	File.AppendAllText(appLog, $"{Environment.ProcessId}\n");
	Console.WriteLine($"[SmokeApp] Heartbeat #{iteration} (PID {Environment.ProcessId})");

	// Run the script again each iteration
	var result = await host.RunSnippetAsync(scriptCode);
	if (!result.Success)
		Console.Error.WriteLine($"[SmokeApp] Script error: {result.Error}");

	// Request reload every iteration (every 5s) — keeps CI fast
	Console.WriteLine($"[SmokeApp] Requesting reload...");
	try { Sea.RequestReload(reason: $"auto-reload at iteration {iteration}"); }
	catch (Exception ex) { Console.WriteLine($"[SmokeApp] RequestReload failed: {ex.Message}"); }

	try { await Task.Delay(5000, Sea.ShutdownToken); }
	catch (OperationCanceledException) { break; }
}

Console.WriteLine($"[SmokeApp] Exiting.");
return 0;
