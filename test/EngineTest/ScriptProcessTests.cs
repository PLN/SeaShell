using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Invoker;
using Xunit;

namespace SeaShell.Engine.Tests;

/// <summary>
/// A script with captured output that starts a child and does not wait for it: the child
/// inherits the output pipes and keeps them open after the script is gone.
/// </summary>
public class ScriptProcessTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

	[Fact]
	public async Task Collect_ReturnsAllOutput_OfAScriptWithoutChildren()
	{
		using var script = ScriptProcess.Start(Shell(
			windows: "1..20000; [Console]::Error.WriteLine('err')",
			linux: "seq 1 20000; echo err >&2"), containTree: true);
		Assert.True(await WaitForExitAsync(script));

		var sw = Stopwatch.StartNew();
		var (stdout, stderr) = await script.CollectOutputAsync();

		Assert.True(sw.Elapsed < ScriptProcess.OutputDrainTimeout, $"collect took {sw.Elapsed}: the streams did not end");
		Assert.Equal(Enumerable.Range(1, 20000).Select(i => i.ToString()), Lines(stdout));
		Assert.Equal("err", stderr.Trim());
		Assert.Equal(0, script.ExitCode);
	}

	[Fact]
	public async Task Collect_DoesNotWaitForAChildThatHoldsTheOutput()
	{
		var pidFile = TempPidFile();
		var child = 0;
		try
		{
			using var script = ScriptProcess.Start(StartChild(pidFile, then: "exit 3"), containTree: true);
			Assert.True(await WaitForExitAsync(script));
			child = await ReadPidAsync(pidFile);

			var sw = Stopwatch.StartNew();
			var (stdout, _) = await script.CollectOutputAsync();

			// Proves the child held the output open: collection gave up waiting for EOF
			Assert.InRange(sw.Elapsed, ScriptProcess.OutputDrainTimeout - TimeSpan.FromMilliseconds(100), ScriptProcess.OutputDrainTimeout + TimeSpan.FromSeconds(5));
			Assert.Equal(3, script.ExitCode);
			Assert.Contains("started", stdout);
			// A normal exit leaves the child alone
			Assert.True(IsRunning(child));
		}
		finally
		{
			KillQuietly(child);
			File.Delete(pidFile);
		}
	}

	[Fact]
	public async Task Stop_EndsTheScriptAndItsChildren()
	{
		var pidFile = TempPidFile();
		var child = 0;
		try
		{
			using var script = ScriptProcess.Start(StartChild(pidFile, then: "sleep 120"), containTree: true);
			child = await ReadPidAsync(pidFile);

			var sw = Stopwatch.StartNew();
			await script.StopAsync(TimeSpan.Zero);
			var (stdout, _) = await script.CollectOutputAsync();

			Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"stop took {sw.Elapsed}");
			Assert.True(script.Process.HasExited);
			Assert.Contains("started", stdout);
			Assert.True(await GoneAsync(child), $"child {child} still running");
		}
		finally
		{
			KillQuietly(child);
			File.Delete(pidFile);
		}
	}

	[Fact]
	public async Task Stop_EndsChildrenTheScriptLeftBehind()
	{
		var pidFile = TempPidFile();
		var child = 0;
		try
		{
			using var script = ScriptProcess.Start(StartChild(pidFile, then: "exit 0"), containTree: true);
			Assert.True(await WaitForExitAsync(script));
			child = await ReadPidAsync(pidFile);
			Assert.True(IsRunning(child));

			await script.StopAsync(TimeSpan.Zero);

			Assert.True(await GoneAsync(child), $"orphaned child {child} still running");
		}
		finally
		{
			KillQuietly(child);
			File.Delete(pidFile);
		}
	}

	[Fact]
	public async Task Stop_EndsDescendantsOfAnExitedChild()
	{
		var pidFile = TempPidFile();
		var grandchild = 0;
		try
		{
			using var script = ScriptProcess.Start(StartChild(pidFile, then: "sleep 120", viaIntermediate: true), containTree: true);
			grandchild = await ReadPidAsync(pidFile);
			Assert.True(IsRunning(grandchild));

			await script.StopAsync(TimeSpan.Zero);

			Assert.True(await GoneAsync(grandchild), $"grandchild {grandchild} still running");
		}
		finally
		{
			KillQuietly(grandchild);
			File.Delete(pidFile);
		}
	}

	[Fact]
	public async Task Kill_SparesAProcessWhoseEnvironmentOnlyMentionsTheMarker()
	{
		if (!OperatingSystem.IsLinux()) return; // the environment marker is Linux-only

		var psi = Shell(windows: "", linux: "sleep 120");
		using var tree = ProcessTree.Prepare(psi);
		var marker = psi.Environment.Keys.Single(k => k.StartsWith("SEASHELL_TREE_"));

		var bystanderPsi = new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sleep 120" }, UseShellExecute = false };
		bystanderPsi.Environment["UNRELATED"] = $"{marker}=1";
		using var bystander = Process.Start(bystanderPsi)!;
		try
		{
			using var root = Process.Start(psi)!;
			tree.Attach(root);

			tree.Kill();

			Assert.True(root.WaitForExit(10_000));
			await Task.Delay(500);
			Assert.True(IsRunning(bystander.Id), "a process carrying the marker only inside a value was killed");
		}
		finally
		{
			KillQuietly(bystander.Id);
		}
	}

	// ── Helpers ─────────────────────────────────────────────────────────

	/// <summary>
	/// A script that prints "started", starts a silent long-running process the way a C#
	/// script would (Process.Start, no shell, output inherited) whose pid ends up in
	/// <paramref name="pidFile"/>, then runs <paramref name="then"/> ("exit N" or "sleep N").
	/// With <paramref name="viaIntermediate"/> the script starts it through a process that
	/// exits right away, so it is no longer linked to the script by its parent.
	/// </summary>
	private static ProcessStartInfo StartChild(string pidFile, string then, bool viaIntermediate = false)
	{
		var windowsThen = then.StartsWith("sleep ") ? $"Start-Sleep {then[6..]}" : then;
		var sleeper = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(
			Encoding.Unicode.GetBytes($"Set-Content -LiteralPath '{pidFile}' -Value $PID; Start-Sleep 120"));
		var start = viaIntermediate
			? $"$i = New-Object Diagnostics.ProcessStartInfo 'cmd.exe', '/c start /b powershell.exe {sleeper}'; "
				+ "$i.UseShellExecute = $false; [Diagnostics.Process]::Start($i).WaitForExit(); "
			: $"$i = New-Object Diagnostics.ProcessStartInfo 'powershell.exe', '{sleeper}'; "
				+ "$i.UseShellExecute = $false; [void][Diagnostics.Process]::Start($i); ";
		return Shell(
			windows: "[Console]::Out.WriteLine('started'); [Console]::Out.Flush(); " + start + windowsThen,
			linux: viaIntermediate
				? $"echo started; (sleep 120 & echo $! > '{pidFile}'); {then}"
				: $"echo started; sleep 120 & echo $! > '{pidFile}'; {then}");
	}

	private static ProcessStartInfo Shell(string windows, string linux)
	{
		var psi = OperatingSystem.IsWindows()
			? new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", windows } }
			: new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", linux } };
		psi.UseShellExecute = false;
		psi.RedirectStandardOutput = true;
		psi.RedirectStandardError = true;
		psi.CreateNoWindow = true;
		return psi;
	}

	private static string[] Lines(string text) =>
		text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

	private static string TempPidFile() =>
		Path.Combine(Path.GetTempPath(), $"seashell-test-{Guid.NewGuid():N}.pid");

	private static async Task<bool> WaitForExitAsync(ScriptProcess script)
	{
		using var cts = new CancellationTokenSource(Patience);
		return await script.WaitForExitAsync(cts.Token);
	}

	private static async Task<int> ReadPidAsync(string pidFile)
	{
		var deadline = DateTime.UtcNow + Patience;
		while (DateTime.UtcNow < deadline)
		{
			try
			{
				if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && pid > 0)
					return pid;
			}
			catch (IOException) { } // not written yet
			await Task.Delay(50);
		}
		throw new TimeoutException($"no pid in {pidFile}");
	}

	private static async Task<bool> GoneAsync(int pid)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
		while (IsRunning(pid))
		{
			if (DateTime.UtcNow > deadline) return false;
			await Task.Delay(50);
		}
		return true;
	}

	private static bool IsRunning(int pid)
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
			using var process = Process.GetProcessById(pid);
			return !process.HasExited;
		}
		catch (ArgumentException) { return false; }
	}

	private static void KillQuietly(int pid)
	{
		if (pid <= 0) return;
		try
		{
			using var process = Process.GetProcessById(pid);
			process.Kill();
		}
		catch { }
	}
}
