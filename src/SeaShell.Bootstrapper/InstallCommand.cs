using System;
using System.IO;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// <c>seashell install</c> (and the install half of <c>update</c>) for the
/// invoking user: resolve the effective scope (<see cref="ScopeResolver"/>),
/// run the passes it needs — elevating once on Windows, running the root pass
/// through sudo on Linux — and record what was actually installed in
/// <c>install.scope</c> (<see cref="ScopeRecording"/>).
///
/// <c>--system</c> does not come here (a single SYSTEM pass, never touching
/// the user's scope); the Linux root install is a single ambient pass.
/// </summary>
internal sealed class InstallCommand
{
	private readonly InstallTarget _target;
	private readonly BootstrapperOptions _options;

	/// <param name="target">The ambient identity's target (the invoking user, or root on Linux).</param>
	public InstallCommand(InstallTarget target, BootstrapperOptions options)
	{
		_target = target;
		_options = options;
	}

	private InstallPassOptions PassOptions(bool registerElevator) =>
		new(registerElevator, _options.IfNeeded, _options.KeepGlobalTool);

	public async Task<int> RunAsync()
	{
		var isWindows = _target.IsWindows;
		if (!isWindows && Elevation.IsElevatedOrRoot(false))
			return await RootPassAsync();

		var elevated = Elevation.IsElevated(isWindows);
		var dataDir = SeaShellIdentity.Current.DataDir;
		var recorded = InstallScopeFile.Read(dataDir);
		var resolution = ScopeResolver.Resolve(_options.ScopeRequest, recorded, () => ScopeProbe.Detect(isWindows), isWindows);
		var knownBefore = recorded.State == ScopeFileState.Read ? recorded.Parts
			: resolution.Source == ScopeSource.Detected ? resolution.Parts
			: ScopeParts.None;

		var summary = new InstallRunSummary();

		// We are the elevated helper and the invoker could not see these parts:
		// look now, and install the ones that exist.
		if (_options.Detect != ScopeParts.None)
		{
			var detection = ScopeProbe.Detect(isWindows);
			// The elevator is a per-account part: as a different account than the
			// invoker this process would be looking at the wrong profile.
			if (InstallAllPlanner.ShouldSkipUserParts(_options.InvokerSid, SeaShellIdentity.Current.Token))
				detection = detection with { Elevator = PartPresence.Unknown };
			summary.Detection = detection;
			var found = _options.Detect & detection.Present;
			Console.WriteLine($"  Detected (elevated): {ScopeTokens.Describe(found)} of {ScopeTokens.Describe(_options.Detect)}");
			resolution = resolution with { Parts = resolution.Parts | found };
		}

		PrintScopeNotes(resolution, recorded, elevated);

		resolution = isWindows
			? await RunWindowsAsync(resolution, elevated, summary)
			: await RunLinuxAsync(resolution, summary);

		Finish(resolution, summary, knownBefore, recorded, dataDir);
		return summary.ExitCode;
	}

	// ── Windows ────────────────────────────────────────────────────────

