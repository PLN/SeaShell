using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// <c>seashell update --auto on|off [--source &lt;feed&gt;]</c>: create or
/// remove the scheduled automatic-update entries for the remembered scope
/// (<see cref="AutoUpdatePlan"/>). State is the presence of the entries.
///
/// Windows: the user task needs elevation to register when it runs with
/// highest privileges (the scope has an elevator), the SYSTEM task always;
/// the command then self-elevates once, as install does, and an elevated
/// helper running as a different account creates only the SYSTEM task — the
/// invoking console creates the user's own task itself. Linux: the user's
/// crontab here, root's through the same sudo pass as install.
///
/// The SYSTEM / root entry records a <c>dotnet</c> that only administrators can
/// change (<see cref="SystemDotnet"/>); when there is none, that entry is not
/// created, the user's is, and the command exits with
/// <see cref="SystemDotnet.RefusedExitCode"/>.
/// </summary>
internal sealed class AutoUpdateCommand
{
	private readonly BootstrapperOptions _options;
	private readonly bool _isWindows;
	private bool _relaunchedOnly;
	private bool _systemRefused;

	/// <summary><c>--source</c> as the entries store it (a local path made absolute).</summary>
	private readonly string? _source;

	public AutoUpdateCommand(BootstrapperOptions options, bool isWindows)
	{
		_options = options;
		_isWindows = isWindows;
		_source = options.Source == null ? null
			: AutoUpdatePlan.ResolveSource(options.Source, Environment.CurrentDirectory, p => Directory.Exists(p) || File.Exists(p));
	}

	public async Task<int> RunAsync()
	{
		if (AutoUpdatePlan.ParseSwitch(_options.AutoUpdate) is not { } on)
		{
			Console.Error.WriteLine($"seashell: --auto expects on or off (got '{_options.AutoUpdate}')");
			return 1;
		}
		if (_options.AllMode || _options.UserMode || _options.SystemMode)
		{
			Console.Error.WriteLine("seashell: --auto applies to the remembered scope; change that first with 'seashell install --all' or '--user'.");
			return 1;
		}

		if (_source != null && AutoUpdatePlan.StoredValueError(_source) is { } sourceError)
		{
			Console.Error.WriteLine($"seashell: --source {sourceError}; it cannot be stored in a scheduled entry.");
			return 1;
		}

		Console.WriteLine($"{{~}} SeaShell automatic updates: {(on ? "on" : "off")}");
		Console.WriteLine();
		if (on && _source != null)
			Console.WriteLine(_source == _options.Source ? $"  Source: {_source}" : $"  Source: {_source} (from '{_options.Source}')");

		var rootAmbient = !_isWindows && Elevation.IsElevatedOrRoot(false);
		var parts = rootAmbient ? ScopeParts.System : _options.Pass ?? ResolveScope();

		// The user's entry records the user's dotnet; root's own process checks
		// its candidates itself (ChooseSystemDotnet).
		var dotnet = DotnetHost.Resolve();
		if (on && !rootAmbient && !ValidDotnet(dotnet)) return 1;

		var user = SeaShellIdentity.Current;
		var entries = AutoUpdatePlan.Entries(parts, _isWindows, Environment.MachineName, user.DisplayName, user.Token, dotnet, _source);
		if (rootAmbient) entries = entries.Where(e => e.System).ToArray();

		var exit = _isWindows ? await WindowsAsync(on, parts, entries) : Linux(on, parts, entries, rootAmbient);
		if (exit == 0 && _systemRefused) exit = SystemDotnet.RefusedExitCode;
		if (_relaunchedOnly) return exit; // the elevated helper printed the verdict
		Console.WriteLine();
		Console.WriteLine(exit switch
		{
			0 => $"  {{~}} Automatic updates are {(on ? "on" : "off")}.",
			SystemDotnet.RefusedExitCode => $"  Automatic updates: {(rootAmbient ? "root's entry was not created" : $"on for {user.DisplayName}; the {(_isWindows ? "SYSTEM" : "root")} entry was not created")} (see above).",
			_ => "  Automatic updates: not every entry could be changed (see above).",
		});
		return exit;
	}

