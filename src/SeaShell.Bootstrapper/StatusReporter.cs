using System;
using System.IO;
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
		var carries = EmbeddedArchive.CarriesCurrentRid(_target.IsWindows);

		string? installedVersion = null;
		if (installed)
		{
			var verOutput = EnvironmentProbe.RunProcess(seaPath!, "--version").Trim();
			installedVersion = StatusRules.ParseInstalledVersion(verOutput);

			Console.WriteLine($"  Installed    v{installedVersion}");
			Console.WriteLine($"  Current      {SeaShellPaths.CurrentDir} -> v{currentVersion}");
			Console.WriteLine($"  Platform     {EnvironmentProbe.CurrentRid(_target.IsWindows)}");
			if (!_options.SystemMode) PrintScopeRows(currentVersion);
		}
		else
		{
			Console.WriteLine("  Installed    no");
			if (carries) Console.WriteLine($"  Bundled      v{_target.Version} ready to install");
		}

		Console.WriteLine($"  Bootstrapper v{_target.Version}{(carries ? "" : " (carries no archive; install = update)")}");
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
					if (trimmed.Length > 0 && !StatusRules.IsUpdateNoticeRow(trimmed)) Console.WriteLine($"  {trimmed}");
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

		var check = installed ? UpdateCheck.Read(SeaShellIdentity.Current.DataDir) : null;
		foreach (var line in StatusRules.ClosingLines(installed, installedVersion, _target.Version, carries, check))
			Console.WriteLine($"  {line}");

		await Task.CompletedTask;
		return 0;
	}

	/// <summary>
	/// The remembered (or detected) scope, and a <c>system</c> row whenever the
	/// scope includes the SYSTEM / root install — with a warning when its
	/// version differs from the user's, as far as it is visible from here.
	/// </summary>
	private void PrintScopeRows(Version? userVersion)
	{
		var isWindows = _target.IsWindows;
		var recorded = InstallScopeFile.Read(SeaShellIdentity.Current.DataDir);
		var resolution = ScopeResolver.Resolve(new ScopeRequest(), recorded, () => ScopeProbe.Detect(isWindows), isWindows);
		var source = resolution.Source == ScopeSource.Recorded ? "recorded" : "detected";
		var unresolved = resolution.Unresolved != ScopeParts.None
			? $"; {ScopeTokens.Describe(resolution.Unresolved)} not ruled out"
			: "";
		Console.WriteLine($"  Scope        {ScopeTokens.Describe(resolution.Parts)} ({source}{unresolved})");
		Console.WriteLine($"  Auto-update  {AutoUpdateState(resolution.Parts, isWindows)}");
		if (LastScheduledFailure(SeaShellIdentity.Current.DataDir) is { } failure)
			Console.WriteLine($"  WARNING      {failure}");

		if ((resolution.Parts & ScopeParts.System) == 0) return;
		var systemDataDir = isWindows ? SeaShellIdentity.LocalSystem.DataDir : ScopeProbe.LinuxRootDataDir;
		if (LastScheduledFailure(systemDataDir) is { } systemFailure)   // readable elevated (Windows) / when root's log is
			Console.WriteLine($"  WARNING      {(isWindows ? "SYSTEM" : "root")}: {systemFailure}");

		var systemVersion = VisibleSystemVersion(isWindows);
		var label = isWindows ? "System" : "Root";
		Console.WriteLine(systemVersion != null
			? $"  {label,-12} v{systemVersion}"
			: $"  {label,-12} installed (version not visible without elevation)");
		if (StatusRules.TaskVersionDrifts(userVersion, systemVersion))
			Console.WriteLine($"  WARNING      {label} is on v{systemVersion}, the user install on v{userVersion}");
	}

	/// <summary>
	/// When the last scheduled update recorded in <c>{dataDir}/update.log</c>
	/// failed: when, and where its complete output is. Null otherwise, or when
	/// the log cannot be read from here.
	/// </summary>
	private static string? LastScheduledFailure(string dataDir)
	{
		var path = UpdateLog.PathIn(dataDir);
		try
		{
			if (!File.Exists(path) || UpdateLog.LastRun(File.ReadAllText(path)) is not { } run) return null;
			return run.ExitCode != 0
				? $"the last scheduled update failed ({run.Time:yyyy-MM-ddTHH:mm:sszzz}, exit {run.ExitCode}); output in {path}"
				: null;
		}
		catch { return null; }
	}

	// Presence of the scheduled entries (see AutoUpdatePlan). SYSTEM's task and
	// root's crontab are not visible without elevation; say so rather than "off".
	private static string AutoUpdateState(ScopeParts scope, bool isWindows)
	{
		static string Word(PartPresence p) => p switch
		{
			PartPresence.Present => "on",
			PartPresence.Absent => "off",
			_ => "unknown",
		};

		string own;
		if (isWindows)
			own = Word(TaskQuery.Probe(SeaShellTaskNames.UpdateTaskPath(SeaShellIdentity.Current.DisplayName)));
		else
			own = Crontab.Read() is { } crontab ? (crontab.Exists(AutoUpdatePlan.IsCronLine) ? "on" : "off") : "unknown";

		var text = own == "off" ? "off (enable: seashell update --auto on)" : own;
		if ((scope & ScopeParts.System) == 0) return text;

		if (isWindows && Elevation.IsElevated(true))
			return $"{text}; SYSTEM: {Word(TaskQuery.Probe(SeaShellTaskNames.UpdateTaskPath("SYSTEM")))}";
		return $"{text}; {(isWindows ? "SYSTEM" : "root")}: not visible without elevation";
	}

	// Elevated: the SYSTEM current junction. Otherwise what a non-elevated
	// user can see: the version in the SYSTEM daemon task's name, or of a live
	// SYSTEM daemon endpoint. Linux: /var/lib/seashell/current.
	private static Version? VisibleSystemVersion(bool isWindows)
	{
		if (!isWindows) return VersionDiscovery.GetCurrentVersion(ScopeProbe.LinuxRootDataDir);
		if (Elevation.IsElevated(true))
		{
			var current = VersionDiscovery.GetCurrentVersion(SeaShellIdentity.LocalSystem.DataDir);
			if (current != null) return current;
		}
		if (ScopeProbe.SystemTaskVersion(out var taskVersion) && taskVersion != null) return taskVersion;
		return ScopeProbe.SystemEndpointVersion();
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
		Console.WriteLine(elevated
			? $"  auto-update  {(TaskQuery.Probe(SeaShellTaskNames.UpdateTaskPath("SYSTEM")) == PartPresence.Present ? "on" : "off")}"
			: "  auto-update  unknown (run elevated)");
		if (LastScheduledFailure(sys.DataDir) is { } failure)
			Console.WriteLine($"  WARNING      {failure}");

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