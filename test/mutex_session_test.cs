//sea_mutex session
// Test: verify session-scope mutex. Sea.MutexScope should be 1 (Session).
// Second instance in the SAME session should be blocked (exit 200).
// An instance in a DIFFERENT session should NOT be blocked.
//
// The pipeline tests this by:
// 1. Starting this script in the SSH session (holds session mutex)
// 2. Launching a second instance in the same SSH session → exit 200
// 3. Launching a third instance via Task Scheduler (different session) →
//    writes a marker file and exits 0 (not blocked)

Console.WriteLine($"  mutex_session_test: MutexScope={Sea.MutexScope}");

if (Sea.MutexScope != 1) // 1 = Session
{
	Console.Error.WriteLine($"  FAIL: Sea.MutexScope should be 1 (Session), got {Sea.MutexScope}");
	return 1;
}

// If args[0] is a marker path, write our pid and exit immediately.
// This is used by the Task Scheduler instance to prove it ran (different session).
if (args.Length > 0 && args[0].EndsWith(".txt"))
{
	File.WriteAllText(args[0], $"{Environment.ProcessId}");
	Console.WriteLine($"  mutex_session_test: wrote marker {args[0]}");
	return 0;
}

Console.WriteLine("  mutex_session_test: holding mutex for 30s...");
Thread.Sleep(30000);
Console.WriteLine("  mutex_session_test: done");
return 0;