	private ScopeParts ResolveScope()
	{
		var recorded = InstallScopeFile.Read(SeaShellIdentity.Current.DataDir);
		var resolution = ScopeResolver.Resolve(new ScopeRequest(), recorded, () => ScopeProbe.Detect(_isWindows), _isWindows);
		Console.WriteLine($"  Scope: {ScopeTokens.Describe(resolution.Parts)} ({(resolution.Source == ScopeSource.Recorded ? "recorded" : "detected")})");
		if (resolution.Unresolved != ScopeParts.None)
			Console.WriteLine($"  NOTE: an existing {ScopeTokens.Describe(resolution.Unresolved)} install could not be ruled out; 'seashell install --all' records the full scope.");
		return resolution.Parts;
	}

	// The entry records this dotnet; it must exist and have the SDK that
	// `dotnet tool install` needs, now rather than at 3 a.m.
	private static bool ValidDotnet(string dotnet)
	{
		if (!Path.IsPathRooted(dotnet) || !File.Exists(dotnet))
		{
			Console.Error.WriteLine($"  ERROR: no absolute dotnet path to record (resolved '{dotnet}'). Set DOTNET_ROOT or put dotnet on PATH.");
			return false;
		}
		Console.WriteLine($"  dotnet: {dotnet}");
		return Preflight.SdkPresent();
	}

	/// <summary>The SYSTEM / root entry's dotnet, its DOTNET_ROOT and (Linux) PATH.</summary>
	private sealed record SystemHost(string Dotnet, string Root, string Path);

	/// <summary>
	/// The first candidate (the enabling user's dotnet, then the machine-wide
	/// install) that only administrators / root can change and that has a
	/// .NET 10 SDK; null, with every candidate's reason and what to do printed,
	/// when there is none.
	/// </summary>
	private SystemHost? ChooseSystemDotnet()
	{
		var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		(string, string?) Examine(string candidate)
		{
			if (!File.Exists(candidate)) return (candidate, "does not exist");
			var path = _isWindows ? Path.GetFullPath(candidate) : CacheDirsInUse.RealPath(candidate) ?? candidate;
			var reason = _isWindows
				? SystemDotnet.WindowsDisqualified(path, programFiles)
				: SystemDotnet.LinuxDisqualified(path, EnvironmentProbe.Stat);
			if (reason == null && !DotnetSdk.HasSdk(EnvironmentProbe.RunProcess(path, "--list-sdks"), 10))
				reason = "has no .NET 10 SDK";
			return (path, reason);
		}

		var choice = SystemDotnet.Choose(SystemDotnet.Candidates(DotnetHost.Resolve(), _isWindows, programFiles), Examine);
		if (choice.Dotnet is { } chosen)
		{
			foreach (var r in choice.Rejected) Console.WriteLine($"    not recorded for {(_isWindows ? "SYSTEM" : "root")}: {r}");
			var root = Path.GetDirectoryName(chosen)!;
			return new SystemHost(chosen, root, _isWindows ? "" : SystemDotnet.LinuxPathFor(root, EnvironmentProbe.Stat));
		}

		var who = _isWindows ? "SYSTEM" : "root";
		Console.Error.WriteLine($"  ERROR: the {who} entry was not created: it runs as {who}, so the dotnet it records must be");
		Console.Error.WriteLine("         in a location only administrators can change, and none of these qualifies:");
		foreach (var r in choice.Rejected) Console.Error.WriteLine($"           {r}");
		Console.Error.WriteLine(_isWindows
			? $"         Install the .NET 10 SDK machine-wide (the installer from https://dot.net puts it in {Path.Combine(programFiles, "dotnet")}),"
			: "         Install the .NET 10 SDK machine-wide as root (the distribution's dotnet-sdk-10.0 package, or under /usr/share/dotnet or /usr/lib/dotnet),");
		Console.Error.WriteLine("         then run 'seashell update --auto on' again. Updating it by hand: 'seashell update'.");
		_systemRefused = true;
		return null;
	}

