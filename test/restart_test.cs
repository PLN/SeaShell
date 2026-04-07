//sea_restart
// Test: script restarts on exit, then opts out via Sea.Restart = false.
// Uses Sea.RestartCount to track restarts across process instances.

Console.WriteLine($"  restart_test: RestartCount={Sea.RestartCount}");

if (Sea.RestartCount < 2)
{
	Console.WriteLine($"  restart_test: exiting to trigger restart (count={Sea.RestartCount})");
	return 0; // Sea.Restart defaults to true — Invoker will restart
}

// Reached target — opt out and exit cleanly
Console.WriteLine($"  restart_test: reached count={Sea.RestartCount}, opting out");
Sea.Restart = false;
return 0;
