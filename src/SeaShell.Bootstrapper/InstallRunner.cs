using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// One install pass's outcome: the exit code it contributes, how the pass
/// ended (installed, already satisfied, skipped because a newer version is
/// active, failed), and whether it registered the elevator. The caller needs
/// these before starting the daemon/elevator chain and when recording the
/// scope — passed back explicitly instead of living in captured flags.
/// </summary>
internal readonly record struct InstallOutcome(int ExitCode, PassResult Result, bool ElevatorRegistered = false)
{
	public bool SkippedNewerVersion => Result == PassResult.SkippedNewer;

	public static InstallOutcome Failed => new(1, PassResult.Failed);
}

/// <summary>What one pass is asked to do beyond the target's own step matrix.</summary>
/// <param name="RegisterElevator">User pass: register the elevator task (the process is elevated).</param>
/// <param name="IfNeeded">Skip the target when it already has this version and every requested part.</param>
/// <param name="KeepGlobalTool">The run that started this one executes from the global tool.</param>
internal sealed record InstallPassOptions(bool RegisterElevator, bool IfNeeded, bool KeepGlobalTool);

/// <summary>
/// The install flow for a single target: gate on version, stop what is
/// running, extract the embedded archive, activate the junction, wire up
/// PATH/tasks/associations, then verify end to end.
/// </summary>
internal sealed class InstallRunner
{
	private readonly InstallTarget _target;
	private readonly BootstrapperOptions _options;
	private readonly InstallPassOptions _pass;

	public InstallRunner(InstallTarget target, BootstrapperOptions options, InstallPassOptions pass)
	{
		_target = target;
		_options = options;
		_pass = pass;
	}

