// Symlink Feasibility Test for SeaShell binary deployment.
// Run via the REAL sea.exe, not a symlinked one:  sea test/symlink_test.cs
//
// Creates a temp fixture with versioned directories + symlinks,
// launches processes through the symlinks, and reports PASS/FAIL.

using System.Diagnostics;
using System.Runtime.InteropServices;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const int Timeout = 15_000;
var testDir = Path.Combine(Path.GetTempPath(), $"seashell-symlink-test-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
var passed  = 0;
var failed  = 0;
var skipped = 0;

// Declared up front so goto Fallbacks can reach them
string? v1Dir    = null;
string? v2Dir    = null;
string? binDir   = null;
string? seaLink  = null;
string? seawLink = null;

void Log(string msg) => Console.WriteLine(msg);

void Pass(string name, string detail = "")
{
	passed++;
	Console.ForegroundColor = ConsoleColor.Green;
	Console.Write($"  PASS");
	Console.ResetColor();
	Console.WriteLine($"  {name}{(detail.Length > 0 ? $" — {detail}" : "")}");
}

void Fail(string name, string detail = "")
{
	failed++;
	Console.ForegroundColor = ConsoleColor.Red;
	Console.Write($"  FAIL");
	Console.ResetColor();
	Console.WriteLine($"  {name}{(detail.Length > 0 ? $" — {detail}" : "")}");
}

void Skip(string name, string detail = "")
{
	skipped++;
	Console.ForegroundColor = ConsoleColor.Yellow;
	Console.Write($"  SKIP");
	Console.ResetColor();
	Console.WriteLine($"  {name}{(detail.Length > 0 ? $" — {detail}" : "")}");
}

(int exitCode, string stdout, string stderr) Run(string exe, string args = "", int timeout = Timeout, Dictionary<string, string>? env = null)
{
	var psi = new ProcessStartInfo(exe, args)
	{
		RedirectStandardOutput = true,
		RedirectStandardError  = true,
		UseShellExecute        = false,
		CreateNoWindow         = true,
	};
	if (env != null)
		foreach (var kv in env)
			psi.Environment[kv.Key] = kv.Value;

	using var proc = Process.Start(psi)!;
	var stdout_ = proc.StandardOutput.ReadToEnd();
	var stderr_ = proc.StandardError.ReadToEnd();
	proc.WaitForExit(timeout);
	if (!proc.HasExited)
	{
		proc.Kill(true);
		return (-1, stdout_, stderr_ + "\n[TIMEOUT]");
	}
	return (proc.ExitCode, stdout_, stderr_);
}

Process StartBackground(string exe, string args = "")
{
	var psi = new ProcessStartInfo(exe, args)
	{
		RedirectStandardOutput = true,
		RedirectStandardError  = true,
		UseShellExecute        = false,
		CreateNoWindow         = true,
	};
	return Process.Start(psi)!;
}

// ---------------------------------------------------------------------------
// Fixture setup
// ---------------------------------------------------------------------------

Log("=== SeaShell Symlink Feasibility Test ===");
Log("");

// Locate the real installation.
// We can't use Environment.ProcessPath — scripts run inside the daemon.
// Try LOCALAPPDATA, then ProgramData, then fall back to PATH lookup.
string? realBin = null;
foreach (var candidate in new[]
{
	Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "seashell", "bin"),
	Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "seashell", "bin"),
})
{
	if (File.Exists(Path.Combine(candidate, "sea.exe")))
	{
		realBin = candidate;
		break;
	}
}
if (realBin == null)
	throw new Exception("Cannot find SeaShell installation (checked LOCALAPPDATA and ProgramData)");
var realSea = Path.Combine(realBin, "sea.exe");

Log($"  Real install: {realBin}");
Log($"  Test dir:     {testDir}");
Log("");

// ---------------------------------------------------------------------------
// Test 0: Privilege Gate
// ---------------------------------------------------------------------------

Log("--- Test 0: Symlink Privilege ---");

var gateFile   = Path.Combine(Path.GetTempPath(), $"symlink-gate-{Guid.NewGuid():N}.txt");
var gateLinkFile = gateFile + ".link";
bool canSymlink = false;

