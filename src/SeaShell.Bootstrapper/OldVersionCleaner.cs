using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Remove version directories this install supersedes. Which versions are
/// candidates and what may be done to each comes from
/// <see cref="VersionCleanupPlan"/>; this class only performs the steps.
/// </summary>
internal sealed class OldVersionCleaner
{
	/// <summary>One old version's daemon, as probed over its pipe marker.</summary>
	private readonly record struct DaemonProbe(DaemonProbeState State, int ActiveScripts);

	private readonly InstallTarget _target;

	public OldVersionCleaner(InstallTarget target) => _target = target;

	public async Task CleanAsync()
	{
		var mine = System.Version.TryParse(_target.Version, out var mv) ? mv : null;
		var uninstallable = VersionCleanupPlan.SelectOldestFirst(
			VersionDiscovery.EnumerateVersions(_target.DataDir), mine);
		if (uninstallable.Count == 0) return;

		Console.WriteLine();
		Console.WriteLine("  Cleaning up old versions:");

		foreach (var (ver, dir) in uninstallable)
		{
			Console.Write($"    v{ver}: ");

			// Step a: delete daemon.path and elevator.path markers — permanent.
			// With markers gone, BinaryDiscovery no longer returns this version
			// and new sea invocations can't start its daemon. If cleanup aborts
			// partway through (daemon still in use), the directory lingers until
			// the next install pass retries.
			MarkerFile.Delete(dir, MarkerFile.DaemonPath);
			MarkerFile.Delete(dir, MarkerFile.ElevatorPath);

			// Step b: probe the daemon once and decide how far we may go.
			var pipe = MarkerFile.Read(dir, MarkerFile.DaemonPipe);
			var probe = await ProbeDaemonAsync(pipe);
			var action = VersionCleanupPlan.Decide(probe.State);

			if (action == VersionCleanupAction.SkipInUse)
			{
				Console.WriteLine($"in use ({probe.ActiveScripts} active), skipped");
				continue;
			}

			if (action == VersionCleanupAction.StopThenRemove)
				await DaemonIpc.StopAsync(pipe!);
			else if (action == VersionCleanupAction.KillStaleThenRemove)
				KillStaleDaemon(dir);

			// Step c: remove scheduled tasks registered for this version.
			if (_target.IsWindows)
			{
				foreach (var marker in new[] { MarkerFile.DaemonTask, MarkerFile.ElevatorTask })
				{
					var taskPath = MarkerFile.Read(dir, marker);
					if (string.IsNullOrEmpty(taskPath)) continue;
					TaskQuery.End(taskPath);
					TaskQuery.Delete(taskPath);
				}
			}

			// Step d: remove version directory.
			try
			{
				Directory.Delete(dir, true);
				Console.WriteLine("removed");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"failed ({ex.Message})");
			}
		}
	}

	private static async Task<DaemonProbe> ProbeDaemonAsync(string? pipe)
	{
		if (string.IsNullOrEmpty(pipe)) return new DaemonProbe(DaemonProbeState.NoPipeMarker, 0);

		var ping = await DaemonIpc.PingAsync(pipe);
		// Pipe marker present but unreachable — a hung or already-gone daemon.
		if (ping == null) return new DaemonProbe(DaemonProbeState.Unreachable, 0);
		return ping.ActiveScripts > 0
			? new DaemonProbe(DaemonProbeState.Busy, ping.ActiveScripts)
			: new DaemonProbe(DaemonProbeState.Idle, 0);
	}

	// Kill by pid marker: the daemon ignored IPC, so only a hard kill is left.
	private static void KillStaleDaemon(string versionDir)
	{
		var pidStr = MarkerFile.Read(versionDir, MarkerFile.DaemonPid);
		if (pidStr != null && int.TryParse(pidStr, out var pid))
		{
			try { Process.GetProcessById(pid).Kill(); } catch { }
		}
	}
}