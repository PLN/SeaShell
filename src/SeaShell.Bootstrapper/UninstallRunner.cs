using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// What one uninstall pass left behind. <see cref="Remaining"/> lists what is
/// still there and why; empty means the target is gone. <see cref="Deferred"/>:
/// the only remaining files are held by the seashell that started this
/// (relocated) uninstall, which removes them after it exits.
/// </summary>
internal sealed record UninstallOutcome(IReadOnlyList<string> Remaining, bool Deferred)
{
	public bool Complete => Remaining.Count == 0;

	public static UninstallOutcome Failed(string reason) => new(new[] { reason }, false);
}

/// <summary>
/// Remove SeaShell from one target: stop instances, drop the user-profile
/// wiring (associations, PATH, symlinks), delete this target's daemon,
/// elevator and automatic-update entries, list (or with <c>--purge</c>
/// remove) its scheduled scripts, then remove the version directories and
/// SeaShell's own files in the data directory. Anything that cannot be
/// removed is reported, never silently passed over.
/// </summary>
internal sealed class UninstallRunner
{
	private readonly InstallTarget _target;
	private readonly BootstrapperOptions _options;
	private readonly List<string> _remaining = new();
	private bool _deferred;

	public UninstallRunner(InstallTarget target, BootstrapperOptions options)
	{
		_target = target;
		_options = options;
	}

	public async Task<UninstallOutcome> RunAsync()
	{
		var isSystemTarget = _target.IsSystemTarget || (!_target.IsWindows && _target.Identity.IsSystem);
		if (isSystemTarget && !Elevation.IsElevatedOrRoot(_target.IsWindows))
		{
			Console.Error.WriteLine("  ERROR: removing the SYSTEM / root install requires elevation (gsudo on Windows, sudo on Linux).");
			return UninstallOutcome.Failed($"{_target.DisplayName}: not elevated");
		}

		using (var installLock = InstallLock.TryAcquire(_target.Identity, TimeSpan.FromSeconds(30),
			() => Console.WriteLine("  Waiting for another install, uninstall or removal to finish...")))
		{
			if (installLock == null)
			{
				Console.Error.WriteLine("seashell: another install or uninstall is in progress");
				return UninstallOutcome.Failed($"{_target.DisplayName}: another install or uninstall is in progress");
			}
			await RemoveAsync();
		}

		// Linux: the lock was a file in the data directory; with it released, an
		// otherwise empty data directory can go too.
		if (_dataDirEmptyButLock) RemoveEmptyDataDir();
		return new UninstallOutcome(_remaining.ToArray(), _deferred);
	}

	private async Task RemoveAsync()
	{
		Console.WriteLine($"{{~}} SeaShell Uninstaller ({_target.DisplayName})");
		Console.WriteLine();

		await InstanceStopper.StopAsync(_target);

		// Wipe any pre-0.5.0 flat-layout state so the final Directory.Delete
		// below on an empty DataDir actually succeeds.
		LegacyCleanup.SweepFilesystem(_target.DataDir, msg => Console.WriteLine($"  {msg}"));
		LegacyCleanup.SweepStaleTempDir(msg => Console.WriteLine($"  {msg}"));
		if (_target.Scope.SweepLegacySystemArtifacts)
			LegacyCleanup.SweepLegacySystemArtifacts(msg => Console.WriteLine($"  {msg}"));

		// File associations and PATH / symlinks — user-context concerns; the
		// SYSTEM target never had either.
		if (_target.Scope.EditFileAssociations)
		{
			FileAssoc.Unassociate(".cs", msg => Console.WriteLine($"  {msg}"));
			FileAssoc.Unassociate(".csw", msg => Console.WriteLine($"  {msg}"));
		}
		if (_target.Scope.EditUserProfilePath && PathEnvironment.Remove(_target) is { } pathLeft)
			_remaining.Add(pathLeft);

		if (_target.IsWindows) RemoveWindowsTasks();
		HandleScheduledScripts();

		RemoveFiles();
	}

