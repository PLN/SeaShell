//sea_watch

Sea.Reloading += () => Console.WriteLine("[script] Reloading event fired, cleaning up...");

Console.WriteLine($"[script] Started! IsReload={Sea.IsReload} ReloadCount={Sea.ReloadCount}");
Console.WriteLine("[script] Version: 1");

while (!Sea.IsShuttingDown)
{
	Console.WriteLine($"[script] tick (v1, reload #{Sea.ReloadCount})");
	try { await Task.Delay(2000, Sea.ShutdownToken); }
	catch (OperationCanceledException) { break; }
}

Console.WriteLine("[script] Exiting gracefully");
return 0;
