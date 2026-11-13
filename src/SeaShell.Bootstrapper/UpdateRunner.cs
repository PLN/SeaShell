using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Installer;
using SeaShell.Protocol;

namespace SeaShell.Bootstrapper;

/// <summary>
/// <c>seashell update</c>, from any bootstrapper: fetch the latest SeaShell
/// package into a throw-away tool directory (<c>dotnet tool install SeaShell
/// --tool-path</c>: works on every .NET 10 SDK and never prompts), run that
/// bootstrapper with <c>install --if-needed</c> and the same scope flags,
/// forward its output and exit code, and remove the directory.
///
/// The fetched bootstrapper resolves the scope itself — from the same flags,
/// or from the same user's <c>install.scope</c> — so recording, elevation and
/// the sudo pass all follow the newest version's rules.
///
/// The scheduled automatic update adds <c>--if-idle</c> (skip while the
/// target's daemon has connected sessions) and <c>--quiet</c> (see
/// <see cref="QuietRun"/>).
/// </summary>
internal sealed class UpdateRunner
{
	private readonly BootstrapperOptions _options;
	private readonly string _version;
	private readonly bool _isWindows;

	public UpdateRunner(BootstrapperOptions options, string version, bool isWindows)
	{
		_options = options;
		_version = version;
		_isWindows = isWindows;
	}

