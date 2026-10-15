using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;
using SeaShell.Protocol;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Take down everything that could hold handles or keep running against the
/// target, in escalating order: polite IPC stop, Task Scheduler end, kill by
/// pid marker, kill by process name. Every step is scoped to the install
/// TARGET — an elevated SYSTEM pass must never end or kill the invoking
/// admin's daemons, and vice versa.
/// </summary>
internal static class InstanceStopper
{
	public static async Task StopAsync(InstallTarget target)
	{
		// 1. Graceful stop via IPC on every live SeaShell pipe for the TARGET
		//    identity (covers any version — legacy or new — that still holds a
		//    pipe). Matches both the SID token and the pre-0.6.0 username token,
		//    so upgrading over an old install stops its daemons too. For the
		//    SYSTEM target this works against 0.6.0+ daemons (Administrators ACE
		//    on the pipe); older SYSTEM daemons fall through to steps 2/3.
		foreach (var ep in TransportEndpoint.EnumerateAllSeashellEndpoints())
		{
			if (ep.IsElevator) continue; // elevator accepts spawn requests, not StopRequest
			if (!target.Identity.Matches(ep.Identity)) continue;
			await DaemonIpc.StopAsync(ep.Address);
		}

		// 2. Ask Task Scheduler to end any registered daemon/elevator tasks for
		//    the target (catches task-scheduler-launched daemons that aren't
		//    currently holding the IPC pipe open, and elevators where IPC stop
		//    isn't the right signal). "({DisplayName})" matches pre-0.6.0 names
		//    too, including "(SYSTEM)".
		if (target.IsWindows)
		{
			foreach (var path in TaskQuery.EnumerateFolder().ToList())
			{
				if (!SeaShellTaskNames.MatchesTarget(path, target.DisplayName)) continue;
				TaskQuery.End(path);
			}
		}

		// 3. Kill by pid markers — daemons/elevators of the target whose task is
		//    gone and who ignore IPC. The markers live in the target's version
		//    dirs and are readable (and the processes killable) by an elevated
		//    installer even when the target is SYSTEM.
		foreach (var (_, dir) in VersionDiscovery.EnumerateVersions(target.DataDir))
		{
			foreach (var marker in new[] { MarkerFile.DaemonPid, MarkerFile.ElevatorPid })
			{
				var pidStr = MarkerFile.Read(dir, marker);
				if (pidStr == null || !int.TryParse(pidStr, out var pid)) continue;
				try
				{
					using var proc = Process.GetProcessById(pid);
					if (proc.ProcessName.StartsWith("seashell-", StringComparison.OrdinalIgnoreCase))
						proc.Kill();
				}
				catch { } // already gone, or pid reused by something else
			}
		}

		// 4. Hard kill by process name — absolute fallback for hung daemons that
		//    ignore IPC and left no usable markers. Only for the ambient identity:
		//    name-based killing can't distinguish owners, and an elevated --system
		//    install must not take down the admin's own user daemons.
		if (target.Scope.HardKillByNameFallback)
		{
			if (target.IsWindows)
			{
				try
				{
					foreach (var p in Process.GetProcessesByName("seashell-daemon"))
						try { p.Kill(); } catch { }
					foreach (var p in Process.GetProcessesByName("seashell-elevator"))
						try { p.Kill(); } catch { }
				}
				catch { }
			}
			else
			{
				try { EnvironmentProbe.RunProcess("pkill", "-f seashell-daemon"); } catch { }
				try { EnvironmentProbe.RunProcess("pkill", "-f seashell-elevator"); } catch { }
			}
		}

		// 5. Brief pause so Windows releases file locks before cleanup.
		await Task.Delay(500);
	}
}