	// ── Tasks ──────────────────────────────────────────────────────────

	// The target's tasks across all installed versions: first via markers
	// (precise), then the \SeaShell\ folder for orphans (left by a crashed
	// uninstall, or pre-0.6.0 names: "({DisplayName})" matches those too), and
	// the automatic-update task. Daemon/elevator/update kinds only — scheduled
	// scripts are handled separately.
	private void RemoveWindowsTasks()
	{
		var marked = new List<string>();
		foreach (var (_, dir) in VersionDiscovery.EnumerateVersions(_target.DataDir))
		{
			foreach (var marker in new[] { MarkerFile.DaemonTask, MarkerFile.ElevatorTask })
			{
				var taskPath = MarkerFile.Read(dir, marker);
				if (!string.IsNullOrEmpty(taskPath)) marked.Add(taskPath);
			}
		}
		foreach (var taskPath in marked.Distinct(StringComparer.OrdinalIgnoreCase))
			DeleteTask(taskPath, "task");

		foreach (var path in TaskQuery.EnumerateFolder().ToList())
		{
			if (SeaShellTaskNames.MatchesTarget(path, _target.DisplayName))
				DeleteTask(path, "orphan task");
			else if (SeaShellTaskNames.IsUpdateTaskOf(path, _target.DisplayName))
				DeleteTask(path, "automatic-update task");
		}
	}

	private void DeleteTask(string taskPath, string label)
	{
		if (TaskQuery.Probe(taskPath) == PartPresence.Absent) return;
		if (TaskQuery.Delete(taskPath) && TaskQuery.Probe(taskPath) == PartPresence.Absent)
		{
			Console.WriteLine($"  Removed {label}: {SeaShellTaskNames.NameOf(taskPath)}");
			return;
		}
		var hint = Elevation.IsElevated(true) ? "" : " (registered by an elevated install: run 'seashell uninstall' from an elevated prompt)";
		Console.Error.WriteLine($"  ERROR: could not remove {label} {taskPath}{hint}");
		_remaining.Add($"task {taskPath}{hint}");
	}

	// ── Scheduled scripts ──────────────────────────────────────────────

	private void HandleScheduledScripts()
	{
		IReadOnlyList<ScheduledScript> scripts;
		List<string>? crontab = null;
		if (_target.IsWindows)
		{
			// SeaShell's script tasks are named after the account that scheduled
			// them; SYSTEM schedules none through seashell.
			if (_target.IsSystemTarget) return;
			scripts = UninstallPlanner.WindowsScripts(TaskQuery.EnumerateFolder().ToList(), Environment.UserName);
		}
		else
		{
			crontab = Crontab.Read();
			if (crontab == null)
			{
				Console.Error.WriteLine("  WARNING: could not read the crontab; its automatic-update line and scheduled scripts were not checked.");
				_remaining.Add("crontab entries (crontab -l failed)");
				return;
			}
			scripts = UninstallPlanner.CronScripts(crontab);

			var after = UninstallPlanner.CrontabAfter(crontab, _options.Purge);
			var before = crontab.ToList();
			while (before.Count > 0 && string.IsNullOrWhiteSpace(before[^1])) before.RemoveAt(before.Count - 1);
			if (!after.SequenceEqual(before))
			{
				var hadAutoUpdate = crontab.Exists(AutoUpdatePlan.IsCronLine);
				if (Crontab.Write(after))
				{
					if (hadAutoUpdate) Console.WriteLine("  Removed the automatic-update crontab line");
				}
				else
				{
					Console.Error.WriteLine("  ERROR: could not write the crontab.");
					_remaining.Add("crontab entries (crontab write failed)");
					return;
				}
			}
		}

		if (scripts.Count == 0) return;

		if (_options.Purge)
		{
			foreach (var script in scripts)
			{
				if (!_target.IsWindows || TaskQuery.Delete(script.Entry))
					Console.WriteLine($"  Removed scheduled script: {script.Name}");
				else
				{
					Console.Error.WriteLine($"  ERROR: could not remove scheduled script {script.Name} ({script.Entry})");
					_remaining.Add($"scheduled script {script.Name}");
				}
			}
			return;
		}

		Console.WriteLine($"  Scheduled scripts kept ({scripts.Count}); they run again after a reinstall,");
		Console.WriteLine("  'seashell uninstall --purge' removes them:");
		foreach (var script in scripts) Console.WriteLine($"    {script.Name}");
	}

