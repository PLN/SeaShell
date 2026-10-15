using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Remove SeaShell from one target: stop instances, drop the user-profile
/// wiring (associations, PATH), delete this target's scheduled tasks across
/// every installed version, then remove the version directories themselves.
/// </summary>
internal sealed class UninstallRunner
{
	private readonly InstallTarget _target;
	private readonly BootstrapperOptions _options;

	public UninstallRunner(InstallTarget target, BootstrapperOptions options)
	{
		_target = target;
		_options = options;
	}

	public async Task<int> RunAsync()
	{
		if (!ElevationPolicy.IsSatisfied(_options.SystemMode, Elevation.IsElevatedOrRoot(_target.IsWindows)))
		{
			Console.Error.WriteLine("  ERROR: --system requires elevation (gsudo on Windows, sudo on Linux).");
			return 1;
		}

		using var installLock = InstallLock.TryAcquire(_target.Identity);
		if (installLock == null)
		{
			Console.Error.WriteLine("seashell: another install or uninstall is in progress");
			return 1;
		}

		Console.WriteLine($"{{~}} SeaShell Uninstaller{(_options.SystemMode ? " (system)" : "")}");
		Console.WriteLine();

		await InstanceStopper.StopAsync(_target);

		// Wipe any pre-0.5.0 flat-layout state so the final Directory.Delete
		// below on an empty DataDir actually succeeds.
		LegacyCleanup.SweepFilesystem(_target.DataDir, msg => Console.WriteLine($"  {msg}"));
		LegacyCleanup.SweepStaleTempDir(msg => Console.WriteLine($"  {msg}"));
		if (_target.Scope.SweepLegacySystemArtifacts)
			LegacyCleanup.SweepLegacySystemArtifacts(msg => Console.WriteLine($"  {msg}"));

		// Remove file associations and PATH entry — user-context concerns; the
		// SYSTEM target never had either.
		if (_target.Scope.EditFileAssociations)
		{
			FileAssoc.Unassociate(".cs", msg => Console.WriteLine($"  {msg}"));
			FileAssoc.Unassociate(".csw", msg => Console.WriteLine($"  {msg}"));
		}
		if (_target.Scope.EditUserProfilePath)
			PathEnvironment.Remove(_target);

		// Remove scheduled tasks belonging to the target identity, across all
		// installed versions. First via markers (precise), then sweep the
		// \SeaShell\ folder for orphans (covers tasks left behind by a crashed
		// prior uninstall). The "({DisplayName})" prefix matches pre-0.6.0 task
		// names too, including "(SYSTEM)".
		if (_target.IsWindows)
		{
			foreach (var (_, dir) in VersionDiscovery.EnumerateVersions(_target.DataDir))
			{
				foreach (var marker in new[] { MarkerFile.DaemonTask, MarkerFile.ElevatorTask })
				{
					var taskPath = MarkerFile.Read(dir, marker);
					if (!string.IsNullOrEmpty(taskPath) && TaskQuery.Delete(taskPath))
						Console.WriteLine($"  Removed task: {taskPath}");
				}
			}

			foreach (var path in TaskQuery.EnumerateFolder().ToList())
			{
				if (!SeaShellTaskNames.MatchesTarget(path, _target.DisplayName)) continue;
				if (TaskQuery.Delete(path))
					Console.WriteLine($"  Removed orphan task: {SeaShellTaskNames.NameOf(path)}");
			}
		}

		// Remove current junction
		if (JunctionHelper.IsJunction(_target.CurrentDir))
		{
			try { Directory.Delete(_target.CurrentDir, false); }
			catch { }
			Console.WriteLine("  Removed current junction");
		}

		// Remove all version directories
		foreach (var (ver, dir) in VersionDiscovery.EnumerateVersions(_target.DataDir))
		{
			Console.WriteLine($"  Removing v{ver}...");
			try { Directory.Delete(dir, true); }
			catch (Exception ex) { Console.Error.WriteLine($"  Warning: {ex.Message}"); }
		}

		// Remove shared dirs
		var snippetsDir = Path.Combine(_target.DataDir, "snippets");
		if (Directory.Exists(snippetsDir))
		{
			try { Directory.Delete(snippetsDir, true); } catch { }
		}

		// Remove data dir if empty
		if (Directory.Exists(_target.DataDir))
		{
			try { Directory.Delete(_target.DataDir, false); } // non-recursive: only if empty
			catch { }
		}

		Console.WriteLine("  {~} Uninstalled.");
		if (_target.Scope.SuggestBootstrapperRemoval)
			Console.WriteLine("  Run 'dotnet tool uninstall -g SeaShell' to remove the bootstrapper.");
		return 0;
	}
}