// ── Host Child-Process Regression Test ───────────────────────────────
//
// A script run through ScriptHost (output captured) starts a child process
// and does not wait for it. The child inherits the captured stdout/stderr, so
// the pipes stay open after the script is gone — RunAsync used to wait for the
// child to exit, whatever the cancellation token said.
//
//   cancel — the script sleeps past cancellation: RunAsync returns within
//            seconds, flagged Cancelled, with the output so far, and the
//            child is gone.
//   reload — cancelled while the script exits slowly for a reload it
//            requested: same outcome, the reload does not happen.
//   reload-drain — cancelled after that script exited, while its output
//            (held open by the child) drains: same outcome.
//   exit   — the script returns while the child runs: RunAsync returns
//            promptly with the script's exit code and output.
//   plain  — a script without children: complete output and exit code.
//
// Exit 0 = pass, non-zero = fail.

//sea_nuget seashell.host

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Host;
using SeaShell.Invoker;

var testName = "host-child-process";
var failures = new List<string>();
var children = new List<int>();
var host = new ScriptHost();

void Fail(string message)
{
	failures.Add(message);
	Console.Error.WriteLine($"[{testName}]   FAIL: {message}");
}

// Cancel one second after the script reported its child; the run must end promptly with it.
async Task CancelledRunAsync(string then)
{
	var conn = new ScriptConnection();
	using var cts = new CancellationTokenSource();
	var childPid = 0;
	var sinceCancel = new Stopwatch();
	conn.MessageReceived += (payload, topic) =>
	{
		if (topic != "child") return;
		childPid = int.Parse(Encoding.UTF8.GetString(payload));
		children.Add(childPid);
		cts.Token.Register(sinceCancel.Start);
		cts.CancelAfter(TimeSpan.FromSeconds(1));
	};

	var run = host.RunSnippetAsync(Snippet(then), connection: conn, ct: cts.Token);
	if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(3))) != run)
	{
		Fail("RunAsync still running 3 minutes after start");
		return;
	}

	var r = await run;
	sinceCancel.Stop();
	Report(r, sinceCancel.Elapsed, "after cancellation");
	if (childPid == 0)
	{
		Fail("script never reported its child");
		return;
	}
	if (!r.Cancelled) Fail("result not flagged Cancelled");
	if (sinceCancel.Elapsed > TimeSpan.FromSeconds(20)) Fail($"returned {sinceCancel.Elapsed.TotalSeconds:F1}s after cancellation");
	if (!r.StandardOutput.Contains($"child {childPid}")) Fail("output written before cancellation is missing");
	if (!await GoneAsync(childPid)) Fail($"child {childPid} still running after cancellation");
}