	private async Task<ScopeResolution> RunWindowsAsync(ScopeResolution resolution, bool elevated, InstallRunSummary summary)
	{
		var plan = InstallAllPlanner.Plan(
			resolution.Parts,
			elevated,
			_options.InvokerSid,
			SeaShellIdentity.Current.Token,
			opportunisticElevator: _options.UsesRememberedScope);

		if (plan.RelaunchElevated)
		{
			if (resolution.Unresolved == ScopeParts.None && SettledWithoutElevation(resolution.Parts, summary))
				return resolution;

			var relaunch = await PassRelaunch.ElevatedAsync(
				resolution.Parts, resolution.Unresolved, _options.IfNeeded, _options.KeepGlobalTool);
			if (relaunch.Summary == null)
			{
				summary.Add(resolution.Parts, PassResult.Failed);
				return resolution;
			}

			summary.Merge(relaunch.Summary);
			if (relaunch.Summary.Detection is { } detection)
				resolution = ScopeResolver.WithResolved(resolution, detection);

			if (InstallAllPlanner.RunUserPassAfterRelaunch(resolution.Parts, relaunch.Summary))
			{
				Console.WriteLine();
				Console.WriteLine("  The elevated helper ran as a different account and installed only SYSTEM;");
				Console.WriteLine($"  installing the user part for {_target.DisplayName} here, without elevation.");
				if ((resolution.Parts & ScopeParts.Elevator) != 0)
				{
					Console.WriteLine("  NOTE: no elevator. It runs with this account's own elevated token, which a");
					Console.WriteLine("        standard user does not have; the recorded scope leaves it out.");
				}
				var outcome = await new InstallRunner(_target, _options, PassOptions(registerElevator: false)).RunAsync();
				AddUserPass(summary, outcome, registerElevator: false);
			}

			// A helper that failed without saying which part failed.
			if (relaunch.ExitCode != 0 && summary.Failed == ScopeParts.None)
				summary.Add(resolution.Parts & ~summary.Completed, PassResult.Failed);
			return resolution;
		}

		if (plan.SkipUserParts)
		{
			summary.UserSkipped = true;
			Console.WriteLine($"  NOTE: elevated as a different account than the invoker ({_options.InvokerSid}).");
			Console.WriteLine("        Installing SYSTEM only; the invoking console installs the user part itself.");
		}

		foreach (var pass in plan.Passes)
		{
			if (pass == InstallPassScope.System)
			{
				// The SYSTEM pass is always announced by a blank separator line.
				Console.WriteLine();
				var system = await new InstallRunner(
					_target.WithIdentity(SeaShellIdentity.LocalSystem), _options, PassOptions(registerElevator: false)).RunAsync();
				summary.Add(ScopeParts.System, system.Result);
				continue;
			}

			var userTarget = _target.WithIdentity(SeaShellIdentity.Current);
			var outcome = await new InstallRunner(userTarget, _options, PassOptions(plan.RegisterElevator)).RunAsync();
			AddUserPass(summary, outcome, plan.RegisterElevator);
			if (outcome.Result == PassResult.Failed) break; // as before: no SYSTEM pass after a failed user pass

			if (outcome.Result == PassResult.Installed && outcome.ElevatorRegistered
				&& InstallAllPlanner.ShouldStartUserServiceChain(outcome.SkippedNewerVersion))
				await StartUserServiceChainAsync(userTarget);
		}
		return resolution;
	}

	/// <summary>
	/// Before asking for UAC: when every target visibly needs nothing — already
	/// at this version under <c>--if-needed</c>, or on a newer version that the
	/// pass would skip anyway — no prompt is needed and nothing is changed.
	/// What cannot be seen without elevation counts as needing work; the
	/// elevated helper then decides per target under its lock. True when the
	/// run is settled (the results are in <paramref name="summary"/>).
	/// </summary>
	private bool SettledWithoutElevation(ScopeParts parts, InstallRunSummary summary)
	{
		var manifest = EmbeddedArchive.Manifest(EnvironmentProbe.CurrentRid(true));
		var userParts = parts & (ScopeParts.User | ScopeParts.Elevator);
		var userFacts = ScopeProbe.ReadFacts(SeaShellIdentity.Current.DataDir, true, manifest);
		var checks = new System.Collections.Generic.List<(ScopeParts Parts, string Name, TargetPrecheck Result, Version? Active)>();
		if (userParts != ScopeParts.None)
			checks.Add((userParts, _target.DisplayName,
				UpdateDecision.Precheck(InstallPassScope.User, parts & ScopeParts.Elevator, userFacts, _target.Version, true, _options.IfNeeded),
				userFacts.ActiveVersion));
		if ((parts & ScopeParts.System) != 0)
		{
			var systemFacts = ScopeProbe.SystemFactsWithoutElevation();
			checks.Add((ScopeParts.System, "SYSTEM",
				UpdateDecision.Precheck(InstallPassScope.System, ScopeParts.None, systemFacts, _target.Version, true, _options.IfNeeded),
				systemFacts.ActiveVersion));
		}
		if (checks.Count == 0 || checks.Exists(c => c.Result == TargetPrecheck.NeedsWork)) return false;

		foreach (var (checkParts, name, result, active) in checks)
		{
			if (result == TargetPrecheck.Satisfied)
			{
				Console.WriteLine($"  Already at v{_target.Version}: {name} ({ScopeTokens.Describe(checkParts)}).");
				summary.Add(checkParts, PassResult.Satisfied);
			}
			else
			{
				Console.WriteLine($"  Newer version already active for {name}: v{active}; skipping v{_target.Version}.");
				summary.Add(checkParts, PassResult.SkippedNewer);
			}
		}
		Console.WriteLine("  No elevation needed; nothing was changed.");
		return true;
	}

