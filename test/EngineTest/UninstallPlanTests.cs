using System;
using System.IO;
using System.Linq;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class UninstallPlanTests
{
	private static readonly ScopeParts Full = ScopeParts.User | ScopeParts.Elevator | ScopeParts.System;

	private static ScopeResolution Resolve(ScopeRequest request, ScopeParts recorded) =>
		ScopeResolver.Resolve(request, new ScopeFileRead(ScopeFileState.Read, recorded),
			() => new ScopeDetection(PartPresence.Absent, PartPresence.Absent), isWindows: true);

	[Fact]
	public void NoFlags_RemovesTheRememberedScope()
	{
		var resolution = Resolve(new ScopeRequest(), Full);

		Assert.Equal(Full, UninstallPlanner.PartsToRemove(resolution));
		Assert.Equal(ScopeParts.None, UninstallPlanner.LeftInPlace(resolution, Full));
	}

	[Fact]
	public void User_RemovesTheElevatorWithIt_AndLeavesSystem()
	{
		var resolution = Resolve(new ScopeRequest { User = true }, Full);
		var parts = UninstallPlanner.PartsToRemove(resolution);

		Assert.Equal(ScopeParts.User | ScopeParts.Elevator, parts);
		Assert.Equal(ScopeParts.System, UninstallPlanner.LeftInPlace(resolution, parts));
	}

	[Fact]
	public void ScheduledScripts_OnlyThisUsersScripts_NeverSeaShellsOwnTasks()
	{
		var tasks = new[]
		{
			@"\SeaShell\qd - android-backup",
			@"\SeaShell\qd - T3CodeUpdate",
			@"\SeaShell\other - nightly",
			@"\SeaShell\SeaShell Daemon (qd) 0.6.3.9011",
			@"\SeaShell\SeaShell Elevator (qd) 0.6.3.9011",
			@"\SeaShell\SeaShell Update (qd)",
		};

		var scripts = UninstallPlanner.WindowsScripts(tasks, "qd");

		Assert.Equal(new[] { "android-backup", "T3CodeUpdate" }, scripts.Select(s => s.Name));
		Assert.Equal(@"\SeaShell\qd - android-backup", scripts[0].Entry);
	}

	[Fact]
	public void Crontab_ScriptsAreKeptUnlessPurged_AutoUpdateAlwaysGoes()
	{
		var crontab = new[]
		{
			"MAILTO=pl",
			"0 4 * * * cd /home/pl && /home/pl/.local/bin/sea /home/pl/backup.cs # seashell:backup",
			"5 3 * * * HOME=/home/pl /home/pl/.local/share/seashell/current/bin/seashell update --pass user --if-idle --quiet # seashell-auto-update",
			"*/5 * * * * /usr/bin/unrelated",
		};

		Assert.Equal(new[] { "backup" }, UninstallPlanner.CronScripts(crontab).Select(s => s.Name));
		Assert.Equal(new[] { crontab[0], crontab[1], crontab[3] }, UninstallPlanner.CrontabAfter(crontab, purge: false));
		Assert.Equal(new[] { crontab[0], crontab[3] }, UninstallPlanner.CrontabAfter(crontab, purge: true));
	}

	[Fact]
	public void Linux_RootPassRunsBeforeTheUserPassDeletesTheBootstrapper()
	{
		// sudo runs this bootstrapper; as the installed copy, the user pass deletes it.
		Assert.Equal(new[] { ScopeParts.System, ScopeParts.User },
			UninstallPlanner.LinuxPassOrder(ScopeParts.User | ScopeParts.System));
		Assert.Equal(new[] { ScopeParts.User }, UninstallPlanner.LinuxPassOrder(ScopeParts.User));
	}

	[Fact]
	public void DeferredRemoval_KeepsADirAnImmediateReinstallMadeCurrentAgain()
	{
		var v = new Version("0.6.3.9021");

		Assert.Null(UninstallPlanner.KeepDeferredDir(v, current: null, hasDaemonMarker: false));
		Assert.NotNull(UninstallPlanner.KeepDeferredDir(v, current: v, hasDaemonMarker: true));
		Assert.NotNull(UninstallPlanner.KeepDeferredDir(v, current: null, hasDaemonMarker: true));
		Assert.Null(UninstallPlanner.KeepDeferredDir(v, current: new Version("0.6.3.9022"), hasDaemonMarker: false));
	}

	[Fact]
	public void Verdict_IgnoresThePassesOwnLockFile()
	{
		// Linux: install.lock is held by the uninstall itself until it ends.
		Assert.Empty(UninstallPlanner.DataDirLeftovers(new[] { "install.lock" }));
		Assert.Equal(new[] { "notes.txt" }, UninstallPlanner.DataDirLeftovers(new[] { "install.lock", "notes.txt" }));
		// A shared directory the pass kept (in use) and reported is no failure either.
		Assert.Empty(UninstallPlanner.DataDirLeftovers(new[] { "install.lock", "mutex" }, new[] { "mutex" }));
	}

	[Fact]
	public void MutexDir_KeepsALockHeldByARunningScript_AndGoesOnceUnused()
	{
		// Observed in CI: uninstall failed over the leftover user-scope mutex
		// directory. A held lock (a running script, maybe of another version)
		// must survive; unheld leftovers and the directory go.
		var dir = Path.Combine(Path.GetTempPath(), $"seashell-mutexdir-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		try
		{
			var heldPath = Path.Combine(dir, "running.lock");
			var stale = Path.Combine(dir, "stale.lock");
			File.WriteAllText(stale, "");
			// The exclusive lock ScriptMutex takes (flock on Linux).
			using (new FileStream(heldPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
			{
				var kept = MutexDirRemoval.Remove(dir);
				Assert.False(kept.DirRemoved);
				Assert.Equal(1, kept.Held);
				Assert.True(File.Exists(heldPath));
				Assert.False(File.Exists(stale));
			}

			var removed = MutexDirRemoval.Remove(dir);
			Assert.True(removed.DirRemoved);
			Assert.False(Directory.Exists(dir));
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { }
		}
	}
}
