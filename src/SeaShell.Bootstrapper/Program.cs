using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Bootstrapper;
using SeaShell.Installer;

// ── SeaShell Bootstrapper ───────────────────────────────────────────
// Run via:  dnx SeaShell install --all   (or a global tool: dotnet tool install -g SeaShell)
//           and afterwards as the installed copy, {DataDir}/current/bin/seashell
// Command:  seashell install | update | uninstall | status | start | stop | schedule
//
// Extracts the embedded per-RID archive to a per-version directory,
// manages a `current` junction, and registers daemon + file associations.
// A bootstrapper without an embedded archive (the installed copy, a dev
// build) carries no version of its own: `install` then behaves as `update`,
// which fetches the latest package and runs that bootstrapper.
//
// This file parses the command line and dispatches. Each command lives in
// its own class in the SeaShell.Bootstrapper namespace; the install target
// (identity + version + derived paths) is passed to them explicitly, so no
// command reads captured mutable state from another one.

var version = typeof(Program).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
var options = BootstrapperOptions.Parse(args);

if (options.LogFile != null)
{
	var teeStream = new StreamWriter(File.Open(options.LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
	{
		AutoFlush = true,
	};
	Console.SetOut(new TeeWriter(Console.Out, teeStream));
	Console.SetError(new TeeWriter(Console.Error, teeStream));
}

// The install TARGET. On Windows, --system targets LocalSystem: every
// identity-derived value (data dir, task principal, lock name) is computed
// for S-1-5-18 and the elevated bootstrapper operates on it directly — no
// SYSTEM-context execution needed. On Linux, --system is just "run as root",
// so the ambient identity is already the target.
// An install with several parts (user, then SYSTEM) runs the install flow
// once per pass by retargeting; each pass builds its own InstallTarget, so a
// step can never run against the other pass's directories.
var target = new InstallTarget(
	options.SystemMode && isWindows ? SeaShellIdentity.LocalSystem : SeaShellIdentity.Current,
	version,
	isWindows);

DotnetHost.Override = options.Dotnet;

if (options.Error != null)
{
	Console.Error.WriteLine($"seashell: {options.Error}");
	return 1;
}

if (options.Command == null)
	return await new StatusReporter(target, options).RunAsync();

if (options.Command is "--help" or "-h")
{
	Console.WriteLine($"{{~}} SeaShell v{version}");
	Console.WriteLine();
	Console.WriteLine("Usage: seashell                    Show installation status");
	Console.WriteLine("       seashell status [--system]  Same; --system shows the SYSTEM install");
	Console.WriteLine();
	Console.WriteLine("       seashell install            Install the version this bootstrapper carries, with the");
	Console.WriteLine("                                   remembered scope (the installed copy carries none: same as update)");
	Console.WriteLine("       seashell install --all      Everything: user + elevator + SYSTEM (Windows, one UAC prompt)");
	Console.WriteLine("                                   or user + root (Linux, through sudo); remembered from now on");
	Console.WriteLine("       seashell install --user     User only; remembered from now on");
	Console.WriteLine("       seashell install --system   SYSTEM / root only (run elevated); leaves the user's scope alone");
	Console.WriteLine("       seashell update             Fetch the latest SeaShell and install it with the remembered scope");
	Console.WriteLine("                                   (takes --all / --user / --system like install)");
	Console.WriteLine("         --version <version>       Fetch this package version instead of the latest");
	Console.WriteLine("         --source <feed>           Fetch from this NuGet source instead of the configured ones");
	Console.WriteLine("       seashell update --auto on   Daily unattended update of the remembered scope (one task per");
	Console.WriteLine("                                   target; skipped while sessions are connected); --source <feed>");
	Console.WriteLine("                                   is stored with it. 'seashell update --auto off' removes it.");
	Console.WriteLine();
	Console.WriteLine("       seashell uninstall          Remove the remembered scope: binaries, tasks, PATH entry, file");
	Console.WriteLine("                                   associations, automatic updates (one UAC prompt / sudo when the");
	Console.WriteLine("                                   scope has elevated parts). Lists scheduled scripts and keeps them;");
	Console.WriteLine("         --purge                   also removes the scripts scheduled with 'seashell schedule'");
	Console.WriteLine("         --all / --user / --system Everything / the user install only / the SYSTEM or root install only");
	Console.WriteLine("       seashell start              Start daemon and elevator");
	Console.WriteLine("       seashell stop               Stop daemon and elevator");
	Console.WriteLine("       seashell schedule <script.cs> <timing...>");
	Console.WriteLine("       seashell schedule           List scheduled scripts");
	Console.WriteLine("       seashell unschedule <script.cs>");
	return 0;
}

switch (options.Command.ToLowerInvariant())
{
	case "update":
	case "install":
	{
		var scopeError = ScopeResolver.Validate(options.ScopeRequest);
		if (scopeError != null)
		{
			Console.Error.WriteLine($"seashell: {scopeError}");
			return 1;
		}

		if (options.Command.Equals("update", StringComparison.OrdinalIgnoreCase))
		{
			if (options.AutoUpdate != null)
				return await new AutoUpdateCommand(options, isWindows).RunAsync();
			return await new UpdateRunner(options, version, isWindows).RunAsync();
		}

		// No archive for this RID: nothing of its own to install, so fetch the
		// latest (the installed copy, a dev build). Not in update mode — that
		// is the fetched bootstrapper itself, and it must carry one.
		if (!EmbeddedArchive.CarriesCurrentRid(isWindows))
		{
			if (options.IfNeeded)
			{
				Console.Error.WriteLine($"seashell: this bootstrapper carries no archive for {EnvironmentProbe.CurrentRid(isWindows)}; cannot install --if-needed.");
				return 1;
			}
			// `dotnet tool update -g SeaShell && seashell install --all` keeps its
			// meaning: the installed copy wins PATH, but a newer global tool
			// carries the version the user just asked for.
			if (options.Pass == null && options.PackageVersion == null && options.Source == null
				&& GlobalTool.NewerThanInstalled() is { } global)
			{
				Console.WriteLine($"  This bootstrapper (v{version}, the installed copy) carries no SeaShell archive;");
				Console.WriteLine($"  the global SeaShell tool v{global.Version} does and is newer than the installed v{global.Installed?.ToString() ?? "(none)"}:");
				Console.WriteLine($"  running it instead of fetching from the NuGet sources:");
				Console.WriteLine($"    {DotnetHost.Describe(global.Path, args)}");
				Console.WriteLine();
				return DotnetHost.RunInherited(global.Path, args);
			}

			Console.WriteLine($"  This bootstrapper (v{version}) carries no SeaShell archive for {EnvironmentProbe.CurrentRid(isWindows)};");
			Console.WriteLine("  fetching the latest package instead, as 'seashell update' does.");
			Console.WriteLine();
			return await new UpdateRunner(options, version, isWindows).RunAsync();
		}

		// Preflight before anything on the machine changes. A relaunched pass
		// (--pass) was preflighted by the run that started it.
		if (options.Pass == null)
		{
			if (!Preflight.SdkPresent()) return 1;
			if (!Elevation.IsElevatedOrRoot(isWindows)) Preflight.LinuxPathHint();
		}

		if (options.SystemMode)
			return (await new InstallRunner(target, options, new InstallPassOptions(
				RegisterElevator: false, options.IfNeeded, options.KeepGlobalTool)).RunAsync()).ExitCode;
		return await new InstallCommand(target, options).RunAsync();
	}

	case "uninstall":
		return await new UninstallCommand(target, options, args).RunAsync();
	case SelfRelocation.FinishCommand:
		return SelfRelocation.Finish(options);

	case "status":
	case "--version":
	case "-v":
		return await new StatusReporter(target, options).RunAsync();

	case "start":
		return new DaemonLifecycle(target).Start();

	case "stop":
		return await new DaemonLifecycle(target).StopAsync();

	case "schedule":
		return new ScheduleCommands(target).Schedule(options.Arguments);
	case "unschedule":
		return new ScheduleCommands(target).Unschedule(options.Arguments);

	default:
		Console.Error.WriteLine($"seashell: unknown command '{options.Command}'. Use --help.");
		return 1;
}