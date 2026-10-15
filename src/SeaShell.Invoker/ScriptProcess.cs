using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SeaShell.Invoker;

/// <summary>
/// A started script process. Captures redirected output as it arrives and ends the script —
/// optionally with every process it started — without waiting on its output streams.
///
/// A child the script starts inherits the redirected stdout/stderr, so the streams only
/// reach EOF once every such child has exited, however long after the script that is.
/// Collecting output therefore waits a bounded time for EOF and then returns what was read.
/// </summary>
internal sealed class ScriptProcess : IDisposable
{
	/// <summary>How long a cancelled script gets to act on ScriptStop before it is terminated.</summary>
	internal static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(5);

	/// <summary>How long collecting output waits for the streams to end after the script exited.</summary>
	internal static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

	/// <summary>How long to wait for a killed script to be gone.</summary>
	internal static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(2);

	private readonly ProcessTree? _tree;
	private readonly OutputPump? _stdout;
	private readonly OutputPump? _stderr;

	public Process Process { get; }

	private ScriptProcess(Process process, ProcessTree? tree, bool capture)
	{
		Process = process;
		_tree = tree;
		if (capture)
		{
			_stdout = new OutputPump(process.StandardOutput);
			_stderr = new OutputPump(process.StandardError);
		}
	}

	/// <summary>
	/// Start the script. With <paramref name="containTree"/>, <see cref="StopAsync"/> also
	/// terminates every process the script started, including ones it left behind on exit.
	/// </summary>
	public static ScriptProcess Start(ProcessStartInfo psi, bool containTree)
	{
		var tree = containTree ? ProcessTree.Prepare(psi) : null;
		var process = Process.Start(psi)!;
		tree?.Attach(process);
		return new ScriptProcess(process, tree, psi.RedirectStandardOutput && psi.RedirectStandardError);
	}

	/// <summary>The script's exit code, or -1 while it is still running.</summary>
	public int ExitCode => Process.HasExited ? Process.ExitCode : -1;

	/// <summary>Wait for the script to exit. False if <paramref name="ct"/> fired first.</summary>
	public async Task<bool> WaitForExitAsync(CancellationToken ct)
	{
		try
		{
			await Process.WaitForExitAsync(ct);
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
	}

	/// <summary>Wait at most <paramref name="timeout"/> for the script to exit. False if it is still running.</summary>
	public async Task<bool> WaitForExitAsync(TimeSpan timeout)
	{
		using var cts = new CancellationTokenSource(timeout);
		return await WaitForExitAsync(cts.Token);
	}

	/// <summary>
	/// End the script after <paramref name="grace"/> to exit on its own (after ScriptStop).
	/// A contained tree is terminated whether or not the script exited in time: what it
	/// started goes with it.
	/// </summary>
	public async Task StopAsync(TimeSpan grace)
	{
		await WaitForExitAsync(grace);
		Kill();
		await WaitForExitAsync(KillWaitTimeout);
	}

	/// <summary>
	/// Output captured so far, once the script has exited or been terminated: waits up to
	/// <see cref="OutputDrainTimeout"/> for the streams to end, then stops reading them.
	/// Empty when output is not captured.
	/// </summary>
	public async Task<(string Stdout, string Stderr)> CollectOutputAsync()
	{
		if (_stdout == null || _stderr == null)
			return ("", "");

		try { await Task.WhenAll(_stdout.Completion, _stderr.Completion).WaitAsync(OutputDrainTimeout); }
		catch (TimeoutException) { } // a child the script left running still holds the streams

		return (_stdout.Stop(), _stderr.Stop());
	}

	public void Dispose()
	{
		// Still running only when the run failed: end it
		try
		{
			if (!Process.HasExited)
				Kill();
		}
		catch { }

		_stdout?.Stop();
		_stderr?.Stop();
		_tree?.Dispose();
		Process.Dispose();
	}

	private void Kill()
	{
		if (_tree != null)
		{
			_tree.Kill();
			return;
		}

		try
		{
			if (!Process.HasExited)
				Process.Kill(entireProcessTree: false);
		}
		catch { }
	}

	/// <summary>
	/// Reads a redirected stream as data arrives, so the text read so far is available
	/// without waiting for the stream to end.
	/// </summary>
	private sealed class OutputPump
	{
		private readonly StringBuilder _text = new();
		private readonly CancellationTokenSource _stop = new();

		public OutputPump(StreamReader reader) => Completion = PumpAsync(reader);

		/// <summary>Completes at end of stream, on a read error, or once stopped. Never faults.</summary>
		public Task Completion { get; }

		/// <summary>Stop reading (which closes the stream) and return the text read.</summary>
		public string Stop()
		{
			_stop.Cancel();
			lock (_text) return _text.ToString();
		}

		private async Task PumpAsync(StreamReader reader)
		{
			var buffer = new char[4096];
			try
			{
				int read;
				while ((read = await reader.ReadAsync(buffer, _stop.Token)) > 0)
					lock (_text) _text.Append(buffer, 0, read);
			}
			catch { } // stopped, or the stream failed: keep what was read
			finally
			{
				reader.Dispose();
			}
		}
	}
}