	// ── Windows ────────────────────────────────────────────────────────

	private async Task<int> WindowsAsync(bool on, ScopeParts parts, IReadOnlyList<AutoUpdateEntry> entries)
	{
		// Registering a highest-privileges or SYSTEM task needs elevation; so
		// does removing one that an elevated process registered.
		var needsElevation = (parts & ScopeTokens.NeedingElevation) != 0;
		if (needsElevation && !Elevation.IsElevated(true))
		{
			var relaunch = await PassRelaunch.ElevatedAsync(resultFile => RelaunchArguments(on, parts, resultFile),
				$"automatic updates {(on ? "on" : "off")} ({ScopeTokens.Describe(parts)})");
			if (relaunch.Summary is not { UserSkipped: true })
			{
				_relaunchedOnly = true;
				return relaunch.ExitCode;
			}

			// The helper ran as another account and did SYSTEM only.
			Console.WriteLine();
			Console.WriteLine("  The elevated helper ran as a different account; changing this account's task here.");
			var own = entries.Where(e => !e.System).Select(e => e with { HighestPrivileges = false });
			return Worst(Apply(on, own, skipUser: false), relaunch.ExitCode);
		}

		var skipUser = InstallAllPlanner.ShouldSkipUserParts(_options.InvokerSid, SeaShellIdentity.Current.Token);
		var exit = Apply(on, entries, skipUser);
		if (_options.ResultFile != null)
		{
			var summary = new InstallRunSummary { UserSkipped = skipUser };
			if (exit == 0 && !_systemRefused) summary.Add(parts, PassResult.Installed);
			try { File.WriteAllText(_options.ResultFile, summary.Serialize()); } catch { }
		}
		return exit;
	}

	// 1 (failed) beats the refusal of the SYSTEM entry, which beats success.
	private int Worst(int a, int b)
	{
		if (a == SystemDotnet.RefusedExitCode) { _systemRefused = true; a = 0; }
		if (b == SystemDotnet.RefusedExitCode) { _systemRefused = true; b = 0; }
		return a != 0 || b != 0 ? 1 : 0;
	}

	private List<string> RelaunchArguments(bool on, ScopeParts parts, string resultFile)
	{
		var args = new List<string> { "update", "--auto", on ? "on" : "off", "--pass", ScopeTokens.FormatList(parts) };
		if (_source != null) { args.Add("--source"); args.Add(_source); }
		args.Add("--dotnet");
		args.Add(DotnetHost.Resolve());
		args.Add("--result-file");
		args.Add(resultFile);
		return args;
	}

