//sea_mutex user
// Test: verify user-scope mutex. Sea.MutexScope should be 2 (User).
// Second instance (same user) should be blocked with exit 200.

Console.WriteLine($"  mutex_user_test: MutexScope={Sea.MutexScope}");

if (Sea.MutexScope != 2) // 2 = User
{
	Console.Error.WriteLine($"  FAIL: Sea.MutexScope should be 2 (User), got {Sea.MutexScope}");
	return 1;
}

Console.WriteLine("  mutex_user_test: holding mutex for 5s...");
Thread.Sleep(5000);
Console.WriteLine("  mutex_user_test: done");
return 0;
