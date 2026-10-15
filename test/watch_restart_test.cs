//sea_watch
//sea_restart
// Test: //sea_restart in watch mode — the script restarts twice, then opts out.
// A session that ends instead of restarting exits without the "reached" line,
// which the CI step checks for (the broken path also reported exit code 0).

Console.WriteLine($"  watch_restart_test: RestartCount={Sea.RestartCount}");

if (Sea.RestartCount < 2)
	return 3;

Console.WriteLine($"  watch_restart_test: reached count={Sea.RestartCount}, opting out");
Sea.Restart = false;
return 0;