	public async Task<int> RunAsync()
	{
		var quiet = QuietRun.Begin(_options.Quiet);
		var outcome = QuietOutcome.Failed;
		var summary = "SeaShell update failed";
		var exit = 1;
		try
		{
			(exit, outcome, summary) = await RunCoreAsync(quiet);
			return exit;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"  ERROR: {ex}");
			return 1;
		}
		finally
		{
			quiet?.End(outcome, $"{summary} (seashell update {string.Join(' ', ScopeArguments())}, exit {exit})", exit);
		}
	}

	private async Task<(int Exit, QuietOutcome Outcome, string Summary)> RunCoreAsync(QuietRun? quiet)
	{
		Console.WriteLine($"{{~}} SeaShell update (bootstrapper v{_version})");
		Console.WriteLine();

		if (_options.IfIdle && await ConnectedSessionsAsync() is { } busy)
		{
			Console.WriteLine($"  Skipped: the daemon of {busy.Name} has {busy.Sessions} connected session(s)");
			Console.WriteLine("  (watch sessions, REPLs or compiles that an update would cut off). The next run tries again.");
			return (0, QuietOutcome.Nothing, $"SeaShell update skipped: {busy.Sessions} connected session(s) on the daemon of {busy.Name}");
		}

		if (!Preflight.SdkPresent()) return (1, QuietOutcome.Failed, "SeaShell update failed: no .NET 10 SDK");

		var dotnet = DotnetHost.Resolve();
		var toolDir = Path.Combine(Path.GetTempPath(), $"seashell-update-{Guid.NewGuid():N}");
		try
		{
			var fetchArgs = new List<string> { "tool", "install", "SeaShell", "--tool-path", toolDir };
			if (_options.PackageVersion != null) { fetchArgs.Add("--version"); fetchArgs.Add(_options.PackageVersion); }
			if (_options.Source != null) { fetchArgs.Add("--source"); fetchArgs.Add(_options.Source); }

			var sources = _options.Source != null ? new[] { _options.Source } : ConfiguredSources(dotnet);
			Console.WriteLine($"  Fetching SeaShell {_options.PackageVersion ?? "(latest)"} from " +
				(_options.Source != null ? "this source only:" : "the configured NuGet sources (highest version wins):"));
			foreach (var source in sources) Console.WriteLine($"    {source}");
			Console.WriteLine($"    {DotnetHost.Describe(dotnet, fetchArgs)}");
			var fetchExit = Run(quiet, dotnet, fetchArgs);
			if (fetchExit != 0)
			{
				Console.Error.WriteLine($"  ERROR: could not fetch the SeaShell package ('dotnet tool install' exited {fetchExit}); nothing was changed.");
				if (EmbeddedArchive.CarriesCurrentRid(_isWindows))
					Console.Error.WriteLine($"         'seashell install' installs v{_version}, which this bootstrapper carries, without a download.");
				return (fetchExit, QuietOutcome.Failed, "SeaShell update failed: could not fetch the package");
			}

			var fetched = Path.Combine(toolDir, _isWindows ? "seashell.exe" : "seashell");
			if (!File.Exists(fetched))
			{
				Console.Error.WriteLine($"  ERROR: the fetched package has no {Path.GetFileName(fetched)} in {toolDir}.");
				return (1, QuietOutcome.Failed, "SeaShell update failed: the package has no bootstrapper");
			}
			var fetchedVersion = FetchedVersion(toolDir) ?? "?";
			Console.WriteLine($"  Resolved: SeaShell v{fetchedVersion}");

			// A quiet run learns what the install did from its summary; an
			// interactive one lets it print "Up to date" itself.
			var resultFile = quiet != null ? Path.Combine(toolDir, "update-result.txt") : null;
			var installArgs = ForwardedInstallArguments(resultFile);
			Console.WriteLine();
			Console.WriteLine($"  Running: {DotnetHost.Describe(fetched, installArgs)}");
			Console.WriteLine();
			var exit = Run(quiet, fetched, installArgs);
			if (exit != 0) return (exit, QuietOutcome.Failed, $"SeaShell update to v{fetchedVersion} failed");

			var result = ReadResult(resultFile);
			var installed = result == null ? ScopeParts.None : result.Completed & ~result.Satisfied;
			return installed != ScopeParts.None
				? (0, QuietOutcome.Done, $"SeaShell updated to v{fetchedVersion}: {ScopeTokens.Describe(installed)}")
				: (0, QuietOutcome.Nothing, $"SeaShell up to date (v{fetchedVersion} is the latest; nothing to install)");
		}
		finally
		{
			RemoveToolDir(toolDir);
		}
	}

	private static int Run(QuietRun? quiet, string fileName, List<string> arguments) =>
		quiet != null
			? DotnetHost.RunCaptured(fileName, arguments, quiet.Sink)
			: DotnetHost.RunInherited(fileName, arguments);

	/// <summary>
	/// <c>--if-idle</c>: the connected sessions (<c>ActiveScripts</c>) of the
	/// target's live daemon(s); null when idle or not running. A snapshot,
	/// not a barrier: plain running scripts hold no connection and keep
	/// running from their own cache directory across an update.
	/// </summary>
	private async Task<(string Name, int Sessions)?> ConnectedSessionsAsync()
	{
		var identity = _isWindows && _options.Pass == ScopeParts.System
			? SeaShellIdentity.LocalSystem
			: SeaShellIdentity.Current;
		var sessions = 0;
		foreach (var ep in TransportEndpoint.EnumerateAllSeashellEndpoints())
		{
			if (ep.IsElevator || !identity.Matches(ep.Identity)) continue;
			var ping = await DaemonIpc.PingAsync(ep.Address);
			if (ping != null) sessions += ping.ActiveScripts;
		}
		return sessions > 0 ? (identity.DisplayName, sessions) : null;
	}

	private IEnumerable<string> ScopeArguments()
	{
		if (_options.AllMode) yield return "--all";
		if (_options.UserMode) yield return "--user";
		if (_options.SystemMode) yield return "--system";
		if (_options.Pass is { } pass) { yield return "--pass"; yield return ScopeTokens.FormatList(pass); }
		if (_options.Source != null) { yield return "--source"; yield return _options.Source; }
	}

	/// <summary>
	/// <c>install --if-needed</c> plus this invocation's scope flags. The
	/// fetched bootstrapper must not remove a global tool that this very
	/// process runs from (it is still executing).
	/// </summary>
	private List<string> ForwardedInstallArguments(string? resultFile)
	{
		var args = new List<string> { "install", "--if-needed" };
		if (_options.AllMode) args.Add("--all");
		if (_options.UserMode) args.Add("--user");
		if (_options.SystemMode) args.Add("--system");
		if (_options.Pass is { } pass) { args.Add("--pass"); args.Add(ScopeTokens.FormatList(pass)); }
		if (_options.KeepGlobalTool || GlobalTool.RunningFromIt()) args.Add("--keep-global-tool");
		if (resultFile != null) { args.Add("--result-file"); args.Add(resultFile); }
		return args;
	}

	private static InstallRunSummary? ReadResult(string? path)
	{
		if (path == null) return null;
		try
		{
			var content = File.ReadAllText(path);
			return string.IsNullOrWhiteSpace(content) ? null : InstallRunSummary.Parse(content);
		}
		catch { return null; }
	}

	/// <summary>
	/// The enabled sources <c>dotnet tool install</c> will search, as
	/// <c>dotnet nuget list source</c> reports them (empty when it cannot).
	/// </summary>
	private static string[] ConfiguredSources(string dotnet)
	{
		var output = EnvironmentProbe.RunProcess(dotnet, "nuget list source --format short");
		var enabled = new List<string>();
		foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			// "E https://api.nuget.org/v3/index.json"; "D ..." is disabled; "EM" = enabled machine-wide.
			var space = raw.IndexOf(' ');
			if (space > 0 && raw[..space].Contains('E') && !raw[..space].Contains('D'))
				enabled.Add(raw[(space + 1)..].Trim());
		}
		return enabled.Count > 0 ? enabled.ToArray() : new[] { "(could not list them: 'dotnet nuget list source' failed)" };
	}

	// dotnet tool install --tool-path keeps the package in .store/seashell/{version}/.
	private static string? FetchedVersion(string toolDir)
	{
		try
		{
			var store = Path.Combine(toolDir, ".store", "seashell");
			return Directory.GetDirectories(store).Select(Path.GetFileName).FirstOrDefault();
		}
		catch { return null; }
	}

	// The fetched bootstrapper (and any elevated helper it started) has exited,
	// but Windows can take a moment to release the image files.
	private static void RemoveToolDir(string toolDir)
	{
		for (var attempt = 0; attempt < 5; attempt++)
		{
			try
			{
				if (Directory.Exists(toolDir)) Directory.Delete(toolDir, true);
				return;
			}
			catch when (attempt < 4) { Thread.Sleep(400); }
			catch (Exception ex)
			{
				Console.Error.WriteLine($"  WARNING: could not remove {toolDir}: {ex.Message}");
			}
		}
	}
}
