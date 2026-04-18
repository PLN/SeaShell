using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SeaShell.Installer;

/// <summary>
/// Runs a SeaShell operation (install/uninstall/status) as LocalSystem via a
/// transient one-shot scheduled task. Used by the <c>--system</c> flow in the
/// Bootstrapper.
///
/// <para>
/// Direct elevation isn't enough: named-pipe DACLs guard cross-user access,
/// so an elevated interactive user can't speak IPC with a SYSTEM-owned
/// daemon. Running the operation as SYSTEM fixes that plus any
/// identity-derived values (<c>Environment.UserName</c>,
/// <c>LocalApplicationData</c>) automatically.
/// </para>
///
/// <para>
/// Contract: caller creates <paramref name="stageDir"/> with whatever extra
/// files the script expects (e.g. a nupkg for install), hands us the <c>.cmd</c>
/// content, we register/run/poll/clean up. The script must write a sentinel
/// file at <c>{stageDir}\op.result</c> on completion containing at minimum a
/// line <c>EXITCODE=N</c>. Optional <c>{stageDir}\op.output</c> is mirrored
/// to the caller as captured stdout/stderr.
/// </para>
/// </summary>
public static class SystemOpRunner
{
	/// <summary>Result returned by <see cref="RunAsync"/>.</summary>
	public sealed record Result(int ExitCode, string Output, string? FailureReason);

	/// <summary>Reserved task-name folder for transient system ops.</summary>
	private const string SystemOpFolder = @"\SeaShell\";

	/// <summary>
	/// Stage a one-shot SYSTEM task running <paramref name="cmdScript"/>, fire
	/// it, poll for the sentinel, and return the aggregated result. Deletes
	/// the task and <paramref name="stageDir"/> before returning.
	/// </summary>
	public static async Task<Result> RunAsync(
		string stageDir,
		string cmdScript,
		string taskLabel,
		TimeSpan? timeout = null,
		Action<string>? log = null)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return new Result(1, "", "System-task bootstrap is Windows-only");

		Directory.CreateDirectory(stageDir);
		var cmdPath = Path.Combine(stageDir, "op.cmd");
		var sentinelPath = Path.Combine(stageDir, "op.result");
		var outputPath = Path.Combine(stageDir, "op.output");

		// Clean slate — stale sentinel from a prior failed run would false-positive the poll.
		try { File.Delete(sentinelPath); } catch { }
		try { File.Delete(outputPath); } catch { }
		File.WriteAllText(cmdPath, cmdScript);

		var taskId = Guid.NewGuid().ToString("N")[..12];
		var taskName = $"SeaShell System {taskLabel} {taskId}";
		var taskPath = SystemOpFolder + taskName;

		var xml = DaemonTasks.BuildTaskXml(
			command: "cmd.exe",
			arguments: $"/c \"{cmdPath}\"",
			principal: DaemonTasks.TaskPrincipal.System,
			trigger: DaemonTasks.TaskTrigger.None,
			runLevel: "HighestAvailable",
			description: $"SeaShell {taskLabel} (transient, SYSTEM)");

		if (!DaemonTasks.RegisterTaskFromXml(taskPath, xml, elevatedContext: true, log))
		{
			TryCleanup(taskPath, stageDir);
			return new Result(1, "", "Failed to register transient SYSTEM task");
		}

		var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(180));
		try
		{
			if (!TaskQuery.Run(taskPath))
				return new Result(1, "", "schtasks /Run failed for transient SYSTEM task");

			Result? result = null;
			while (DateTimeOffset.UtcNow < deadline)
			{
				await Task.Delay(500);

				if (File.Exists(sentinelPath))
				{
					result = ReadResult(sentinelPath, outputPath);
					break;
				}

				// If the task has transitioned back to Ready without a sentinel,
				// the wrapper crashed before writing. Surface that rather than
				// waiting for timeout.
				var state = TaskQuery.QueryState(taskPath);
				if (state != null && !state.Equals("Running", StringComparison.OrdinalIgnoreCase))
				{
					// Give the wrapper a moment to flush the sentinel — small
					// race between its last line and task transition.
					await Task.Delay(500);
					if (File.Exists(sentinelPath))
					{
						result = ReadResult(sentinelPath, outputPath);
						break;
					}
					result = new Result(-1, TryReadOutput(outputPath),
						"Task completed without writing sentinel (wrapper crashed?)");
					break;
				}
			}

			return result ?? new Result(-1, TryReadOutput(outputPath),
				$"Timed out after {(timeout ?? TimeSpan.FromSeconds(180)).TotalSeconds}s waiting for SYSTEM task");
		}
		finally
		{
			TryCleanup(taskPath, stageDir);
		}
	}

	internal static Result ReadResult(string sentinelPath, string outputPath)
	{
		int exit = -1;
		try
		{
			foreach (var line in File.ReadAllLines(sentinelPath))
			{
				if (line.StartsWith("EXITCODE=", StringComparison.Ordinal) &&
				    int.TryParse(line["EXITCODE=".Length..].Trim(), out var parsed))
				{
					exit = parsed;
					break;
				}
			}
		}
		catch { }
		return new Result(exit, TryReadOutput(outputPath), null);
	}

	private static string TryReadOutput(string outputPath)
	{
		try { return File.Exists(outputPath) ? File.ReadAllText(outputPath) : ""; }
		catch { return ""; }
	}

	private static void TryCleanup(string taskPath, string stageDir)
	{
		try { TaskQuery.End(taskPath); } catch { }
		try { TaskQuery.Delete(taskPath); } catch { }
		try { Directory.Delete(stageDir, recursive: true); } catch { }
	}
}
