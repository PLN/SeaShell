//sea_watch

// Restore counter from previous instance, or start at 0
var counter = 0;
var prevState = Sea.GetReloadStateString();
if (prevState != null && int.TryParse(prevState, out var prev))
{
	counter = prev;
	Console.WriteLine($"[v1] Resumed with counter={counter} from previous instance");
}
else
{
	Console.WriteLine($"[v1] Fresh start, counter={counter}");
}

Sea.Reloading += () =>
{
	Console.WriteLine($"[v1] Passing counter={counter} to next instance");
	Sea.SetReloadState(counter.ToString());
};

while (!Sea.IsShuttingDown)
{
	counter++;
	Console.WriteLine($"[v1] counter={counter}");
	try { await Task.Delay(1500, Sea.ShutdownToken); }
	catch (OperationCanceledException) { break; }
}

return 0;
