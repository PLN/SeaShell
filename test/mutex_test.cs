//sea_mutex system
// Test: verify Sea.MutexScope is set correctly.
// The actual mutex blocking is tested by the pipeline (launches two instances).

Console.WriteLine($"  mutex_test: MutexScope={Sea.MutexScope}");

if (Sea.MutexScope != 3) // 3 = System
{
	Console.Error.WriteLine($"  FAIL: Sea.MutexScope should be 3 (System), got {Sea.MutexScope}");
	return 1;
}

// Hold for a moment so the pipeline can test a second blocked instance
Console.WriteLine("  mutex_test: holding mutex for 5s...");
Thread.Sleep(5000);
Console.WriteLine("  mutex_test: done");
return 0;
