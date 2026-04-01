//sea_restart
// Test: script immediately opts out of restart — should exit cleanly without restarting.

Console.WriteLine($"  restart_opt_out_test: RestartCount={Sea.RestartCount}");

if (Sea.RestartCount > 0)
{
	Console.Error.WriteLine("  FAIL: script restarted despite Sea.Restart = false");
	return 1;
}

Sea.Restart = false;
return 0;