	public async Task<InstallOutcome> RunAsync()
	{
		if (!ElevationPolicy.IsSatisfied(_options.SystemMode, Elevation.IsElevatedOrRoot(_target.IsWindows)))
		{
			Console.Error.WriteLine("  ERROR: --system requires elevation (gsudo on Windows, sudo on Linux).");
			return InstallOutcome.Failed;
		}

		// Lock is scoped to the TARGET identity — two elevated admins racing a
		// --system install contend on the same lock.
		using var installLock = InstallLock.TryAcquire(_target.Identity, TimeSpan.FromSeconds(30),
			() => Console.WriteLine("  Waiting for another install, uninstall or removal to finish..."));
		if (installLock == null)
		{
			Console.Error.WriteLine("seashell: another install or uninstall is in progress");
			return InstallOutcome.Failed;
		}

		Console.WriteLine($"{{~}} SeaShell Installer v{_target.Version}{(_options.SystemMode ? " (system)" : "")}");
		Console.WriteLine();

		// 1. Platform info
		Console.WriteLine($"  Platform:  {EnvironmentProbe.CurrentRid(_target.IsWindows)}");
		Console.WriteLine($"  Target:    {_target.DisplayName}");
		Console.WriteLine($"  Version:   {_target.VersionDir}");

		// A stale bootstrapper must not disturb a newer active installation. In
		// particular, stopping its daemon and re-registering this version's task
		// would leave `current` on the newer version while booting the older one.
		// This is the first thing after the target lock is acquired and before
		// anything below touches the filesystem, Task Scheduler, or a process.
		var gate = InstallGate.Evaluate(
			VersionDiscovery.GetCurrentVersion(_target.DataDir), _target.Version);
		if (gate.Outcome == InstallGateOutcome.SkipNewerVersionActive)
		{
			Console.WriteLine();
			Console.WriteLine($"  Newer version already active: v{gate.ActiveVersion}");
			Console.WriteLine($"  Skipping v{_target.Version} for {_target.DisplayName}; no binaries, tasks, or running processes were changed.");
			Console.WriteLine($"  Use a v{gate.ActiveVersion} or newer bootstrapper to update or repair this target.");
			return new InstallOutcome(0, PassResult.SkippedNewer);
		}

		// Update mode: decided here, under this target's lock, from what is on
		// the target now — another install may have finished in the meantime.
		if (_pass.IfNeeded)
		{
			var parts = _pass.RegisterElevator ? ScopeParts.Elevator : ScopeParts.None;
			var facts = ScopeProbe.ReadFacts(_target.DataDir, _target.IsWindows,
				EmbeddedArchive.Manifest(EnvironmentProbe.CurrentRid(_target.IsWindows)));
			var passScope = _target.IsSystemTarget ? InstallPassScope.System : InstallPassScope.User;
			if (UpdateDecision.Decide(passScope, parts, facts, _target.Version, _target.IsWindows) == UpdateTargetAction.Skip)
			{
				Console.WriteLine();
				Console.WriteLine($"  Already at v{_target.Version} with every requested part; nothing to do for {_target.DisplayName}.");
				return new InstallOutcome(0, PassResult.Satisfied);
			}
		}

		// 2. Stop running daemons/elevators and wipe any pre-0.5.0 flat-layout state.
		//    Running daemons first so rename-then-delete in the sweep below isn't
		//    blocked by held handles on Windows.
		await InstanceStopper.StopAsync(_target);
		LegacyCleanup.SweepFilesystem(_target.DataDir, msg => Console.WriteLine($"  {msg}"));
		LegacyCleanup.SweepStaleTempDir(msg => Console.WriteLine($"  {msg}"));
		if (_target.Scope.SweepLegacySystemArtifacts)
		{
			// Pre-0.6.0 --system installs ran `dotnet tool install` under SYSTEM's
			// profile via a transient task; remove that footprint.
			LegacyCleanup.SweepLegacySystemArtifacts(msg => Console.WriteLine($"  {msg}"));
		}
		else if (_target.Scope.StripLegacyBinFromUserPath)
			PathEnvironment.StripLegacyBinEntries(_target);
		if (_target.Scope.CleanLegacyCommonInstall)
			LegacyCommonInstallCleaner.Clean(_target.IsWindows);

		// 3. Create version directory structure
		Directory.CreateDirectory(_target.BinDir);
		Directory.CreateDirectory(_target.CacheDir);
		Console.WriteLine("  Created version directory");

		// 4. Extract the matching embedded archive into bin/
		if (!EmbeddedArchive.ExtractTo(EnvironmentProbe.CurrentRid(_target.IsWindows), _target.BinDir))
			return InstallOutcome.Failed;

		// 5. Set execute bits on Linux (zip extraction does not carry them).
		if (!OperatingSystem.IsWindows())
		{
			foreach (var name in new[] { "sea", "seashell", "seashell-daemon", "seashell-elevator" })
			{
				var path = Path.Combine(_target.BinDir, name);
				if (File.Exists(path))
				{
					// The root install in /var/lib/seashell is root's own and is not
					// shared with other users; group/other bits only mirror the usual
					// 0755 of an installed executable.
					try
					{
						File.SetUnixFileMode(path, File.GetUnixFileMode(path)
							| UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
					}
					catch { }
				}
			}
		}

		// 6. Write marker files
		var daemonExe = Path.Combine(_target.BinDir, _target.IsWindows ? "seashell-daemon.exe" : "seashell-daemon");
		var daemonDll = Path.Combine(_target.BinDir, "seashell-daemon.dll");
		if (File.Exists(daemonExe))
			MarkerFile.Write(_target.VersionDir, MarkerFile.DaemonPath, daemonExe);
		else if (File.Exists(daemonDll))
			MarkerFile.Write(_target.VersionDir, MarkerFile.DaemonPath, daemonDll);
		Console.WriteLine($"  daemon.path: {(MarkerFile.Exists(_target.VersionDir, MarkerFile.DaemonPath) ? "ok" : "not found")}");

		var elevatorExe = Path.Combine(_target.BinDir, _target.IsWindows ? "seashell-elevator.exe" : "seashell-elevator");
		var elevatorDll = Path.Combine(_target.BinDir, "seashell-elevator.dll");
		if (File.Exists(elevatorExe))
			MarkerFile.Write(_target.VersionDir, MarkerFile.ElevatorPath, elevatorExe);
		else if (File.Exists(elevatorDll))
			MarkerFile.Write(_target.VersionDir, MarkerFile.ElevatorPath, elevatorDll);
		Console.WriteLine($"  elevator.path: {(MarkerFile.Exists(_target.VersionDir, MarkerFile.ElevatorPath) ? "ok" : "not found")}");

		// 7. Activate this version. A newer active version returned early above,
		//    so the junction, registered tasks, and verification all agree.
		JunctionHelper.CreateOrUpdate(_target.CurrentDir, _target.VersionDir);
		Console.WriteLine($"  current -> v{_target.Version}");

		// 8. Add to PATH (User environment, using junction path). Skipped for the
		//    SYSTEM target — that would edit the invoking admin's HKCU, not SYSTEM's,
		//    and SYSTEM consumers find binaries via markers, not PATH.
		if (_target.Scope.EditUserProfilePath)
		{
			PathEnvironment.Add(_target);
			// The installed seashell supersedes an older global tool, which would
			// otherwise answer `seashell update` with its old alias.
			GlobalTool.HandleOlderThan(_target.Version, _pass.KeepGlobalTool);
		}

		// 8b. Refresh baked-in paths in user-scheduled tasks / crontab entries so
		//     schedules created under a prior layout keep pointing at the current
		//     install. Skip for SYSTEM — it doesn't own user-scoped schedules.
		if (_target.Scope.RefreshScheduledTaskPaths)
		{
			if (_target.IsWindows)
				TaskPathRefresher.RefreshWindows(msg => Console.WriteLine($"  {msg}"));
			else
				TaskPathRefresher.RefreshLinux(msg => Console.WriteLine($"  {msg}"));
		}

		var seaPath = Path.Combine(_target.BinDir, _target.IsWindows ? "sea.exe" : "sea");
		if (!File.Exists(seaPath))
		{
			Console.Error.WriteLine("  ERROR: sea not found after extraction.");
			return InstallOutcome.Failed;
		}

		Action<string> taskLog = msg => Console.WriteLine($"  {msg}");
		var elevatorRegistered = false;

		// 9. Register scheduled tasks (Windows only)
		if (_target.Scope.RegisterSystemDaemon)
		{
			Console.WriteLine("  Registering daemon (SYSTEM, boot trigger)...");
			DaemonTasks.InstallSystemDaemon(_target.Identity, taskLog);

			RegisterEventLogSource();

			// Boot trigger only covers the next reboot — start the daemon now so
			// the install is immediately usable (and verifiable below).
			Console.WriteLine(DaemonTasks.TryRunSystemDaemonTask()
				? "  Starting daemon (SYSTEM)..."
				: "  WARNING: could not start the SYSTEM daemon task");
		}
		else if (_target.Scope.RegisterUserTasks)
		{
			Console.WriteLine("  Registering daemon...");
			DaemonTasks.InstallDaemon(_target.Identity, taskLog);

			if (_pass.RegisterElevator)
			{
				Console.WriteLine("  Registering elevator (elevated)...");
				elevatorRegistered = DaemonTasks.InstallElevator(_target.Identity, taskLog) == 0;
				RegisterEventLogSource();
			}
			else if (!Elevation.IsElevated(true))
			{
				Console.WriteLine("  Elevator skipped (not elevated). To register it (one UAC prompt):");
				Console.WriteLine("    seashell install --all");
			}
			else
				Console.WriteLine("  Elevator skipped (not in the requested scope).");
		}

		// 10. File associations — user-context only. SYSTEM's HKCU isn't useful.
		if (_target.Scope.EditFileAssociations)
		{
			Console.WriteLine("  Registering file associations...");
			FileAssoc.Associate(".cs", seaPath, taskLog);
			var seawPath = Path.Combine(_target.BinDir, "seaw.exe");
			if (File.Exists(seawPath))
				FileAssoc.Associate(".csw", seawPath, taskLog);
		}

		// 11. Clean up old versions
		await new OldVersionCleaner(_target).CleanAsync();

		// 12. End-to-end verification.
		Console.WriteLine();
		if (!await new InstallationVerifier(_target).VerifyAsync(seaPath, _pass.RegisterElevator))
			return InstallOutcome.Failed with { ElevatorRegistered = elevatorRegistered };

		Console.WriteLine();
		Console.WriteLine("  {~} Done! Run 'sea' to start.");
		return new InstallOutcome(0, PassResult.Installed, elevatorRegistered);
	}

	/// <summary>Event Log source registration — needs elevation on Windows.</summary>
	private static void RegisterEventLogSource()
	{
		if (!OperatingSystem.IsWindows()) return;
		try
		{
			if (!EventLog.SourceExists("SeaShell"))
			{
				EventLog.CreateEventSource("SeaShell", "Application");
				Console.WriteLine("  Registered Event Log source: SeaShell");
			}
		}
		catch { }
	}
}