	// We're elevated and both tasks are registered — bring the whole chain up
	// now: daemon first, then the elevator (it connects TO the daemon,
	// retrying every 3s, so order only affects latency).
	private static async Task StartUserServiceChainAsync(InstallTarget userTarget)
	{
		Console.WriteLine();
		Console.WriteLine("  Starting daemon...");
		if (!DaemonTasks.TryRunDaemonTask())
			Console.WriteLine("  WARNING: could not start the daemon task");
		else if (await new InstallationVerifier(userTarget).DaemonLiveAsync("daemon"))
		{
			Console.WriteLine("  Starting elevator...");
			if (!DaemonTasks.TryRunElevatorTask())
				Console.WriteLine("  WARNING: could not start the elevator task");
			else
				await VerifyElevatorRegisteredAsync(userTarget);
		}
	}

	// After starting the elevator task: the elevator connects to the daemon and
	// registers; the daemon then reports its version in PingResponse — one check
	// proving daemon up + elevator up + handshake complete.
	private static async Task<bool> VerifyElevatorRegisteredAsync(InstallTarget target)
	{
		for (var i = 0; i < 30; i++)
		{
			var pipe = MarkerFile.Read(target.VersionDir, MarkerFile.DaemonPipe);
			if (!string.IsNullOrEmpty(pipe))
			{
				var ping = await DaemonIpc.PingAsync(pipe, timeoutMs: 1000);
				if (ping?.ElevatorVersion != null)
				{
					Console.WriteLine($"    elevator → OK (v{ping.ElevatorVersion}, registered with daemon)");
					return true;
				}
			}
			await Task.Delay(500);
		}
		Console.WriteLine("    elevator → WARNING (did not register with the daemon within 15s)");
		return false;
	}

	// ── Linux ──────────────────────────────────────────────────────────

	private async Task<ScopeResolution> RunLinuxAsync(ScopeResolution resolution, InstallRunSummary summary)
	{
		var plan = InstallAllPlanner.PlanLinux(resolution.Parts, isRoot: false);

		if (plan.UserPass)
		{
			var outcome = await new InstallRunner(_target, _options, PassOptions(registerElevator: false)).RunAsync();
			summary.Add(ScopeParts.User, outcome.Result);
			if (outcome.Result == PassResult.Failed) return resolution;
		}

		if (plan.SudoSystemPass)
		{
			// Without a prompt when the root install is visibly at this version.
			if (_options.IfNeeded
				&& UpdateDecision.Decide(InstallPassScope.System, ScopeParts.None,
					ScopeProbe.ReadFacts(ScopeProbe.LinuxRootDataDir, false, EmbeddedArchive.Manifest(EnvironmentProbe.CurrentRid(false))),
					_target.Version, false) == UpdateTargetAction.Skip)
			{
				Console.WriteLine();
				Console.WriteLine($"  Already at v{_target.Version} for root ({ScopeProbe.LinuxRootDataDir}); no sudo needed.");
				summary.Add(ScopeParts.System, PassResult.Satisfied);
				return resolution;
			}

			var relaunch = PassRelaunch.Sudo(_options.IfNeeded, _options.KeepGlobalTool);
			if (relaunch.Summary != null) summary.Merge(relaunch.Summary);
			else summary.Add(ScopeParts.System, PassResult.Failed);
			if (relaunch.ExitCode != 0 && (summary.Failed & ScopeParts.System) == 0)
				summary.Add(ScopeParts.System, PassResult.Failed);
		}
		return resolution;
	}

	/// <summary>
	/// Invoked as root on Linux: root's own install (<c>/var/lib/seashell</c>)
	/// is the only pass. The sudo pass of a user's <c>install --all</c> ends up
	/// here too (with <c>--pass system</c>).
	/// </summary>
	private async Task<int> RootPassAsync()
	{
		if (_options.Pass == null)
		{
			var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
			Console.WriteLine($"  NOTE: running as root installs root's own SeaShell ({ScopeProbe.LinuxRootDataDir}) only;");
			Console.WriteLine($"        the install of {(string.IsNullOrEmpty(sudoUser) ? "the invoking user" : sudoUser)} is not touched. To update both, run");
			Console.WriteLine("        'seashell install --all' as that user (it runs this root pass through sudo).");
			Console.WriteLine();
		}

		var outcome = await new InstallRunner(_target, _options, PassOptions(registerElevator: false)).RunAsync();
		var summary = new InstallRunSummary();
		summary.Add(ScopeParts.System, outcome.Result);
		WriteResultFile(summary);
		if (_options.IfNeeded && _options.ResultFile == null && summary.AllSatisfied(ScopeParts.System))
			PrintUpToDate();
		return outcome.ExitCode;
	}