try
{
	// ── cancel ───────────────────────────────────────────────────────

	Console.WriteLine($"[{testName}] cancel: script sleeps past cancellation...");
	await CancelledRunAsync("Thread.Sleep(600_000); return 0;");

	// ── reload ───────────────────────────────────────────────────────

	Console.WriteLine($"[{testName}] reload: cancelled while the script exits for a reload...");
	await CancelledRunAsync("""
		AppDomain.CurrentDomain.ProcessExit += (_, _) => Thread.Sleep(4000);
		Sea.RequestReload(reason: "test");
		Sea.ShutdownToken.WaitHandle.WaitOne(60_000);
		return 0;
		""");

	// Exits at once for the reload: the cancellation, a second later, falls in the
	// two-second output drain that the child holding the pipes forces
	Console.WriteLine($"[{testName}] reload-drain: cancelled while a reloading script's output drains...");
	await CancelledRunAsync("""
		Sea.RequestReload(reason: "test");
		Sea.ShutdownToken.WaitHandle.WaitOne(60_000);
		return 0;
		""");

	// ── exit ─────────────────────────────────────────────────────────

	Console.WriteLine($"[{testName}] exit: script returns while its child runs...");
	{
		var conn = new ScriptConnection();
		var childPid = 0;
		conn.MessageReceived += (payload, topic) =>
		{
			if (topic != "child") return;
			childPid = int.Parse(Encoding.UTF8.GetString(payload));
			children.Add(childPid);
		};

		var sw = Stopwatch.StartNew();
		var run = host.RunSnippetAsync(Snippet("Console.WriteLine(\"exiting\"); return 7;"), connection: conn);
		if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(3))) != run)
			Fail("RunAsync still running 3 minutes after start");
		else
		{
			var r = await run;
			Report(r, sw.Elapsed, "in total, compile included");
			if (childPid == 0) Fail("script never reported its child");
			else
			{
				// The child sleeps 600 s: anything near that means the call waited for it
				if (sw.Elapsed > TimeSpan.FromSeconds(60)) Fail($"returned after {sw.Elapsed.TotalSeconds:F1}s");
				if (r.ExitCode != 7) Fail($"exit code {r.ExitCode}, expected 7");
				if (r.Cancelled) Fail("result flagged Cancelled");
				if (!r.StandardOutput.Contains($"child {childPid}") || !r.StandardOutput.Contains("exiting"))
					Fail("output written before exit is missing");
			}
		}
	}

	// ── plain ────────────────────────────────────────────────────────

	Console.WriteLine($"[{testName}] plain: script without children...");
	{
		var r = await host.RunSnippetAsync("""
			using System;
			for (var i = 1; i <= 5000; i++) Console.WriteLine($"line {i}");
			Console.Error.WriteLine("to stderr");
			return 5;
			""");
		Console.WriteLine($"[{testName}]   exit {r.ExitCode}, {r.StandardOutput.Length} chars of stdout");
		if (r.ExitCode != 5) Fail($"exit code {r.ExitCode}, expected 5");
		var expected = string.Join("\n", Enumerable.Range(1, 5000).Select(i => $"line {i}"));
		if (r.StandardOutput.Replace("\r\n", "\n").TrimEnd() != expected) Fail("stdout incomplete");
		if (r.StandardError.Trim() != "to stderr") Fail($"stderr '{r.StandardError.Trim()}'");
	}
}
catch (Exception ex)
{
	Fail($"exception: {ex}");
}
finally
{
	foreach (var pid in children)
	{
		try { using var p = Process.GetProcessById(pid); p.Kill(); }
		catch { }
	}
}

if (failures.Count == 0)
{
	Console.WriteLine($"[{testName}] PASS: a child the script leaves running does not hold the run open");
	return 0;
}
Console.Error.WriteLine($"[{testName}] FAIL ({failures.Count})");
return 1;

// The script under test: starts a silent child that outlives it, prints and reports the
// child's pid over the host channel, then runs `then`.
static string Snippet(string then) => $$"""
	using System;
	using System.Diagnostics;
	using System.Threading;
	using SeaShell;

	var psi = OperatingSystem.IsWindows()
		? new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep 600")
		: new ProcessStartInfo("sleep", "600");
	psi.UseShellExecute = false;
	var child = Process.Start(psi)!;
	Console.WriteLine($"child {child.Id}");
	Sea.SendMessage(child.Id.ToString(), "child");
	{{then}}
	""";

void Report(ScriptResult r, TimeSpan elapsed, string what)
{
	Console.WriteLine($"[{testName}]   returned {elapsed.TotalSeconds:F1}s {what}: exit {r.ExitCode}, cancelled {r.Cancelled}");
	if (!string.IsNullOrWhiteSpace(r.StandardOutput))
		Console.WriteLine($"[{testName}]   stdout: {r.StandardOutput.Trim()}");
	if (!string.IsNullOrWhiteSpace(r.StandardError))
		Console.WriteLine($"[{testName}]   stderr: {r.StandardError.Trim()}");
}

static async Task<bool> GoneAsync(int pid)
{
	var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
	while (IsRunning(pid))
	{
		if (DateTime.UtcNow > deadline) return false;
		await Task.Delay(100);
	}
	return true;
}

static bool IsRunning(int pid)
{
	if (OperatingSystem.IsLinux())
	{
		// A killed orphan lingers as a zombie until init reaps it
		try
		{
			var stat = File.ReadAllText($"/proc/{pid}/stat");
			return stat[stat.LastIndexOf(')') + 2] != 'Z';
		}
		catch (IOException) { return false; }
	}
	try
	{
		using var p = Process.GetProcessById(pid);
		return !p.HasExited;
	}
	catch (ArgumentException) { return false; }
}
