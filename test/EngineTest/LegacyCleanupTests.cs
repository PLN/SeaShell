using System;
using System.IO;
using System.Linq;
using Xunit;

namespace EngineTest;

public class LegacyCleanupTests : IDisposable
{
	private readonly string _dataDir;

	public LegacyCleanupTests()
	{
		_dataDir = Path.Combine(Path.GetTempPath(), $"seashell-legacy-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_dataDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_dataDir, recursive: true); } catch { }
	}

	[Fact]
	public void Sweep_RemovesPre050LegacyDirs()
	{
		foreach (var name in new[] { "bin", "cache", "daemon", "elevator" })
		{
			var dir = Path.Combine(_dataDir, name);
			Directory.CreateDirectory(dir);
			File.WriteAllText(Path.Combine(dir, "probe.txt"), "hello");
		}
		File.WriteAllText(Path.Combine(_dataDir, "seashell.json"), "{}");

		SeaShell.Installer.LegacyCleanup.SweepFilesystem(_dataDir);

		Assert.False(Directory.Exists(Path.Combine(_dataDir, "bin")));
		Assert.False(Directory.Exists(Path.Combine(_dataDir, "cache")));
		Assert.False(Directory.Exists(Path.Combine(_dataDir, "daemon")));
		Assert.False(Directory.Exists(Path.Combine(_dataDir, "elevator")));
		Assert.False(File.Exists(Path.Combine(_dataDir, "seashell.json")));
	}

	[Fact]
	public void Sweep_LeavesVersionDirsUntouched()
	{
		var versionDir = Path.Combine(_dataDir, "v0.5.0.999");
		Directory.CreateDirectory(Path.Combine(versionDir, "bin"));
		File.WriteAllText(Path.Combine(versionDir, "daemon.path"), "whatever");

		// Legacy dir present alongside:
		Directory.CreateDirectory(Path.Combine(_dataDir, "bin"));

		SeaShell.Installer.LegacyCleanup.SweepFilesystem(_dataDir);

		Assert.True(Directory.Exists(versionDir));
		Assert.True(Directory.Exists(Path.Combine(versionDir, "bin")));
		Assert.True(File.Exists(Path.Combine(versionDir, "daemon.path")));
		Assert.False(Directory.Exists(Path.Combine(_dataDir, "bin")));
	}

	[Fact]
	public void Sweep_LatentQuarantineFromPriorInstall_GetsCleaned()
	{
		// Simulate a prior install that renamed bin → bin.remove-1713000000
		// but failed to delete it (the lock has since released).
		var quarantine = Path.Combine(_dataDir, "bin.remove-1713000000");
		Directory.CreateDirectory(quarantine);
		File.WriteAllText(Path.Combine(quarantine, "sea.exe"), "fake");

		SeaShell.Installer.LegacyCleanup.SweepFilesystem(_dataDir);

		Assert.False(Directory.Exists(quarantine));
	}

	[Fact]
	public void Sweep_UnrelatedQuarantinePattern_LeftAlone()
	{
		// A dir ending in '.remove-N' but with a non-legacy prefix is not ours.
		var foreign = Path.Combine(_dataDir, "foo.remove-123");
		Directory.CreateDirectory(foreign);

		SeaShell.Installer.LegacyCleanup.SweepFilesystem(_dataDir);

		Assert.True(Directory.Exists(foreign));
	}

	[Fact]
	public void Sweep_OnlyLegacyBin_OtherDirsAbsent_SucceedsSilently()
	{
		Directory.CreateDirectory(Path.Combine(_dataDir, "bin"));
		SeaShell.Installer.LegacyCleanup.SweepFilesystem(_dataDir);
		Assert.False(Directory.Exists(Path.Combine(_dataDir, "bin")));
	}

	[Fact]
	public void Sweep_NonexistentDataDir_NoThrow()
	{
		var ghost = Path.Combine(Path.GetTempPath(), $"seashell-ghost-{Guid.NewGuid():N}");
		// Should return quietly with nothing to do.
		SeaShell.Installer.LegacyCleanup.SweepFilesystem(ghost);
	}

	[Fact]
	public void Sweep_LogsRemovalActions()
	{
		Directory.CreateDirectory(Path.Combine(_dataDir, "bin"));
		File.WriteAllText(Path.Combine(_dataDir, "seashell.json"), "{}");

		var messages = new System.Collections.Generic.List<string>();
		SeaShell.Installer.LegacyCleanup.SweepFilesystem(_dataDir, messages.Add);

		Assert.Contains(messages, m => m.Contains("bin", StringComparison.Ordinal));
		Assert.Contains(messages, m => m.Contains("seashell.json", StringComparison.Ordinal));
	}

	[Fact]
	public void Sweep_ReparsePointsAreNotRenamed()
	{
		// A reparse point at $DataDir/bin would be the (unlikely) case where the
		// new 'current' junction layout intersects with a legacy name. The sweep
		// must not touch reparse points.
		if (!OperatingSystem.IsWindows()) return; // Windows-only via mklink

		var target = Path.Combine(_dataDir, "target");
		Directory.CreateDirectory(target);
		var link = Path.Combine(_dataDir, "bin");
		SeaShell.JunctionHelper.CreateOrUpdate(link, target);

		SeaShell.Installer.LegacyCleanup.SweepFilesystem(_dataDir);

		Assert.True(Directory.Exists(link));
		Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
	}
}
