//sea_mutex_attach
// Test: running instance receives attach from a blocked caller.
// The pipeline launches this script, then launches a second instance.
// The second instance sends its args via attach; this instance receives them.

using System.Threading;

var received = new ManualResetEventSlim(false);
string[]? attachedArgs = null;
string? attachedCwd = null;

Sea.Attached += (ctx) =>
{
	attachedArgs = ctx.Args;
	attachedCwd = ctx.WorkingDirectory;
	Console.WriteLine($"  mutex_attach_test: received attach — args=[{string.Join(", ", ctx.Args)}], cwd={ctx.WorkingDirectory}");
	ctx.SendString("OK");
	ctx.Close();
	received.Set();
};

Console.WriteLine("  mutex_attach_test: waiting for attach (up to 15s)...");

// Wait for an attach client, or timeout
if (!received.Wait(15_000))
{
	Console.Error.WriteLine("  FAIL: no attach received within 15s");
	Sea.Restart = false;
	return 1;
}

if (attachedArgs == null || attachedArgs.Length == 0)
{
	Console.Error.WriteLine("  FAIL: attached args were null or empty");
	Sea.Restart = false;
	return 1;
}

Console.WriteLine("  mutex_attach_test: PASS");
Sea.Restart = false;
return 0;
