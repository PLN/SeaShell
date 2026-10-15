using System;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;
using SeaShell.Protocol;

namespace SeaShell.Bootstrapper;

/// <summary>
/// The status views. Interpretation — which version is installed, whether the
/// daemon is up, whether an update is available — comes from
/// <see cref="StatusRules"/>.
/// </summary>
internal sealed class StatusReporter
{
	private readonly InstallTarget _target;
	private readonly BootstrapperOptions _options;

	public StatusReporter(InstallTarget target, BootstrapperOptions options)
	{
		_target = target;
		_options = options;
	}

	public async Task<int> RunAsync()
	{
		if (_options.SystemMode && _target.IsWindows)
			return await SystemStatusAsync();

		Console.WriteLine($"{{~}} SeaShell{(_options.SystemMode ? " (system)" : "")}");
		Console.WriteLine();

		var currentVersion = VersionDiscovery.GetCurrentVersion();
		var seaPath = EnvironmentProbe.FindSea(_target.BinDir, _target.IsWindows);
		var installed = seaPath != null;

		string? installedVersion = null;
		if (installed)
		{
			var verOutput = EnvironmentProbe.RunProcess(seaPath!, "--version").Trim();
			installedVersion = StatusRules.ParseInstalledVersion(verOutput);

			Console.WriteLine($"  Installed    v{installedVersion}");
			Console.WriteLine($"  Current      {SeaShellPaths.CurrentDir} -> v{currentVersion}");
			Console.WriteLine($"  Platform     {EnvironmentProbe.CurrentRid(_target.IsWindows)}");
		}
		else
		{
			Console.WriteLine("  Installed    no");
			Console.WriteLine($"  Bundled      v{_target.Version} ready to install");
		}

		Console.WriteLine($"  Bootstrapper v{_target.Version}");
		Console.WriteLine();

		if (installed)
		{
			var statusOutput = EnvironmentProbe.RunProcess(seaPath!, "--status").Trim();
			if (!string.IsNullOrEmpty(statusOutput))
			{
				var daemonRunning = StatusRules.DaemonReportsRunning(statusOutput);
				foreach (var line in statusOutput.Split('\n'))
				{
					var trimmed = line.Trim();
					if (trimmed.Length > 0) Console.WriteLine($"  {trimmed}");
				}

				if (!daemonRunning && _target.IsWindows)
				{
					var elevState = DaemonTasks.QueryElevatorTaskState();
					if (elevState != null) Console.WriteLine($"  elevator: task {elevState.ToLowerInvariant()}");
				}
			}
		}
		else
		{
			Console.WriteLine("  daemon       -");
			Console.WriteLine("  elevator     -");
		}

		var versions = VersionDiscovery.EnumerateVersions();
		if (versions.Count > 0)
		{
			Console.WriteLine();
			Console.WriteLine("  Versions:");
			foreach (var (ver, dir) in versions)
			{
				var isCurrent = currentVersion != null && ver == currentVersion ? " *" : "";
				var daemon = MarkerFile.Exists(dir, MarkerFile.DaemonPath) ? "daemon: ok" : "daemon: -";
				var elevator = MarkerFile.Exists(dir, MarkerFile.ElevatorPath) ? "elevator: ok" : "elevator: -";
				Console.WriteLine($"    v{ver}{isCurrent}  {daemon}  {elevator}");
			}
		}

		Console.WriteLine();

		switch (StatusRules.Advise(installed, installedVersion, _target.Version))
		{
			case StatusAdvice.UpdateAvailable:
				Console.WriteLine($"  Update: v{installedVersion} -> v{_target.Version}  (run 'seashell update')");
				break;
			case StatusAdvice.UpToDate:
				Console.WriteLine("  Up to date");
				break;
			default:
				Console.WriteLine("  Run 'seashell install' to get started");
				break;
		}

		await Task.CompletedTask;
		return 0;
	}

