using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SeaShell;

Console.WriteLine($"[SmokeApp] Started (PID {Environment.ProcessId})");
Console.WriteLine($"[SmokeApp]   ScriptPath:  {Sea.ScriptPath}");
Console.WriteLine($"[SmokeApp]   IsReload:    {Sea.IsReload}");
Console.WriteLine($"[SmokeApp]   ReloadCount: {Sea.ReloadCount}");
Console.WriteLine($"[SmokeApp]   IsWatchMode: {Sea.IsWatchMode}");

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

Sea.MessageReceived += (payload, topic) =>
{
	var text = Encoding.UTF8.GetString(payload);
	Console.WriteLine($"[SmokeApp] Message: topic={topic} payload={text}");
};

// Loop: print heartbeat, request reload every 30s
while (!Sea.ShutdownToken.IsCancellationRequested)
{
	iteration++;
	Console.WriteLine($"[SmokeApp] Heartbeat #{iteration} (reload #{Sea.ReloadCount})");

	// Request reload every iteration (every 5s) — keeps CI fast
	Console.WriteLine($"[SmokeApp] Requesting reload...");
	try { Sea.RequestReload(reason: $"auto-reload at iteration {iteration}"); }
	catch (Exception ex) { Console.WriteLine($"[SmokeApp] RequestReload failed: {ex.Message}"); }

	try { await Task.Delay(5000, Sea.ShutdownToken); }
	catch (OperationCanceledException) { break; }
}

Console.WriteLine($"[SmokeApp] Exiting.");