	// ── Files ──────────────────────────────────────────────────────────

	private void RemoveFiles()
	{
		if (Directory.Exists(_target.CurrentDir) || JunctionHelper.IsJunction(_target.CurrentDir))
		{
			try
			{
				Directory.Delete(_target.CurrentDir, false);
				Console.WriteLine("  Removed current junction");
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"  ERROR: could not remove {_target.CurrentDir}: {ex.Message}");
				_remaining.Add($"{_target.CurrentDir} ({ex.Message})");
			}
		}

		foreach (var (ver, dir) in VersionDiscovery.EnumerateVersions(_target.DataDir))
			RemoveVersionDir($"v{ver}", dir);

		// Leftovers of an earlier attempt that renamed but could not finish:
		// probed again like a version directory, never deleted while in use.
		foreach (var aside in SafeDirectories(_target.DataDir, "*" + RemovingMarker + "*"))
			ReportRemoval(Path.GetFileName(aside), aside, VersionDirRemoval.RemoveLeftover(aside));

		foreach (var name in new[] { "snippets", "restore" })
		{
			var dir = Path.Combine(_target.DataDir, name);
			if (Directory.Exists(dir)) RemoveTree(dir, name);
		}

		if (!_target.IsWindows) RemoveMutexDir();

		var updateLog = UpdateLog.PathIn(_target.DataDir);
		foreach (var file in new[] { InstallScopeFile.PathIn(_target.DataDir), UpdateCheck.PathIn(_target.DataDir), updateLog, updateLog + ".1" })
		{
			if (!File.Exists(file)) continue;
			try
			{
				File.Delete(file);
				Console.WriteLine($"  Removed {Path.GetFileName(file)}");
			}
			catch (Exception ex) { _remaining.Add($"{file} ({ex.Message})"); }
		}

		if (!Directory.Exists(_target.DataDir)) return;
		IReadOnlyList<string> left;
		try
		{
			var entries = Directory.EnumerateFileSystemEntries(_target.DataDir).Select(e => Path.GetFileName(e)!).ToArray();
			left = UninstallPlanner.DataDirLeftovers(entries, _keptShared);
			if (entries.Length == 0) { RemoveEmptyDataDir(); return; }
			if (left.Count == 0) { _dataDirEmptyButLock = true; return; }
		}
		catch (Exception ex)
		{
			_remaining.Add($"{_target.DataDir} ({ex.Message})");
			return;
		}

