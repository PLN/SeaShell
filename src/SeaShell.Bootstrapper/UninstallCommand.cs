using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// <c>seashell uninstall</c>: remove the remembered scope with the same
/// sticky rule (<see cref="ScopeResolver"/>), self-elevation, sudo pass and
/// alternate-account handling as install — <c>--user</c> / <c>--system</c>
/// narrow it, <c>--all</c> forces everything. Each target is removed by an
/// <see cref="UninstallRunner"/> pass; whatever could not be removed makes the
/// run "incomplete" with a non-zero exit code.
/// </summary>
internal sealed class UninstallCommand
{
	private readonly InstallTarget _target;
	private readonly BootstrapperOptions _options;
	private readonly string[] _args;

	/// <param name="target">The ambient identity's target.</param>
	/// <param name="args">The original command line (relocation re-runs it).</param>
	public UninstallCommand(InstallTarget target, BootstrapperOptions options, string[] args)
	{
		_target = target;
		_options = options;
		_args = args;
	}

	public async Task<int> RunAsync()
	{
		var scopeError = ScopeResolver.Validate(_options.ScopeRequest);
		if (scopeError != null)
		{
			Console.Error.WriteLine($"seashell: {scopeError}");
			return 1;
		}

		// A running image cannot delete itself on Windows: run from a copy.
		if (_options.RelocatedFrom == null && SelfRelocation.Needed())
			return SelfRelocation.Run(_args);

		var isWindows = _target.IsWindows;
		var outcomes = new List<UninstallOutcome>();

		if (_options.SystemMode)
		{
			outcomes.Add(await PassAsync(isWindows ? _target.WithIdentity(SeaShellIdentity.LocalSystem) : _target));
			if (isWindows && outcomes[0].Complete) DropSystemFromScope();
			return Finish(outcomes, ScopeParts.System, ScopeParts.None, ownVerdict: true);
		}

		if (!isWindows && Elevation.IsElevatedOrRoot(false))
		{
			if (_options.Pass == null)
			{
				Console.WriteLine($"  NOTE: running as root removes root's own SeaShell ({ScopeProbe.LinuxRootDataDir}) only.");
				Console.WriteLine();
			}
			outcomes.Add(await PassAsync(_target));
			WriteResultFile(outcomes, ScopeParts.System, userSkipped: false);
			return Finish(outcomes, ScopeParts.System, ScopeParts.None, ownVerdict: true);
		}

		var recorded = InstallScopeFile.Read(SeaShellIdentity.Current.DataDir);
		var resolution = ScopeResolver.Resolve(_options.ScopeRequest, recorded, () => ScopeProbe.Detect(isWindows), isWindows);
		if (_options.Detect != ScopeParts.None)
		{
			var detection = ScopeProbe.Detect(isWindows);
			var found = _options.Detect & detection.Present;
			Console.WriteLine($"  Detected (elevated): {ScopeTokens.Describe(found)} of {ScopeTokens.Describe(_options.Detect)}");
			resolution = resolution with { Parts = resolution.Parts | found };
		}
		var parts = UninstallPlanner.PartsToRemove(resolution);
		var left = UninstallPlanner.LeftInPlace(resolution, parts);

		return isWindows
			? await WindowsAsync(resolution, parts, left)
			: await LinuxAsync(parts, left);
	}

	// ── Windows ────────────────────────────────────────────────────────

	private async Task<int> WindowsAsync(ScopeResolution resolution, ScopeParts parts, ScopeParts left)
	{
		var elevated = Elevation.IsElevated(true);
		var plan = InstallAllPlanner.Plan(parts, elevated, _options.InvokerSid, SeaShellIdentity.Current.Token, opportunisticElevator: false);

		if (plan.RelaunchElevated)
		{
			Console.WriteLine($"{{~}} SeaShell uninstall: {ScopeTokens.Describe(parts)}");
			var relaunch = await PassRelaunch.ElevatedAsync(resultFile => RelaunchArguments(parts, resolution.Unresolved, resultFile),
				$"uninstall {ScopeTokens.Describe(parts | resolution.Unresolved)}");
			if (relaunch.Summary is not { UserSkipped: true }) return relaunch.ExitCode;

			Console.WriteLine();
			Console.WriteLine($"  The elevated helper ran as a different account and removed only SYSTEM; removing the user install of {_target.DisplayName} here.");
			var outcomes = new List<UninstallOutcome> { await PassAsync(_target.WithIdentity(SeaShellIdentity.Current)) };
			return Finish(outcomes, parts & ~ScopeParts.System, left, ownVerdict: true) | relaunch.ExitCode;
		}

		if (resolution.Unresolved != ScopeParts.None)
		{
			Console.WriteLine($"  NOTE: an existing {ScopeTokens.Describe(resolution.Unresolved)} install could not be ruled out (not visible from here)");
			Console.WriteLine("        and is not removed; 'seashell uninstall --all' removes everything.");
			Console.WriteLine();
		}
		if (plan.SkipUserParts)
		{
			Console.WriteLine($"  NOTE: elevated as a different account than the invoker ({_options.InvokerSid}).");
			Console.WriteLine("        Removing SYSTEM only; the invoking console removes the user install itself.");
		}

		var results = new List<UninstallOutcome>();
		foreach (var pass in plan.Passes)
		{
			if (results.Count > 0) Console.WriteLine();
			results.Add(await PassAsync(pass == InstallPassScope.System
				? _target.WithIdentity(SeaShellIdentity.LocalSystem)
				: _target.WithIdentity(SeaShellIdentity.Current)));
		}
		WriteResultFile(results, parts, plan.SkipUserParts);
		return Finish(results, parts, left, ownVerdict: true);
	}

