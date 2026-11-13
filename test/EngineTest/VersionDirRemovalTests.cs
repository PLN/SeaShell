using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SeaShell;
using SeaShell.Bootstrapper;
using Xunit;

namespace EngineTest;

/// <summary>
/// The installer's removal of a whole version directory (old-version cleanup,
/// uninstall, the deferred removal after a relocated uninstall). Reproduces the
/// review finding: a copied native exe kept running from the directory, the
/// rename succeeded (a mapped image holds no handle), an unlocked companion file
/// was deleted under it, result "partly removed".
/// </summary>
public sealed class VersionDirRemovalTests : IDisposable
{
	private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"seashell-removal-test-{Guid.NewGuid():N}");

	public void Dispose()
	{
		for (var i = 0; i < 20 && Directory.Exists(_dataDir); i++)
		{
			try { Directory.Delete(_dataDir, recursive: true); }
			catch { Thread.Sleep(50); }
		}
	}

	[Fact]
	public void DirWithRunningExe_IsLeftCompletelyIntact_AndRemovedOnceItExits()
	{
		var dir = VersionDirWithRunningCopy(out var proc);
		try
		{
			var before = Snapshot(dir);
			var (result, detail) = VersionDirRemoval.Remove(dir);

			Assert.Equal(VersionDirRemovalResult.InUse, result);
			Assert.NotEmpty(detail);
			Assert.Equal(before, Snapshot(dir));   // the companion files too
			Assert.Empty(Directory.GetDirectories(_dataDir, "*" + VersionDirRemoval.RemovingMarker + "*"));
		}
		finally
		{
			Stop(proc);
		}

		// Image sections and scanners may let go a moment after the process exits.
		var last = VersionDirRemovalResult.InUse;
		for (var i = 0; i < 50 && Directory.Exists(dir); i++)
		{
			if (i > 0) Thread.Sleep(100);
			last = VersionDirRemoval.Remove(dir).Result;
		}
		Assert.Equal(VersionDirRemovalResult.Removed, last);
		Assert.Empty(Directory.GetFileSystemEntries(_dataDir));
	}

	[Fact]
	public void LeftoverInUse_IsNotDeleted()
	{
		// A `.removing-` directory from an earlier attempt that something runs from again.
		var dir = VersionDirWithRunningCopy(out var proc);
		var leftover = dir + VersionDirRemoval.RemovingMarker + "20261112120000000";
		try
		{
			Stop(proc);
			for (var i = 0; i < 50; i++)
			{
				try { Directory.Move(dir, leftover); break; }
				catch (IOException) { Thread.Sleep(100); }
			}
			proc = StartIdle(Path.Combine(leftover, "bin", ExeName));
			WaitUntilVisible(leftover);

			var before = Snapshot(leftover);
			Assert.Equal(VersionDirRemovalResult.InUse, VersionDirRemoval.RemoveLeftover(leftover).Result);
			Assert.Equal(before, Snapshot(leftover));
		}
		finally
		{
			Stop(proc);
		}
	}

	private static string ExeName => OperatingSystem.IsWindows() ? "ping.exe" : "sleep";

	/// <summary>v0.6.3.1 with a running copy of a native exe in bin/ and unlocked companion files.</summary>
	private string VersionDirWithRunningCopy(out Process proc)
	{
		var dir = SeaShellPaths.VersionDirIn(_dataDir, "0.6.3.1");
		Directory.CreateDirectory(Path.Combine(dir, "bin"));
		File.WriteAllText(Path.Combine(dir, "bin", "companion.dll"), "not loaded by anyone");
		File.WriteAllText(Path.Combine(dir, "bin", "seashell.deps.json"), "{}");
		var exe = Path.Combine(dir, "bin", ExeName);
		File.Copy(OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "PING.EXE") : "/bin/sleep", exe);
		proc = StartIdle(exe);
		WaitUntilVisible(dir);
		return dir;
	}

	// Linux: until the child has exec'd it maps nothing from the dir. Windows: the
	// loader and scanners briefly hold handles on a freshly started file; wait until
	// only the mapped image is left, as with a process that has run for a while.
	private static void WaitUntilVisible(string dir)
	{
		for (var i = 0; OperatingSystem.IsLinux() && i < 50 && DirectoryInUse.Find(dir) == null; i++)
			Thread.Sleep(100);
		for (var i = 0; OperatingSystem.IsWindows() && i < 100 && !NoHandleOpen(dir); i++)
			Thread.Sleep(100);
	}

	private static bool NoHandleOpen(string dir)
	{
		try
		{
			foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
				new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
			return true;
		}
		catch (IOException) { return false; }
	}

	private static string[] Snapshot(string dir) =>
		Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories)
			.Select(p => Path.GetRelativePath(dir, p))
			.Order(StringComparer.Ordinal)
			.ToArray();

	private static Process StartIdle(string exe)
	{
		var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true };
		foreach (var arg in OperatingSystem.IsWindows() ? new[] { "-n", "60", "127.0.0.1" } : new[] { "60" })
			psi.ArgumentList.Add(arg);
		return Process.Start(psi)!;
	}

	private static void Stop(Process proc)
	{
		try { proc.Kill(); } catch (InvalidOperationException) { }
		proc.WaitForExit();
		proc.Dispose();
	}
}