try
{
	File.WriteAllText(gateFile, "gate");
	File.CreateSymbolicLink(gateLinkFile, gateFile);
	var target = new FileInfo(gateLinkFile).LinkTarget;
	if (target != null && File.Exists(gateLinkFile))
	{
		canSymlink = true;
		Pass("Symlink creation", $"target resolved to: {target}");
	}
	else
	{
		Fail("Symlink creation", "link created but target did not resolve");
	}
}
catch (Exception ex)
{
	Fail("Symlink creation", ex.Message);

	// Diagnose why
	try
	{
		using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
			@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
		var devMode = key?.GetValue("AllowDevelopmentWithoutDevLicense");
		Log($"    Developer Mode: {(devMode is int v && v == 1 ? "enabled" : "not enabled")}");
	}
	catch { Log("    Developer Mode: cannot read registry"); }

	var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
	var principal = new System.Security.Principal.WindowsPrincipal(identity);
	var isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
	Log($"    Running elevated: {isAdmin}");
}
finally
{
	try { File.Delete(gateLinkFile); } catch { }
	try { File.Delete(gateFile); } catch { }
}

Log("");

if (!canSymlink)
{
	Log("Cannot create symlinks. Skipping all symlink tests.");
	Log("Run elevated or enable Developer Mode to proceed.");
	Log("");

	// Jump to fallback investigation
	goto Fallbacks;
}

// ---------------------------------------------------------------------------
// Build fixture
// ---------------------------------------------------------------------------

Log("--- Building test fixture ---");

v1Dir    = Path.Combine(testDir, "v1", "sea");
v2Dir    = Path.Combine(testDir, "v2", "sea");
binDir   = Path.Combine(testDir, "bin");
seaLink  = Path.Combine(binDir, "sea.exe");
seawLink = Path.Combine(binDir, "seaw.exe");
var probeScript = Path.Combine(testDir, "probe.cs");
var sleepScript = Path.Combine(testDir, "sleep.cs");

Directory.CreateDirectory(v1Dir);
Directory.CreateDirectory(v2Dir);
Directory.CreateDirectory(binDir);

// Copy all files from real bin into v1/sea/
foreach (var file in Directory.GetFiles(realBin, "*", SearchOption.AllDirectories))
{
	var rel  = Path.GetRelativePath(realBin, file);
	var dest = Path.Combine(v1Dir, rel);
	Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
	File.Copy(file, dest, overwrite: true);
}

// Copy again into v2/sea/ (identical for now)
foreach (var file in Directory.GetFiles(realBin, "*", SearchOption.AllDirectories))
{
	var rel  = Path.GetRelativePath(realBin, file);
	var dest = Path.Combine(v2Dir, rel);
	Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
	File.Copy(file, dest, overwrite: true);
}

// Write a version marker into each so we can distinguish them
File.WriteAllText(Path.Combine(v1Dir, "VERSION_MARKER"), "v1");
File.WriteAllText(Path.Combine(v2Dir, "VERSION_MARKER"), "v2");

// Create symlinks in bin/
File.CreateSymbolicLink(seaLink,  Path.Combine(v1Dir, "sea.exe"));
File.CreateSymbolicLink(seawLink, Path.Combine(v1Dir, "seaw.exe"));