	private int Apply(bool on, IEnumerable<AutoUpdateEntry> entries, bool skipUser)
	{
		var exit = 0;
		var user = SeaShellIdentity.Current;
		foreach (var listed in entries)
		{
			var entry = listed;
			if (!entry.System && skipUser)
			{
				Console.WriteLine($"  {entry.Name}: skipped (elevated as a different account than the invoker; the invoking console does it).");
				continue;
			}

			if (!on)
			{
				var presence = TaskQuery.Probe(entry.Name);
				if (presence == PartPresence.Absent) Console.WriteLine($"  {entry.Name}: not registered");
				else if (TaskQuery.Delete(entry.Name)) Console.WriteLine($"  Removed task: {entry.Name}");
				else { Console.Error.WriteLine($"  ERROR: could not remove {entry.Name}"); exit = 1; }
				continue;
			}

			var dataDir = entry.System ? SeaShellIdentity.LocalSystem.DataDir : user.DataDir;
			var seashell = Path.Combine(SeaShellPaths.CurrentDirIn(dataDir), "bin", "seashell.exe");
			if (!File.Exists(seashell))
			{
				Console.Error.WriteLine($"  ERROR: {seashell} does not exist; the task would have nothing to run.");
				Console.Error.WriteLine("         Install 0.6.3 or newer there first ('seashell update').");
				exit = 1;
				continue;
			}

			if (entry.System)
			{
				if (ChooseSystemDotnet() is not { } host)
				{
					// A task left from an earlier 'on' would keep running the old dotnet.
					if (TaskQuery.Probe(entry.Name) != PartPresence.Absent && TaskQuery.Delete(entry.Name))
						Console.WriteLine($"  Removed the earlier task: {entry.Name}");
					continue;
				}
				entry = AutoUpdatePlan.WithDotnet(entry, host.Dotnet, _source);
			}

			Console.WriteLine($"  {entry.Name}: daily at {entry.Time:HH:mm} (catches up when missed){(entry.HighestPrivileges && !entry.System ? ", highest privileges (elevator)" : "")}");
			Console.WriteLine($"    {DotnetHost.Describe(seashell, entry.Arguments)}");
			if (!DaemonTasks.RegisterUpdateTask(entry, seashell, user, msg => Console.WriteLine($"    {msg}")))
				exit = 1;
		}
		return exit;
	}

	// ── Linux ──────────────────────────────────────────────────────────

	private int Linux(bool on, ScopeParts parts, IReadOnlyList<AutoUpdateEntry> entries, bool rootAmbient)
	{
		var exit = 0;
		// This identity's own crontab: the user's entry, or root's when we are root.
		var own = entries.FirstOrDefault(e => e.System == rootAmbient);
		if (own != null) exit = WriteOwnCrontab(on, own);

		if (!rootAmbient && (parts & ScopeParts.System) != 0)
		{
			var relaunch = PassRelaunch.Sudo(resultFile => RelaunchArguments(on, ScopeParts.System, resultFile),
				$"automatic-update {(on ? "setup" : "removal")} for root");
			exit = Worst(exit, relaunch.ExitCode);
		}
		return exit;
	}

	private int WriteOwnCrontab(bool on, AutoUpdateEntry entry)
	{
		var crontab = Crontab.Read();
		if (crontab == null)
		{
			Console.Error.WriteLine("  ERROR: could not read the crontab ('crontab -l' failed); nothing was changed.");
			return 1;
		}

		string? line = null;
		if (on)
		{
			var seashell = Path.Combine(SeaShellPaths.CurrentBinDir, "seashell");
			if (!File.Exists(seashell))
			{
				Console.Error.WriteLine($"  ERROR: {seashell} does not exist; the entry would have nothing to run ('seashell update' first).");
				return 1;
			}
			var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (entry.System)
			{
				if (ChooseSystemDotnet() is not { } host)
				{
					// A line left from an earlier 'on' would keep running the old dotnet.
					if (crontab.Any(AutoUpdatePlan.IsCronLine) && Crontab.Write(AutoUpdatePlan.WithCronLine(crontab, null)))
						Console.WriteLine($"  {entry.Name}: the earlier automatic-update line was removed");
					return 0;
				}
				line = AutoUpdatePlan.CronLine(AutoUpdatePlan.WithDotnet(entry, host.Dotnet, _source), seashell, home, host.Root, host.Path);
			}
			else
				line = AutoUpdatePlan.CronLine(entry, seashell, home, DotnetHost.Root() ?? "", Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin");
		}
		else if (!crontab.Any(AutoUpdatePlan.IsCronLine))
		{
			Console.WriteLine($"  {entry.Name}: no automatic-update line");
			return 0;
		}

		if (!Crontab.Write(AutoUpdatePlan.WithCronLine(crontab, line)))
		{
			Console.Error.WriteLine("  ERROR: could not write the crontab.");
			return 1;
		}
		Console.WriteLine(on ? $"  {entry.Name}: {line}" : $"  {entry.Name}: automatic-update line removed");
		return 0;
	}
}
