using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using SeaShell;
using SeaShell.Engine;
using Xunit;
using Outcome = SeaShell.Engine.RetiredVersionSweeper.Outcome;

namespace EngineTest;

public sealed class RetiredVersionSweeperTests : IDisposable
{
	private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"seashell-retired-test-{Guid.NewGuid():N}");

	public void Dispose()
	{
		// Windows: the top dir can still count as non-empty while deletes of its entries are pending.
		for (var i = 0; i < 20 && Directory.Exists(_dataDir); i++)
		{
			try { Directory.Delete(_dataDir, recursive: true); }
			catch { Thread.Sleep(50); }
		}
	}

	/// <summary>A version dir with an apphost-like file under bin/ and a cache entry.</summary>
	private string VersionDir(string version, bool installed = false)
	{
		var dir = SeaShellPaths.VersionDirIn(_dataDir, version);
		Directory.CreateDirectory(Path.Combine(dir, "bin"));
		Directory.CreateDirectory(Path.Combine(dir, "cache", "job_aaaaaaaa"));
		File.WriteAllBytes(Path.Combine(dir, "bin", "seaw.dll"), new byte[4096]);
		File.WriteAllBytes(Path.Combine(dir, "cache", "job_aaaaaaaa", "job.dll"), new byte[4096]);
		if (installed)
			MarkerFile.Write(dir, MarkerFile.DaemonPath, Path.Combine(dir, "bin", "seashell-daemon"));
		return dir;
	}

	private void PointCurrentAt(string version) =>
		JunctionHelper.CreateOrUpdate(SeaShellPaths.CurrentDirIn(_dataDir), SeaShellPaths.VersionDirIn(_dataDir, version));

	[Fact]
	public void RetiredIdleDir_IsRemoved_CurrentNewerAndInstalledAreKept()
	{
		var retired = VersionDir("0.6.2.279");
		var installed = VersionDir("0.6.2.200", installed: true);
		var current = VersionDir("0.6.2.283", installed: true);
		var newer = VersionDir("0.6.3.1");
		PointCurrentAt("0.6.2.283");

		var results = RetiredVersionSweeper.Sweep(_dataDir, ownVersion: new Version("0.6.2.283"));

		Assert.False(Directory.Exists(retired));
		Assert.True(Directory.Exists(installed));
		Assert.True(Directory.Exists(current));
		Assert.True(Directory.Exists(newer));
		Assert.Equal(Outcome.Removed, results.Single(e => e.Version == new Version("0.6.2.279")).Outcome);
		Assert.Equal(Outcome.SkippedInstalled, results.Single(e => e.Version == new Version("0.6.2.200")).Outcome);
		// Only versions older than current are candidates at all.
		Assert.DoesNotContain(results, e => e.Version >= new Version("0.6.2.283"));
		// The trash dir is gone again and never looks like a version.
		Assert.False(Directory.Exists(Path.Combine(_dataDir, RetiredVersionSweeper.TrashDirName)));
	}

	/// <summary>
	/// The ways a process uses a retired dir. A running exe and a loaded assembly are
	/// mapped images on Windows: their processes hold no handle, so renaming the dir
	/// succeeds and the delete then removes everything but the images.
	/// </summary>
	[Theory]
	[InlineData("exe")]      // e.g. an old `sea` launcher still running
	[InlineData("assembly")] // e.g. a script or older daemon with a dll loaded from the dir
	[InlineData("cwd")]      // a process working inside the dir
	public void RetiredDirInUse_IsLeftIntact_AndRemovedOnceUnused(string use)
	{
		var retired = VersionDir("0.6.2.279");
		VersionDir("0.6.2.283", installed: true);
		PointCurrentAt("0.6.2.283");
		var entry = Path.Combine(retired, "cache", "job_aaaaaaaa");
		var own = new Version("0.6.2.283");

		Action release;
		switch (use)
		{
			case "exe":
			{
				var exe = Path.Combine(retired, "bin", OperatingSystem.IsWindows() ? "ping.exe" : "sleep");
				File.Copy(OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "PING.EXE") : "/bin/sleep", exe);
				var proc = StartIdle(exe, workingDirectory: null);
				release = () => Stop(proc);
				break;
			}
			case "assembly":
			{
				var dll = Path.Combine(entry, "loaded.dll");
				File.Copy(typeof(MarkerFile).Assembly.Location, dll);
				// Held (not unloaded) until release: a context that unloads early lets a
				// GC elsewhere in the run unmap the file before the in-use sweep.
				var (loaded, alc) = LoadCollectible(dll);
				release = () =>
				{
					Unload(loaded);
					// The context's weak reference dies before the runtime unmaps the file
					// (Linux, seen in CI: still mapped 5 s later with no further GC), so
					// collect until the mapping is gone too.
					for (var i = 0; i < 50 && (alc.IsAlive || MapsUnder(_dataDir)); i++)
					{
						if (i > 0) Thread.Sleep(100);
						GC.Collect();
						GC.WaitForPendingFinalizers();
					}
					Assert.False(alc.IsAlive, "the load context did not unload");
					Assert.False(MapsUnder(_dataDir), "the runtime still maps the unloaded assembly");
				};
				break;
			}
			default:
			{
				var exe = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "PING.EXE") : "/bin/sleep";
				var proc = StartIdle(exe, workingDirectory: entry);
				release = () => Stop(proc);
				break;
			}
		}

		try
		{
			// Linux: until the child has exec'd, it maps nothing from the dir yet.
			for (var i = 0; OperatingSystem.IsLinux() && i < 50 && !CacheDirsInUse.Find(_dataDir, includeCwd: true).Contains("v0.6.2.279"); i++)
				Thread.Sleep(100);
			// Windows: the loader and scanners briefly hold handles on freshly written and
			// started files, which would make even a plain rename fail. Wait until only the
			// mapped images are left, as with a process that has been running for a while.
			for (var i = 0; OperatingSystem.IsWindows() && i < 100 && !NoFileOpen(retired); i++)
				Thread.Sleep(100);

			var before = Snapshot(retired);
			var result = RetiredVersionSweeper.Sweep(_dataDir, own).Single();

			Assert.Equal(Outcome.SkippedInUse, result.Outcome);
			Assert.Equal(before, Snapshot(retired));
			Assert.False(Directory.Exists(Path.Combine(_dataDir, RetiredVersionSweeper.TrashDirName)));
		}
		finally
		{
			release();
		}

		// A later sweep, once nothing uses it, removes it. Image sections and scanners
		// may let go of the files a moment after the process exits.
		for (var i = 0; i < 50 && Directory.Exists(retired); i++)
		{
			if (i > 0) Thread.Sleep(100);
			RetiredVersionSweeper.Sweep(_dataDir, own);
		}
		Assert.False(Directory.Exists(retired));
		Assert.False(Directory.Exists(Path.Combine(_dataDir, RetiredVersionSweeper.TrashDirName)));
	}

	private static string[] Snapshot(string dir) =>
		Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories)
			.Select(p => Path.GetRelativePath(dir, p))
			.Order(StringComparer.Ordinal)
			.ToArray();

	/// <summary>A process that idles for a minute (killed by the test).</summary>
	private static Process StartIdle(string exe, string? workingDirectory)
	{
		var psi = new ProcessStartInfo(exe)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			WorkingDirectory = workingDirectory ?? "",
		};
		foreach (var arg in OperatingSystem.IsWindows() ? new[] { "-n", "60", "127.0.0.1" } : new[] { "60" })
			psi.ArgumentList.Add(arg);
		var proc = Process.Start(psi)!;
		// Windows: Process.Start returns before the child has initialised, and the
		// working directory handle is opened during that: a sweep right away could still
		// rename the dir. Ping's first output line proves it runs with the cwd held. (Not
		// for a copied ping.exe: without its .mui resources it prints nothing. Its image
		// is mapped by CreateProcess already, before Process.Start returns.)
		if (OperatingSystem.IsWindows() && workingDirectory != null && proc.StandardOutput.ReadLine() == null)
			throw new InvalidOperationException($"{exe} exited before writing anything");
		return proc;
	}

	/// <summary>No file under the dir has an open handle (mapped images need none).</summary>
	private static bool NoFileOpen(string dir)
	{
		try
		{
			foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
				new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
			return true;
		}
		catch (IOException) { return false; }
	}

	private static void Stop(Process proc)
	{
		try { proc.Kill(); } catch (InvalidOperationException) { }
		proc.WaitForExit();
		proc.Dispose();
	}

	private sealed class LoadedContext { public AssemblyLoadContext? Context; }

	/// <summary>Load <paramref name="dll"/> into a collectible context, kept loaded until <see cref="Unload"/>.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static (LoadedContext Loaded, WeakReference Weak) LoadCollectible(string dll)
	{
		var alc = new AssemblyLoadContext("retired-sweep-test", isCollectible: true);
		var asm = alc.LoadFromAssemblyPath(dll);
		Assert.Equal(dll, asm.Location);
		return (new LoadedContext { Context = alc }, new WeakReference(alc));
	}

	/// <summary>Start unloading; completes once nothing references the context any more.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void Unload(LoadedContext loaded)
	{
		loaded.Context!.Unload();
		loaded.Context = null;
	}

	/// <summary>Linux: this process maps a file under <paramref name="dir"/> (the test's unique data dir). False elsewhere.</summary>
	private static bool MapsUnder(string dir) =>
		OperatingSystem.IsLinux() && File.ReadAllText("/proc/self/maps").Contains(Path.GetFileName(dir), StringComparison.Ordinal);

	[Fact]
	public void OwnVersion_IsKept_EvenWhenRetired()
	{
		// An old daemon still running after an update must not sweep its own dir.
		var own = VersionDir("0.6.2.279");
		VersionDir("0.6.2.283", installed: true);
		PointCurrentAt("0.6.2.283");

		var results = RetiredVersionSweeper.Sweep(_dataDir, ownVersion: new Version("0.6.2.279"));

		Assert.Equal(Outcome.SkippedOwnVersion, results.Single().Outcome);
		Assert.True(Directory.Exists(own));
	}

	[Fact]
	public void NoCurrentLink_RemovesNothing()
	{
		var old = VersionDir("0.6.2.279");
		VersionDir("0.6.2.283");

		Assert.Empty(RetiredVersionSweeper.Sweep(_dataDir, ownVersion: null));
		Assert.True(Directory.Exists(old));
	}
}
