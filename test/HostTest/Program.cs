using SeaShell.Host;

// Run update test if --update flag is passed
if (args.Length > 0 && args[0] == "--update")
{
	await UpdateTest.Run();
	return;
}

var host = new ScriptHost();
var testDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

// Test 1: Run a code snippet
Console.WriteLine("=== Snippet ===");
var r1 = await host.RunSnippetAsync("""
	Console.WriteLine("Hello from a hosted snippet!");
	Console.WriteLine($"CWD: {Environment.CurrentDirectory}");
	return 0;
	""");
Console.WriteLine($"  stdout: {r1.StandardOutput.Trim()}");
Console.WriteLine($"  exit:   {r1.ExitCode}");

// Test 2: Run a script file
Console.WriteLine("\n=== Script file ===");
var r2 = await host.RunAsync(Path.Combine(testDir, "hello.cs"));
Console.WriteLine($"  stdout: {r2.StandardOutput.Trim()}");
Console.WriteLine($"  exit:   {r2.ExitCode}");

// Test 3: Run a script with NuGet (Serilog)
Console.WriteLine("\n=== NuGet script ===");
var r3 = await host.RunSnippetAsync("""
	//sea_nuget Humanizer.Core
	using Humanizer;
	Console.WriteLine("some_variable_name".Humanize());
	return 0;
	""");
Console.WriteLine($"  stdout: {r3.StandardOutput.Trim()}");
Console.WriteLine($"  exit:   {r3.ExitCode}");

// Test 4: Working directory override
Console.WriteLine("\n=== Working directory ===");
var cwdScript = Path.Combine(testDir, "cwd_test.cs");
var r4cwd = await host.RunAsync(cwdScript, workingDirectory: testDir);
Console.WriteLine($"  stdout: {r4cwd.StandardOutput.Trim()}");
Console.WriteLine($"  exit:   {r4cwd.ExitCode}");
if (!r4cwd.StandardOutput.Contains(testDir.Replace('\\', '/')))
	if (!r4cwd.StandardOutput.Contains(testDir))
		Console.Error.WriteLine($"  FAIL: expected CWD to contain {testDir}");

// Test 5: Environment variable injection
Console.WriteLine("\n=== Environment vars ===");
var envScript = Path.Combine(testDir, "env_test.cs");
var envVars = new Dictionary<string, string> { ["SEASHELL_TEST_VAR"] = "hello_from_host" };
var r5env = await host.RunAsync(envScript, environmentVars: envVars);
Console.WriteLine($"  stdout: {r5env.StandardOutput.Trim()}");
Console.WriteLine($"  exit:   {r5env.ExitCode}");
if (!r5env.StandardOutput.Contains("hello_from_host"))
	Console.Error.WriteLine("  FAIL: expected env var SEASHELL_TEST_VAR=hello_from_host");

// Test 6: Compile once, inspect, run twice
Console.WriteLine("\n=== Compile + run twice ===");
var compiled = host.Compile(Path.Combine(testDir, "hello.cs"));
Console.WriteLine($"  success:  {compiled.Success}");
Console.WriteLine($"  assembly: {Path.GetFileName(compiled.AssemblyPath)}");

var r6a = await host.ExecuteAsync(compiled);
var r6b = await host.ExecuteAsync(compiled);
Console.WriteLine($"  run 1: {r6a.StandardOutput.Trim()}");
Console.WriteLine($"  run 2: {r6b.StandardOutput.Trim()}");