	// ── Shared ─────────────────────────────────────────────────────────

	private static void AddUserPass(InstallRunSummary summary, InstallOutcome outcome, bool registerElevator)
	{
		if (outcome.Result == PassResult.Installed)
		{
			summary.Add(ScopeParts.User, PassResult.Installed);
			if (outcome.ElevatorRegistered) summary.Add(ScopeParts.Elevator, PassResult.Installed);
			else if (registerElevator) summary.Add(ScopeParts.Elevator, PassResult.Failed);
			return;
		}
		summary.Add(ScopeParts.User | (registerElevator ? ScopeParts.Elevator : ScopeParts.None), outcome.Result);
	}

	private void PrintScopeNotes(ScopeResolution resolution, ScopeFileRead recorded, bool elevated)
	{
		if (recorded.State == ScopeFileState.Unreadable)
		{
			Console.WriteLine($"  WARNING: could not read {InstallScopeFile.PathIn(SeaShellIdentity.Current.DataDir)} ({recorded.Error});");
			Console.WriteLine("           detecting the scope instead and leaving the file as it is.");
		}

		if (resolution.LeftBehind != ScopeParts.None)
		{
			Console.WriteLine("  NOTE: --user records a user-only scope. Left in place, and no longer updated:");
			if ((resolution.LeftBehind & ScopeParts.Elevator) != 0)
				Console.WriteLine($"    elevator  remove from an elevated prompt: schtasks /Delete /TN \"{CurrentElevatorTaskPath()}\" /F");
			if ((resolution.LeftBehind & ScopeParts.System) != 0)
				Console.WriteLine(_target.IsWindows
					? "    system    remove from an elevated prompt: seashell uninstall --system"
					: "    root      remove with: sudo env \"PATH=$PATH\" seashell uninstall");
			Console.WriteLine();
		}

		// Uncertain and no elevation otherwise needed: behave as 0.6.2 did.
		var relaunching = _target.IsWindows && !elevated && InstallAllPlanner.NeedsElevation(resolution.Parts);
		if (resolution.Unresolved != ScopeParts.None && !relaunching)
		{
			Console.WriteLine($"  NOTE: an existing {ScopeTokens.Describe(resolution.Unresolved)} install could not be ruled out (not visible from here);");
			Console.WriteLine("        this run updates only what it can see. 'seashell install --all' installs everything and records the full scope.");
			Console.WriteLine();
		}
	}

	private string CurrentElevatorTaskPath()
	{
		var dataDir = SeaShellIdentity.Current.DataDir;
		var current = VersionDiscovery.GetCurrentVersion(dataDir);
		var fromMarker = current == null ? null
			: MarkerFile.Read(SeaShellPaths.VersionDirIn(dataDir, current.ToString()), MarkerFile.ElevatorTask);
		return fromMarker ?? $"{TaskQuery.Folder}{SeaShellTaskNames.ElevatorPrefix}({_target.DisplayName}) {current?.ToString() ?? "<version>"}";
	}

	private void Finish(ScopeResolution resolution, InstallRunSummary summary, ScopeParts knownBefore, ScopeFileRead recorded, string dataDir)
	{
		WriteResultFile(summary);

		if (ScopeRecording.PartsToRecord(resolution, summary, knownBefore, recorded) is { } toRecord)
		{
			try
			{
				InstallScopeFile.Write(dataDir, toRecord);
				Console.WriteLine();
				Console.WriteLine($"  Scope recorded: {ScopeTokens.Describe(toRecord)} ({InstallScopeFile.PathIn(dataDir)})");
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"  WARNING: could not record the install scope in {InstallScopeFile.PathIn(dataDir)}: {ex.Message}");
			}
		}

		if (_options.IfNeeded && _options.ResultFile == null && summary.AllSatisfied(resolution.Parts))
			PrintUpToDate();
	}

	private void WriteResultFile(InstallRunSummary summary)
	{
		if (_options.ResultFile == null) return;
		try { File.WriteAllText(_options.ResultFile, summary.Serialize()); }
		catch (Exception ex) { Console.Error.WriteLine($"  WARNING: could not write the result for the invoking process to {_options.ResultFile}: {ex.Message}"); }
	}

	private void PrintUpToDate()
	{
		Console.WriteLine();
		Console.WriteLine($"  {{~}} Up to date (v{_target.Version})");
	}
}