// Write probe script — note: scripts run inside the daemon cache process,
// so ProcessPath/BaseDirectory reflect the cache dir, not sea.exe's view.
// We report them for informational purposes only.
File.WriteAllText(probeScript, @"
Console.WriteLine($""ScriptProcessPath={Environment.ProcessPath}"");
Console.WriteLine($""ScriptBaseDirectory={AppContext.BaseDirectory}"");
Console.WriteLine(""(Note: these are the daemon-cached script process paths,"");
Console.WriteLine("" not the sea.exe CLI paths. The --version test validates CLI DLL loading.)"");
return 0;
");

// Write sleep script (long-running, for repointing test)
File.WriteAllText(sleepScript, @"
Console.WriteLine(""STARTED"");
Console.Out.Flush();
Thread.Sleep(30000);
Console.WriteLine(""DONE"");
return 0;
");

Log($"  Fixture built: {testDir}");
Log($"  v1 symlink:    {new FileInfo(seaLink).LinkTarget}");
Log("");

// ---------------------------------------------------------------------------
// Test 1: .NET Path Resolution (make-or-break)
// ---------------------------------------------------------------------------

Log("--- Test 1: .NET Path Resolution ---");

var (rc1, out1, err1) = Run(seaLink, "--version");
if (rc1 == 0)
{
	Pass("Symlinked sea.exe --version", out1.Trim());

	// Now probe the paths
	var (rc1b, out1b, err1b) = Run(seaLink, $"\"{probeScript}\"");
	if (rc1b == 0)
	{
		Pass("Path probe via symlink");
		foreach (var line in out1b.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			Log($"    {line.Trim()}");
	}
	else
	{
		Fail("Path probe via symlink", $"exit={rc1b} stderr={err1b.Trim()}");
	}
}
else
{
	Fail("Symlinked sea.exe --version", $"exit={rc1} stderr={err1.Trim()}");
	Log("    .NET could not load dependencies through the symlink.");
	Log("    This is a fundamental blocker for file-level symlinks.");
	Log("");
	goto Fallbacks;
}

Log("");

// ---------------------------------------------------------------------------
// Test 2: seaw.exe Identity Detection
// ---------------------------------------------------------------------------

Log("--- Test 2: seaw.exe Identity ---");

// seaw.exe is a WinExe, --version should still work but may not produce console output.
// Use a script that checks the exe name instead.
var identScript = Path.Combine(testDir, "ident.cs");
File.WriteAllText(identScript, @"
var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? """");
Console.WriteLine($""ExeName={name}"");
return name.Equals(""seaw"", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
");

var (rc2, out2, err2) = Run(seawLink, $"\"{identScript}\"");
if (rc2 == 0 && out2.Contains("ExeName=seaw", StringComparison.OrdinalIgnoreCase))
{
	Pass("seaw identity detection through symlink", out2.Trim());
}
else if (rc2 == 0)
{
	// Process started but identity wrong
	Fail("seaw identity detection", $"expected ExeName=seaw, got: {out2.Trim()}");
}
else
{
	Fail("seaw identity detection", $"exit={rc2} stderr={err2.Trim()}");
}

Log("");

// ---------------------------------------------------------------------------
// Test 3: Script Execution End-to-End
// ---------------------------------------------------------------------------

Log("--- Test 3: Script Execution ---");

var helloScript = Path.Combine(testDir, "hello.cs");
File.WriteAllText(helloScript, @"
Console.WriteLine(""Hello from symlinked SeaShell!"");
return 0;
");

var (rc3, out3, err3) = Run(seaLink, $"\"{helloScript}\"");
if (rc3 == 0 && out3.Contains("Hello from symlinked SeaShell!"))
{
	Pass("Script execution through symlink");
}
else
{
	Fail("Script execution through symlink", $"exit={rc3} stderr={err3.Trim()}");
}

Log("");

// ---------------------------------------------------------------------------
// Test 4: FileAssoc Sibling Discovery
// ---------------------------------------------------------------------------

Log("--- Test 4: FileAssoc Sibling Discovery ---");

// Instead of actually registering, just check if the sibling lookup logic works.
// FileAssoc.GetExePathForExtension looks for seaw.exe next to Environment.ProcessPath.
var siblingScript = Path.Combine(testDir, "sibling.cs");
File.WriteAllText(siblingScript, @"
var procDir = Path.GetDirectoryName(Environment.ProcessPath);
if (procDir == null) { Console.WriteLine(""NO_PROCDIR""); return 1; }
var seawCandidate = Path.Combine(procDir, ""seaw.exe"");
var exists = File.Exists(seawCandidate);
Console.WriteLine($""SiblingDir={procDir}"");
Console.WriteLine($""SeawExists={exists}"");
Console.WriteLine($""SeawPath={seawCandidate}"");
if (exists)
{
    var info = new FileInfo(seawCandidate);
    Console.WriteLine($""SeawIsSymlink={info.LinkTarget != null}"");
    Console.WriteLine($""SeawTarget={info.LinkTarget}"");
}
return exists ? 0 : 1;
");

var (rc4, out4, err4) = Run(seaLink, $"\"{siblingScript}\"");
if (rc4 == 0 && out4.Contains("SeawExists=True"))
{
	Pass("Sibling seaw.exe found next to symlinked sea.exe");
	foreach (var line in out4.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		Log($"    {line.Trim()}");
}
else
{
	Fail("Sibling seaw.exe discovery", $"exit={rc4} out={out4.Trim()} err={err4.Trim()}");
}

Log("");

// ---------------------------------------------------------------------------
// Test 5: Daemon Lifecycle
// ---------------------------------------------------------------------------

Log("--- Test 5: Daemon Lifecycle ---");
Log("    (skipped in automated test — daemon lifecycle changes system state)");
Skip("Daemon lifecycle", "requires daemon stop/start affecting the real installation");
Log("");

// ---------------------------------------------------------------------------
// Test 6: Symlink Repointing While Running
// ---------------------------------------------------------------------------

Log("--- Test 6: Symlink Repointing ---");

Process? bgProc = null;
try
{
	// Start a long-running process via the v1 symlink
	bgProc = StartBackground(seaLink, $"\"{sleepScript}\"");

	// Wait for it to emit "STARTED"
	var startLine = bgProc.StandardOutput.ReadLine();
	if (startLine?.Trim() != "STARTED")
	{
		Fail("Repointing — start background process", $"expected STARTED, got: {startLine}");
	}
	else
	{
		var bgPid = bgProc.Id;
		Log($"    Background process started: PID={bgPid}");

		// Repoint symlink to v2
		File.Delete(seaLink);
		File.CreateSymbolicLink(seaLink, Path.Combine(v2Dir, "sea.exe"));
		Log($"    Symlink repointed to v2: {new FileInfo(seaLink).LinkTarget}");

		// Verify old process still alive
		if (!bgProc.HasExited)
		{
			Pass("Old process survives symlink repointing");
		}
		else
		{
			Fail("Old process survived", "process exited after symlink was repointed");
		}

		// Verify the symlink now targets v2 at the filesystem level
		var linkTarget = new FileInfo(seaLink).LinkTarget;
		var targetDir  = linkTarget != null ? Path.GetDirectoryName(Path.GetFullPath(linkTarget)) : null;
		var v2Marker   = targetDir != null ? Path.Combine(targetDir, "VERSION_MARKER") : null;
		if (v2Marker != null && File.Exists(v2Marker) && File.ReadAllText(v2Marker).Trim() == "v2")
			Pass("Symlink filesystem resolves to v2");
		else
			Fail("Symlink filesystem v2 check", $"target={linkTarget}");

		// Run --version through the repointed symlink — proves DLL resolution still works
		var (rc6b, out6b, err6b) = Run(seaLink, "--version");
		if (rc6b == 0)
			Pass("Repointed symlink — sea.exe --version", out6b.Trim());
		else
			Fail("Repointed symlink — sea.exe --version", $"exit={rc6b} err={err6b.Trim()}");
	}
}
catch (Exception ex)
{
	Fail("Repointing test", ex.Message);
}
finally
{
	if (bgProc != null && !bgProc.HasExited)
	{
		bgProc.Kill(true);
		bgProc.WaitForExit(5000);
	}
	bgProc?.Dispose();
}

// Restore v1 symlink for remaining tests
try
{
	if (File.Exists(seaLink)) File.Delete(seaLink);
	File.CreateSymbolicLink(seaLink, Path.Combine(v1Dir, "sea.exe"));
}
catch { }

Log("");

// ---------------------------------------------------------------------------
// Test 7: Task Scheduler (full lifecycle across repointing)
// ---------------------------------------------------------------------------

Log("--- Test 7: Task Scheduler ---");

var taskName    = $"SeaShellSymlinkTest-{Guid.NewGuid():N}".Substring(0, 40);
var taskScript  = Path.Combine(testDir, "task_test.cs");
var taskOutFile = Path.Combine(testDir, "task_output.txt");

// Write a script that writes its PID and the sea.exe symlink target to a file, then sleeps.
// We can't check AppContext.BaseDirectory here (it's the daemon cache).
// Instead, check the symlink target directly to see which version sea.exe resolves to.
File.WriteAllText(taskScript, $@"
var seawPath = @""{seawLink!.Replace("\\", "\\\\")}"";
var target = new FileInfo(seawPath).LinkTarget;
var ver = ""unknown"";
if (target != null)
{{
    var targetDir = Path.GetDirectoryName(Path.GetFullPath(target));
    var marker = Path.Combine(targetDir!, ""VERSION_MARKER"");
    if (File.Exists(marker)) ver = File.ReadAllText(marker).Trim();
}}
var outPath = @""{taskOutFile.Replace("\\", "\\\\")}"";
File.AppendAllText(outPath, $""PID={{Environment.ProcessId}} MARKER={{ver}}\n"");
Thread.Sleep(15000);
return 0;
");

try
{
	// Ensure output file is clean
	if (File.Exists(taskOutFile)) File.Delete(taskOutFile);

	// Step 1: Register task
	Log($"    Task name: {taskName}");
	var (rcReg, outReg, errReg) = Run("schtasks.exe",
		$"/Create /TN \"{taskName}\" /TR \"\\\"{seawLink}\\\" \\\"{taskScript}\\\"\" /SC ONCE /ST 00:00 /F",
		timeout: 10_000);

	if (rcReg != 0)
	{
		Fail("Task Scheduler — register", $"exit={rcReg} err={errReg.Trim()}");
		goto TaskCleanup;
	}
	Pass("Task Scheduler — register");

	// Step 2: Start the task
	var (rcRun, _, errRun) = Run("schtasks.exe", $"/Run /TN \"{taskName}\"", timeout: 10_000);
	if (rcRun != 0)
	{
		Fail("Task Scheduler — start", $"exit={rcRun} err={errRun.Trim()}");
		goto TaskCleanup;
	}

	// Wait for output file to appear
	var taskStartWait = Stopwatch.StartNew();
	while (!File.Exists(taskOutFile) && taskStartWait.ElapsedMilliseconds < 10_000)
		Thread.Sleep(500);

	if (!File.Exists(taskOutFile))
	{
		Fail("Task Scheduler — start", "task did not produce output within 10s");
		goto TaskCleanup;
	}

	var run1Output = File.ReadAllText(taskOutFile).Trim();
	Log($"    Run 1 output: {run1Output}");
	Pass("Task Scheduler — first run produced output");

	// Step 3: Verify running
	var (rcQ, outQ, _) = Run("schtasks.exe", $"/Query /TN \"{taskName}\" /FO CSV /NH", timeout: 10_000);
	Log($"    Task state: {outQ.Trim()}");

	// Step 4: Repoint symlink to v2
	File.Delete(seawLink);
	File.CreateSymbolicLink(seawLink, Path.Combine(v2Dir, "seaw.exe"));
	Log($"    Symlink repointed to v2");

	// Step 5: Verify old task still running (query status)
	Thread.Sleep(1000);
	var (rcQ2, outQ2, _) = Run("schtasks.exe", $"/Query /TN \"{taskName}\" /FO CSV /NH", timeout: 10_000);
	if (outQ2.Contains("Running", StringComparison.OrdinalIgnoreCase))
		Pass("Task Scheduler — old task survives repointing");
	else
		Log($"    Task state after repoint: {outQ2.Trim()} (may have finished)");

	// Step 6: End the task
	var (rcEnd, _, errEnd) = Run("schtasks.exe", $"/End /TN \"{taskName}\"", timeout: 10_000);
	if (rcEnd == 0)
		Pass("Task Scheduler — end task");
	else
		Log($"    End result: exit={rcEnd} err={errEnd.Trim()} (task may have already finished)");

	Thread.Sleep(1000);

	// Step 7: Restart with repointed symlink — should pick up v2
	if (File.Exists(taskOutFile)) File.Delete(taskOutFile);

	var (rcRun2, _, errRun2) = Run("schtasks.exe", $"/Run /TN \"{taskName}\"", timeout: 10_000);
	if (rcRun2 != 0)
	{
		Fail("Task Scheduler — restart after repoint", $"exit={rcRun2} err={errRun2.Trim()}");
		goto TaskCleanup;
	}

	taskStartWait.Restart();
	while (!File.Exists(taskOutFile) && taskStartWait.ElapsedMilliseconds < 10_000)
		Thread.Sleep(500);

	if (File.Exists(taskOutFile))
	{
		var run2Output = File.ReadAllText(taskOutFile).Trim();
		Log($"    Run 2 output: {run2Output}");

		if (run2Output.Contains("MARKER=v2"))
			Pass("Task Scheduler — restarted task uses v2 target");
		else if (run2Output.Contains("MARKER=v1"))
			Fail("Task Scheduler — restart still using v1", "symlink repointing not picked up");
		else
			Log($"    Unexpected marker: {run2Output}");
	}
	else
	{
		Fail("Task Scheduler — restart", "no output after repoint and restart");
	}

	// Step 8-9: End and verify clean termination
	Thread.Sleep(1000);
	Run("schtasks.exe", $"/End /TN \"{taskName}\"", timeout: 10_000);
}
catch (Exception ex)
{
	Fail("Task Scheduler", ex.Message);
}

TaskCleanup:
// Step 10: Cleanup
try
{
	Run("schtasks.exe", $"/End /TN \"{taskName}\"", timeout: 5000);
	Thread.Sleep(1000);
	Run("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", timeout: 5000);
	Log("    Task cleaned up");
}
catch { }

// Restore v1 symlink
try
{
	if (File.Exists(seawLink)) File.Delete(seawLink);
	File.CreateSymbolicLink(seawLink, Path.Combine(v1Dir, "seaw.exe"));
}
catch { }

Log("");

// ---------------------------------------------------------------------------
// Test 8: UAC / Elevation
// ---------------------------------------------------------------------------

Log("--- Test 8: UAC / Elevation ---");
Skip("UAC elevation", "requires interactive testing");
Log("");

// ---------------------------------------------------------------------------
// Fallbacks
// ---------------------------------------------------------------------------

Fallbacks:

Log("--- Fallback Investigation ---");
Log("");

// Alt A: Directory Junction
Log("  Alt A: Directory Junction");
var juncBinDir = Path.Combine(testDir, "junc");
try
{
	// Ensure v1/v2 dirs exist (may not if privilege gate failed — rebuild minimal fixture)
	if (v1Dir == null || !Directory.Exists(v1Dir))
	{
		v1Dir = Path.Combine(testDir, "v1", "sea");
		Directory.CreateDirectory(v1Dir);
		foreach (var file in Directory.GetFiles(realBin, "*", SearchOption.AllDirectories))
		{
			var rel  = Path.GetRelativePath(realBin, file);
			var dest = Path.Combine(v1Dir, rel);
			Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
			File.Copy(file, dest, overwrite: true);
		}
		File.WriteAllText(Path.Combine(v1Dir, "VERSION_MARKER"), "v1");
	}
	if (v2Dir == null || !Directory.Exists(v2Dir))
	{
		v2Dir = Path.Combine(testDir, "v2", "sea");
		Directory.CreateDirectory(v2Dir);
		foreach (var file in Directory.GetFiles(realBin, "*", SearchOption.AllDirectories))
		{
			var rel  = Path.GetRelativePath(realBin, file);
			var dest = Path.Combine(v2Dir, rel);
			Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
			File.Copy(file, dest, overwrite: true);
		}
		File.WriteAllText(Path.Combine(v2Dir, "VERSION_MARKER"), "v2");
	}

	// Create junction:  junc -> v1/sea
	// Junction points don't need special privileges on Windows.
	var (rcJunc, _, errJunc) = Run("cmd.exe", $"/c mklink /J \"{juncBinDir}\" \"{v1Dir}\"");
	if (rcJunc != 0)
	{
		Fail("Directory junction creation", errJunc.Trim());
	}
	else
	{
		var juncSea = Path.Combine(juncBinDir, "sea.exe");

		// 1. Can sea.exe start through a junction? (proves DLL resolution)
		var (rcJ1, outJ1, errJ1) = Run(juncSea, "--version");
		if (rcJ1 == 0)
			Pass("Junction — sea.exe --version", outJ1.Trim());
		else
		{
			Fail("Junction — sea.exe --version", $"exit={rcJ1} stderr={errJ1.Trim()}");
			goto JuncDone;
		}

		// 2. Can scripts execute through junction?
		var juncHello = Path.Combine(testDir, "junc_hello.cs");
		File.WriteAllText(juncHello, "Console.WriteLine(\"Hello from junction!\"); return 0;");
		var (rcJ2, outJ2, errJ2) = Run(juncSea, $"\"{juncHello}\"");
		if (rcJ2 == 0 && outJ2.Contains("Hello from junction!"))
			Pass("Junction — script execution");
		else
			Fail("Junction — script execution", $"exit={rcJ2} err={errJ2.Trim()}");

		// 3. Verify junction points to v1 (filesystem check, not runtime)
		var juncMarker = Path.Combine(juncBinDir, "VERSION_MARKER");
		if (File.Exists(juncMarker) && File.ReadAllText(juncMarker).Trim() == "v1")
			Pass("Junction — filesystem resolves to v1");
		else
			Fail("Junction — filesystem v1 check", $"marker={File.ReadAllText(juncMarker).Trim()}");

		// 4. Repoint junction: remove + recreate pointing to v2
		Run("cmd.exe", $"/c rmdir \"{juncBinDir}\"");
		var (rcJunc2, _, errJunc2) = Run("cmd.exe", $"/c mklink /J \"{juncBinDir}\" \"{v2Dir}\"");
		if (rcJunc2 != 0)
		{
			Fail("Junction — repoint creation", errJunc2.Trim());
			goto JuncDone;
		}

		// 5. Filesystem now shows v2?
		if (File.Exists(juncMarker) && File.ReadAllText(juncMarker).Trim() == "v2")
			Pass("Junction — filesystem resolves to v2 after repoint");
		else
			Fail("Junction — filesystem v2 check", "marker not found or wrong value");

		// 6. sea.exe still works after repoint?
		var (rcJ3, outJ3, errJ3) = Run(juncSea, "--version");
		if (rcJ3 == 0)
			Pass("Junction — sea.exe --version after repoint", outJ3.Trim());
		else
			Fail("Junction — sea.exe after repoint", $"exit={rcJ3} stderr={errJ3.Trim()}");

		// 7. Script execution still works after repoint?
		var (rcJ4, outJ4, errJ4) = Run(juncSea, $"\"{juncHello}\"");
		if (rcJ4 == 0 && outJ4.Contains("Hello from junction!"))
			Pass("Junction — script execution after repoint");
		else
			Fail("Junction — script after repoint", $"exit={rcJ4} err={errJ4.Trim()}");

		// 8. Repointing while a process is running
		Log("    Testing repoint while process is running...");
		var juncSleep = Path.Combine(testDir, "junc_sleep.cs");
		File.WriteAllText(juncSleep, @"
Console.WriteLine(""STARTED"");
Console.Out.Flush();
Thread.Sleep(30000);
return 0;
");
		Process? juncBg = null;
		try
		{
			juncBg = StartBackground(juncSea, $"\"{juncSleep}\"");
			var line = juncBg.StandardOutput.ReadLine();
			if (line?.Trim() == "STARTED")
			{
				var juncPid = juncBg.Id;
				Log($"    Background PID={juncPid}");

				// Repoint back to v1 while process is running
				Run("cmd.exe", $"/c rmdir \"{juncBinDir}\"");
				Run("cmd.exe", $"/c mklink /J \"{juncBinDir}\" \"{v1Dir}\"");

				Thread.Sleep(500);
				if (!juncBg.HasExited)
					Pass("Junction — running process survives repoint");
				else
					Fail("Junction — running process survived repoint", "process exited");

				// New process through repointed junction
				var (rcJ5, outJ5, _) = Run(juncSea, "--version");
				if (rcJ5 == 0)
					Pass("Junction — new process works after repoint during run");
				else
					Fail("Junction — new process after repoint", $"exit={rcJ5}");
			}
			else
			{
				Fail("Junction — background start", $"expected STARTED, got: {line}");
			}
		}
		finally
		{
			if (juncBg != null && !juncBg.HasExited) { juncBg.Kill(true); juncBg.WaitForExit(5000); }
			juncBg?.Dispose();
		}
	}
	JuncDone:;
}
catch (Exception ex)
{
	Fail("Directory junction", ex.Message);
}

Log("");

// Alt B: .runtimeconfig.dev.json with additional probing paths
Log("  Alt B: runtimeconfig.dev.json probing (info only)");
Log("    If file symlinks fail due to AppContext.BaseDirectory, a");
Log("    sea.runtimeconfig.dev.json with additionalProbingPaths could redirect");
Log("    assembly loading to the target directory. Not tested automatically.");
Skip("runtimeconfig.dev.json probing", "requires manual setup per version");
Log("");

// Alt C: Hard Links
Log("  Alt C: Hard Links");
var hardBinDir = Path.Combine(testDir, "hard");
try
{
	Directory.CreateDirectory(hardBinDir);
	// Hard link just the exe — DLLs must also be linked or present
	var hardSea = Path.Combine(hardBinDir, "sea.exe");
	var (rcHL, _, errHL) = Run("cmd.exe", $"/c mklink /H \"{hardSea}\" \"{Path.Combine(v1Dir, "sea.exe")}\"");
	if (rcHL == 0)
	{
		Pass("Hard link creation");
		// Note: this will fail to run because DLLs are not present in hardBinDir
		var (rcH1, outH1, errH1) = Run(hardSea, "--version");
		if (rcH1 == 0)
			Pass("Hard link — sea.exe --version (unexpected!)", outH1.Trim());
		else
			Log($"    Hard link runs without DLLs: FAIL as expected (DLLs not in hard dir)");

		// Hard link ALL files and retry
		foreach (var file in Directory.GetFiles(v1Dir!, "*", SearchOption.AllDirectories))
		{
			var rel  = Path.GetRelativePath(v1Dir, file);
			var dest = Path.Combine(hardBinDir, rel);
			if (dest == hardSea) continue; // already linked
			Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
			// Hard links only work on same volume and only for files
			Run("cmd.exe", $"/c mklink /H \"{dest}\" \"{file}\"");
		}
		var (rcH2, outH2, errH2) = Run(hardSea, "--version");
		if (rcH2 == 0)
			Pass("Hard link (all files) — sea.exe --version", outH2.Trim());
		else
			Fail("Hard link (all files)", $"exit={rcH2} err={errH2.Trim()}");
	}
	else
	{
		Fail("Hard link creation", errHL.Trim());
	}
}
catch (Exception ex)
{
	Fail("Hard links", ex.Message);
}

Log("");

// ---------------------------------------------------------------------------
// Cleanup
// ---------------------------------------------------------------------------

Log("--- Cleanup ---");
try
{
	// Remove junction first (rmdir, not recursive delete)
	if (Directory.Exists(juncBinDir))
		Run("cmd.exe", $"/c rmdir \"{juncBinDir}\"");

	Directory.Delete(testDir, recursive: true);
	Log("  Test directory cleaned up.");
}
catch (Exception ex)
{
	Log($"  Cleanup partial: {ex.Message}");
	Log($"  Manual cleanup needed: {testDir}");
}

Log("");

// ---------------------------------------------------------------------------
// Summary
// ---------------------------------------------------------------------------

Log("=== Summary ===");
Console.ForegroundColor = ConsoleColor.Green;  Console.Write($"  {passed} passed");
Console.ResetColor(); Console.Write("  ");
Console.ForegroundColor = ConsoleColor.Red;    Console.Write($"  {failed} failed");
Console.ResetColor(); Console.Write("  ");
Console.ForegroundColor = ConsoleColor.Yellow; Console.Write($"  {skipped} skipped");
Console.ResetColor(); Console.WriteLine();
Log("");

return failed > 0 ? 1 : 0;
