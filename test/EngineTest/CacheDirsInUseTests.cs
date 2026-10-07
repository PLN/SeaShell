using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using SeaShell.Engine;
using Xunit;

namespace SeaShell.Engine.Tests;

public sealed class CacheDirsInUseTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"seashell-inuse-test-{Guid.NewGuid():N}");

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	[Fact]
	public void MapsPath_IsThePathnameField()
	{
		Assert.Equal("/c/v1/cache/job_aaaaaaaa/job.dll",
			CacheDirsInUse.MapsPath("7f12a000-7f12b000 r--p 00000000 08:01 1234   /c/v1/cache/job_aaaaaaaa/job.dll"));
		Assert.Equal("/c/with space/x.so",
			CacheDirsInUse.MapsPath("7f12a000-7f12b000 r-xp 00001000 08:01 99 /c/with space/x.so"));
		Assert.Null(CacheDirsInUse.MapsPath("7ffd1000-7ffd2000 rw-p 00000000 00:00 0"));
	}

	[Fact]
	public void OnlyPathsStartingWithTheCacheDirCount()
	{
		var found = new HashSet<string>();
		var prefixes = new[] { "/home/u/.local/share/seashell/v1/cache/" };
		CacheDirsInUse.AddIfUnder("/home/u/.local/share/seashell/v1/cache/job_aaaaaaaa/job.dll", prefixes, found);
		CacheDirsInUse.AddIfUnder("/backup/home/u/.local/share/seashell/v1/cache/old_bbbbbbbb/x.dll", prefixes, found);
		CacheDirsInUse.AddIfUnder("/home/u/.local/share/seashell/v1/cache/.tracker.json", prefixes, found);
		Assert.Equal(new[] { "job_aaaaaaaa" }, found);
	}

	[Fact]
	public void Linux_FindsEntriesThroughASymlinkedCacheDir()
	{
		if (!OperatingSystem.IsLinux()) return;

		var real = Path.Combine(_root, "real-cache");
		var entry = Path.Combine(real, "job_aaaaaaaa");
		Directory.CreateDirectory(entry);
		var dll = Path.Combine(entry, "job.dll");
		File.WriteAllBytes(dll, new byte[4096]);
		var link = Path.Combine(_root, "linked-cache");
		Directory.CreateSymbolicLink(link, real);

		// /proc/self/maps shows the resolved path; the cache is configured via the link.
		using var map = MemoryMappedFile.CreateFromFile(Path.Combine(link, "job_aaaaaaaa", "job.dll"), FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
		using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
		Assert.Contains("job_aaaaaaaa", CacheDirsInUse.Find(link));
	}

	[Fact]
	public void Linux_FindsEntriesNamedOnAProcessCommandLine()
	{
		if (!OperatingSystem.IsLinux()) return;

		var cache = Path.Combine(_root, "cache");
		Directory.CreateDirectory(Path.Combine(cache, "svc_cccccccc"));
		// Like `dotnet exec <entry>/svc.dll`, readable even when the process's maps are not.
		// The trailing ":" keeps BusyBox sh from exec'ing sleep, which would drop the path.
		var psi = new ProcessStartInfo("sh") { ArgumentList = { "-c", "sleep 10; :", Path.Combine(cache, "svc_cccccccc", "svc.dll") } };
		using var proc = Process.Start(psi)!;
		try
		{
			// Until the child has exec'd, its cmdline is still ours.
			var found = CacheDirsInUse.Find(cache);
			for (var i = 0; i < 50 && !found.Contains("svc_cccccccc"); i++)
			{
				System.Threading.Thread.Sleep(100);
				found = CacheDirsInUse.Find(cache);
			}
			Assert.Contains("svc_cccccccc", found);
		}
		finally
		{
			proc.Kill();
		}
	}
}