	private List<string> RelaunchArguments(ScopeParts parts, ScopeParts detect, string resultFile)
	{
		var args = PassRelaunch.PassArguments(parts, detect, ifNeeded: false, keepGlobalTool: false, resultFile, command: "uninstall");
		if (_options.Purge) args.Add("--purge");
		if (_options.RelocatedFrom is { } pid) { args.Add("--relocated-from"); args.Add(pid.ToString()); }
		return args;
	}

	// `uninstall --system` on Windows: the invoking account no longer has a
	// SYSTEM part, so its recorded scope must not bring one back on the next update.
	private static void DropSystemFromScope()
	{
		var dataDir = SeaShellIdentity.Current.DataDir;
		var recorded = InstallScopeFile.Read(dataDir);
		if (recorded.State != ScopeFileState.Read || (recorded.Parts & ScopeParts.System) == 0) return;
		try
		{
			InstallScopeFile.Write(dataDir, recorded.Parts & ~ScopeParts.System);
			Console.WriteLine($"  Scope recorded: {ScopeTokens.Describe(recorded.Parts & ~ScopeParts.System)} ({InstallScopeFile.PathIn(dataDir)})");
		}
		catch (Exception ex) { Console.Error.WriteLine($"  WARNING: could not update {InstallScopeFile.PathIn(dataDir)}: {ex.Message}"); }
	}

	// ── Linux ──────────────────────────────────────────────────────────

	private async Task<int> LinuxAsync(ScopeParts parts, ScopeParts left)
	{
		// Root's pass first: sudo runs this bootstrapper, which the user pass
		// deletes when it is the installed copy.
		var outcomes = new List<UninstallOutcome>();
		foreach (var pass in UninstallPlanner.LinuxPassOrder(parts))
		{
			if (pass == ScopeParts.User)
			{
				if (outcomes.Count > 0) Console.WriteLine();
				outcomes.Add(await PassAsync(_target));
				continue;
			}
			var relaunch = PassRelaunch.Sudo(resultFile =>
			{
				var args = PassRelaunch.PassArguments(ScopeParts.System, ScopeParts.None, false, false, resultFile, command: "uninstall");
				if (_options.Purge) args.Add("--purge");
				return args;
			}, "removal of root's install");
			if (relaunch.ExitCode != 0)
				outcomes.Add(UninstallOutcome.Failed($"root's install ({ScopeProbe.LinuxRootDataDir}): see the output above"));
		}
		return Finish(outcomes, parts, left, ownVerdict: true);
	}

	// ── Shared ─────────────────────────────────────────────────────────

	private Task<UninstallOutcome> PassAsync(InstallTarget target) =>
		new UninstallRunner(target, _options).RunAsync();

	private void WriteResultFile(IReadOnlyList<UninstallOutcome> outcomes, ScopeParts parts, bool userSkipped)
	{
		if (_options.ResultFile == null) return;
		var summary = new InstallRunSummary { UserSkipped = userSkipped };
		summary.Add(parts, outcomes.All(o => o.Complete) ? PassResult.Installed : PassResult.Failed);
		try { File.WriteAllText(_options.ResultFile, summary.Serialize()); }
		catch (Exception ex) { Console.Error.WriteLine($"  WARNING: could not write the result for the invoking process to {_options.ResultFile}: {ex.Message}"); }
	}

	private int Finish(IReadOnlyList<UninstallOutcome> outcomes, ScopeParts removed, ScopeParts left, bool ownVerdict)
	{
		var remaining = outcomes.SelectMany(o => o.Remaining).ToArray();
		Console.WriteLine();

		if ((left & ScopeParts.System) != 0)
			Console.WriteLine(_target.IsWindows
				? "  The SYSTEM install stays; remove it from an elevated prompt with: seashell uninstall --system"
				: $"  Root's install stays; remove it with: sudo env \"PATH=$PATH\" seashell uninstall");
		Console.WriteLine(_target.IsWindows
			? "  Kept on purpose: the NuGet global packages cache (~/.nuget/packages) and the Event Log source 'SeaShell'."
			: "  Kept on purpose: the NuGet global packages cache (~/.nuget/packages).");
		if (GlobalTool.InstalledVersion() is { } global)
			Console.WriteLine($"  The global SeaShell tool v{global} is still installed: dotnet tool uninstall -g SeaShell");

		if (!ownVerdict) return remaining.Length == 0 ? 0 : 1;
		if (remaining.Length == 0)
		{
			Console.WriteLine(outcomes.Any(o => o.Deferred)
				? $"  {{~}} Uninstalled ({ScopeTokens.Describe(removed)}); this seashell's own files go once it exits."
				: $"  {{~}} Uninstalled ({ScopeTokens.Describe(removed)}).");
			return 0;
		}

		Console.Error.WriteLine("  Uninstall incomplete. Still present:");
		foreach (var r in remaining) Console.Error.WriteLine($"    {r}");
		return 1;
	}
}
