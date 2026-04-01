//sea_restart
//sea_mutex user
//sea_window
// Test: verify multiple directives parse correctly.
// This script is used by the engine tests, not run directly.
// (Would need //sea_mutex release to actually run — just for parse verification.)

Console.WriteLine("  directive_test: checking parsed directives");
Console.WriteLine($"  IsRestartMode={Sea.IsRestartMode}");
Console.WriteLine($"  MutexScope={Sea.MutexScope}");

Sea.Restart = false;
return 0;