		// Something else is left: name it unless it is already accounted for.
		var accounted = _remaining.Count > 0 || _deferred;
		if (!accounted)
			_remaining.Add($"{_target.DataDir} still contains: {string.Join(", ", left)}");
	}

	// Shared entries of the data directory this pass kept and reported (in use
	// by another process): not part of the verdict.
	private readonly List<string> _keptShared = new();

	// Linux: the user-scope script mutex lock files. Shared by every SeaShell
	// version this account runs scripts with, so a held one stays (reported,
	// not a failure); otherwise the directory goes. Root's lives under its home,
	// outside /var/lib/seashell, and its emptied parent goes too.
	private void RemoveMutexDir()
	{
		var dir = SeaShellPaths.UserMutexDir();
		if (!Directory.Exists(dir)) return;

		var parent = Path.GetDirectoryName(dir)!;
		var inDataDir = SamePath(parent, _target.DataDir);
		var result = MutexDirRemoval.Remove(dir);
		if (result.DirRemoved)
		{
			Console.WriteLine($"  Removed {(inDataDir ? Path.GetFileName(dir) : dir)}");
			if (!inDataDir)
			{
				try
				{
					if (!Directory.EnumerateFileSystemEntries(parent).Any())
					{
						Directory.Delete(parent, false);
						Console.WriteLine($"  Removed {parent}");
					}
				}
				catch { /* not ours to insist on: an empty directory at most */ }
			}
			return;
		}

		var why = result.Held > 0
			? $"{result.Held} lock file(s) held by running scripts (of this or another SeaShell version)"
			: $"could not remove it: {result.Error}";
		Console.WriteLine($"  Kept {dir}: {why}; harmless, it is shared by every SeaShell version of this account");
		if (inDataDir) _keptShared.Add(Path.GetFileName(dir));
	}

	private static bool SamePath(string a, string b) =>
		string.Equals(Path.GetFullPath(a).TrimEnd('/'), Path.GetFullPath(b).TrimEnd('/'), StringComparison.Ordinal);

	// Set when only this pass's install.lock is left in the data directory.
	private bool _dataDirEmptyButLock;

	private void RemoveEmptyDataDir()
	{
		try
		{
			if (Directory.Exists(_target.DataDir) && !Directory.EnumerateFileSystemEntries(_target.DataDir).Any())
			{
				Directory.Delete(_target.DataDir, false);
				Console.WriteLine($"  Removed {_target.DataDir}");
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"  ERROR: could not remove {_target.DataDir}: {ex.Message}");
			_remaining.Add($"{_target.DataDir} ({ex.Message})");
		}
	}

	private const string RemovingMarker = VersionDirRemoval.RemovingMarker;

	/// <summary>Remove one version directory (see <see cref="VersionDirRemoval"/>).</summary>
	private void RemoveVersionDir(string label, string dir)
	{
		var removal = VersionDirRemoval.Remove(dir, _options.RelocatedFrom);
		// A directory that has to stay is no longer installed: drop its markers,
		// as the old-version cleanup does, so that nothing finds it as an install
		// and the deferred removal can tell it from a reinstall (which writes
		// daemon.path again). Markers are never mapped by a running process.
		if (removal.Result is VersionDirRemovalResult.HeldByExpected or VersionDirRemovalResult.InUse)
		{
			MarkerFile.Delete(dir, MarkerFile.DaemonPath);
			MarkerFile.Delete(dir, MarkerFile.ElevatorPath);
		}
		ReportRemoval(label, dir, removal);
	}

	private void ReportRemoval(string label, string dir, (VersionDirRemovalResult Result, string Detail) removal)
	{
		var (result, detail) = removal;
		switch (result)
		{
			case VersionDirRemovalResult.Removed:
				Console.WriteLine($"  Removed {label}");
				break;
			case VersionDirRemovalResult.HeldByExpected:
				Console.WriteLine($"  {label}: held by the seashell that started this uninstall; removed when it exits");
				_deferred = true;
				break;
			case VersionDirRemovalResult.InUse:
				Console.Error.WriteLine($"  {label}: {detail}; left untouched");
				_remaining.Add($"{label} {dir}: {detail}; left untouched, remove it after that ends");
				break;
			default:
				Console.Error.WriteLine($"  ERROR: could not remove all of {label}: {detail}");
				_remaining.Add($"{label}: {detail}");
				break;
		}
	}

	private void RemoveTree(string dir, string label)
	{
		try
		{
			Directory.Delete(dir, true);
			Console.WriteLine($"  Removed {label}");
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"  ERROR: could not remove all of {dir}: {ex.Message}");
			_remaining.Add($"{dir} ({ex.Message})");
		}
	}

	private static IEnumerable<string> SafeDirectories(string dir, string pattern)
	{
		try { return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : Array.Empty<string>(); }
		catch { return Array.Empty<string>(); }
	}
}
