using System;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;
using SeaShell.Protocol;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Start/stop the daemon and elevator of the current install. Windows drives
/// them through Task Scheduler when a task exists, and falls back to asking
/// the sea binary directly.
/// </summary>
internal sealed class DaemonLifecycle
{
	private readonly InstallTarget _target;

	public DaemonLifecycle(InstallTarget target) => _target = target;

	public int Start()
	{
		var seaPath = EnvironmentProbe.FindSea(_target.BinDir, _target.IsWindows);
		if (seaPath == null)
		{
			Console.Error.WriteLine("  Not installed. Run 'seashell install' first.");
			return 1;
		}

		if (_target.IsWindows)
		{
			if (DaemonTasks.TryRunDaemonTask())
				Console.WriteLine("  daemon:   starting (task)");
			else
				Console.WriteLine(EnvironmentProbe.RunProcess(seaPath, "--daemon-start").Trim());

			if (DaemonTasks.TryRunElevatorTask())
				Console.WriteLine("  elevator: starting (task)");
			else
				Console.WriteLine("  elevator: no task registered");
		}
		else
		{
			Console.WriteLine(EnvironmentProbe.RunProcess(seaPath, "--daemon-start").Trim());
		}
		return 0;
	}

	public async Task<int> StopAsync()
	{
		var seaPath = EnvironmentProbe.FindSea(_target.BinDir, _target.IsWindows);
		if (seaPath == null)
		{
			Console.Error.WriteLine("  Not installed.");
			return 1;
		}

		var daemonAddress = TransportEndpoint.GetDaemonAddress(
			TransportEndpoint.CurrentUserIdentity, _target.Version);
		var ipcOk = await DaemonIpc.StopAsync(daemonAddress);
		Console.WriteLine($"  daemon:   {(ipcOk ? "stopped" : "not running")}");

		if (_target.IsWindows)
		{
			if (!ipcOk)
			{
				var path = TaskQuery.FindByPrefix($"SeaShell Daemon ({Environment.UserName})");
				if (path != null && TaskQuery.End(path))
					Console.WriteLine("  daemon:   stopped (task)");
			}

			var elevPath = TaskQuery.FindByPrefix($"SeaShell Elevator ({Environment.UserName})");
			if (elevPath != null)
			{
				Console.WriteLine(TaskQuery.End(elevPath)
					? "  elevator: stopped"
					: "  elevator: not running");
			}
			else
			{
				Console.WriteLine("  elevator: no task registered");
			}
		}
		return 0;
	}
}