	/// <summary>
	/// Status view for the SYSTEM install, runnable from any user context.
	/// Filesystem and ping details need elevation (systemprofile ACLs, pipe
	/// ACE); without it the view degrades to task state + pipe presence probing.
	/// </summary>
	private async Task<int> SystemStatusAsync()
	{
		Console.WriteLine("{~} SeaShell (system)");
		Console.WriteLine();

		var sys = SeaShellIdentity.LocalSystem;
		var elevated = Elevation.IsElevated(true);

		var versions = VersionDiscovery.EnumerateVersions(sys.DataDir);
		var current = VersionDiscovery.GetCurrentVersion(sys.DataDir);
		if (versions.Count > 0)
		{
			Console.WriteLine($"  Installed    v{current?.ToString() ?? versions[0].Version.ToString()}");
			Console.WriteLine($"  Current      {SeaShellPaths.CurrentDirIn(sys.DataDir)} -> {(current != null ? $"v{current}" : "<missing>")}");
			foreach (var (ver, dir) in versions)
			{
				var isCurrent = current != null && ver == current ? " *" : "";
				var daemon = MarkerFile.Exists(dir, MarkerFile.DaemonPath) ? "daemon: ok" : "daemon: -";
				Console.WriteLine($"    v{ver}{isCurrent}  {daemon}");
			}
		}
		else
		{
			Console.WriteLine(elevated
				? "  Installed    no"
				: "  Installed    unknown (run elevated for filesystem details)");
		}

		// Prefix search so a SYSTEM daemon task from any version shows up. Tasks
		// registered with a SYSTEM principal aren't readable without elevation,
		// so absence only means "not registered" when we could actually look.
		var taskPath = TaskQuery.FindByPrefix("SeaShell Daemon (SYSTEM)");
		if (taskPath != null)
		{
			Console.WriteLine($"  daemon task  {TaskQuery.QueryState(taskPath) ?? "unknown"} ({taskPath[(taskPath.LastIndexOf('\\') + 1)..]})");
			var taskVersion = TaskQuery.ParseVersionSuffix(taskPath);
			if (StatusRules.TaskVersionDrifts(current, taskVersion))
				Console.WriteLine($"  WARNING      current is v{current}, but the SYSTEM daemon task is v{taskVersion}");
		}
		else
			Console.WriteLine(elevated ? "  daemon task  not registered" : "  daemon task  unknown (run elevated)");

		// Live daemon: prefer a real ping via the pipe marker (works elevated on
		// 0.6.0+ daemons thanks to the Administrators ACE), fall back to raw pipe
		// probing of every non-self endpoint.
		var pinged = false;
		if (elevated && current != null)
		{
			var pipe = MarkerFile.Read(SeaShellPaths.VersionDirIn(sys.DataDir, current.ToString()), MarkerFile.DaemonPipe);
			if (!string.IsNullOrEmpty(pipe))
			{
				var ping = await DaemonIpc.PingAsync(pipe);
				if (ping != null)
				{
					Console.WriteLine($"  daemon       v{ping.Version}, pid {ping.Pid}, up {TimeSpan.FromSeconds(ping.UptimeSeconds):g}, {ping.ActiveScripts} active");
					pinged = true;
				}
			}
		}

		if (!pinged)
		{
			var self = SeaShellIdentity.Current;
			var found = 0;
			var anyAclBlocked = false;
			foreach (var ep in TransportEndpoint.EnumerateAllSeashellEndpoints())
			{
				if (self.Matches(ep.Identity)) continue;
				var state = EnvironmentProbe.ProbeSystemPipe(ep.Address, true);
				if (state.Contains("ACL", StringComparison.Ordinal)) anyAclBlocked = true;
				var kind = ep.IsElevator ? "elevator" : "daemon";
				Console.WriteLine($"  {kind,-12} v{ep.Version} ({ep.Identity}) — {state}");
				found++;
			}
			if (found == 0) Console.WriteLine("  daemon       not running");
			if (anyAclBlocked) Console.WriteLine("  (re-run under gsudo for full details)");
		}

		return 0;
	}
}