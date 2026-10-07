using System;
using System.IO;
using System.Threading.Tasks;
using SeaShell.Engine;
using Xunit;

namespace SeaShell.Engine.Tests;

public sealed class CacheTrackerTests : IDisposable
{
	private readonly string _cacheDir;
	private DateTime _now = DateTime.UtcNow;

	public CacheTrackerTests()
	{
		_cacheDir = Path.Combine(Path.GetTempPath(), $"seashell-tracker-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_cacheDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_cacheDir, recursive: true); } catch { }
	}

	private CacheTracker NewTracker() => new(_cacheDir, () => _now);

	private string MakeEntry(CacheTracker tracker, string key = "job_aaaaaaaa")
	{
		var dir = Path.Combine(_cacheDir, key);
		var existed = Directory.Exists(dir);
		Directory.CreateDirectory(dir);
		tracker.RecordCreated(key, "/scripts/job.cs", dirWasAbsent: !existed);
		return key;
	}

	/// <summary>What ScriptCompiler.SweepCache does for each candidate.</summary>
	private void Sweep(CacheTracker tracker)
	{
		foreach (var key in tracker.SweepCandidates())
		{
			if (!tracker.ShouldDelete(key)) continue;
			Directory.Delete(Path.Combine(_cacheDir, key), recursive: true);
			tracker.RecordRemoved(key, explicitClear: false);
		}
	}

	private bool Exists(string key) => Directory.Exists(Path.Combine(_cacheDir, key));

	[Fact]
	public void NewEntry_ExpiresOneDayAfterLastHit()
	{
		var tracker = NewTracker();
		var key = MakeEntry(tracker);

		_now += TimeSpan.FromHours(23);
		Assert.Empty(tracker.SweepCandidates());
		tracker.RecordHit(key, null);

		_now += TimeSpan.FromHours(23);
		Assert.Empty(tracker.SweepCandidates());

		_now += TimeSpan.FromHours(2);
		Assert.Equal(new[] { key }, tracker.SweepCandidates());
	}

	[Fact]
	public void EntryRecreatedAfterSweep_IsPromotedToThirtyDays()
	{
		var tracker = NewTracker();
		var key = MakeEntry(tracker);

		_now += TimeSpan.FromHours(25);
		Sweep(tracker);
		Assert.False(Exists(key));
		Assert.NotNull(tracker.Get(key)); // record outlives the dir

		_now += TimeSpan.FromDays(7); // weekly job comes back
		MakeEntry(tracker, key);
		Assert.True(tracker.Get(key)!.Promoted);

		_now += TimeSpan.FromDays(29);
		Sweep(tracker);
		Assert.True(Exists(key));

		_now += TimeSpan.FromDays(2);
		Sweep(tracker);
		Assert.False(Exists(key));
	}

	[Fact]
	public void EntryRecreatedAfterThirtyDays_IsFresh()
	{
		var tracker = NewTracker();
		var key = MakeEntry(tracker);
		_now += TimeSpan.FromHours(25);
		Sweep(tracker);

		_now += TimeSpan.FromDays(30);
		Sweep(tracker);
		Assert.Null(tracker.Get(key)); // record dropped 30 days after the last hit

		MakeEntry(tracker, key);
		Assert.False(tracker.Get(key)!.Promoted);
	}

	[Fact]
	public void ExplicitClear_KeepsPreviousKeepPeriod()
	{
		var tracker = NewTracker();
		var key = MakeEntry(tracker);

		Directory.Delete(Path.Combine(_cacheDir, key), recursive: true);
		tracker.RecordRemoved(key, explicitClear: true);
		MakeEntry(tracker, key);
		Assert.False(tracker.Get(key)!.Promoted);

		// A promoted entry stays promoted through a clear.
		_now += TimeSpan.FromHours(25);
		Sweep(tracker);
		MakeEntry(tracker, key);
		Directory.Delete(Path.Combine(_cacheDir, key), recursive: true);
		tracker.RecordRemoved(key, explicitClear: true);
		MakeEntry(tracker, key);
		Assert.True(tracker.Get(key)!.Promoted);
	}

	[Fact]
	public void RepairingAPresentEntry_DoesNotPromote()
	{
		var tracker = NewTracker();
		var key = MakeEntry(tracker);
		_now += TimeSpan.FromHours(1);
		tracker.RecordCreated(key, null, dirWasAbsent: false); // half-staged dir recompiled in place
		Assert.False(tracker.Get(key)!.Promoted);
	}

	[Fact]
	public void PromotedEntryIdleThirtyDays_ComesBackFresh()
	{
		var tracker = NewTracker();
		var key = MakeEntry(tracker);
		_now += TimeSpan.FromHours(25);
		Sweep(tracker);
		_now += TimeSpan.FromDays(1);
		MakeEntry(tracker, key);
		Assert.True(tracker.Get(key)!.Promoted);

		_now += TimeSpan.FromDays(31);
		Sweep(tracker);                 // deleted; the record is pruned only next sweep
		Assert.NotNull(tracker.Get(key));
		MakeEntry(tracker, key);        // back before that: history too old to count
		Assert.False(tracker.Get(key)!.Promoted);
	}

	[Fact]
	public void EntryDeletedBehindOurBack_PromotesWhenRecreated()
	{
		var tracker = NewTracker();
		var key = MakeEntry(tracker);
		Directory.Delete(Path.Combine(_cacheDir, key), recursive: true); // no sweep saw it go

		_now += TimeSpan.FromDays(2);
		MakeEntry(tracker, key);
		Assert.True(tracker.Get(key)!.Promoted);
	}

	[Fact]
	public void HitOnASurvivingRemovedDir_ClearsTheRemoval()
	{
		var tracker = NewTracker();
		var key = MakeEntry(tracker);
		tracker.RecordRemoved(key, explicitClear: true); // a clear stopped by a locked file
		Assert.True(tracker.ShouldDelete(key));

		tracker.RecordHit(key, null);
		Assert.False(tracker.ShouldDelete(key));
	}

	[Fact]
	public void RemovalMarkOnAPresentFreshDir_DoesNotDelete()
	{
		// e.g. a sweep's directory listing taken just before a compile created the dir
		var tracker = NewTracker();
		var key = MakeEntry(tracker);
		tracker.RecordRemoved(key, explicitClear: false);

		Assert.False(tracker.ShouldDelete(key));
		Assert.Empty(tracker.SweepCandidates());
	}

	[Fact]
	public void Linux_CaseDistinctEntriesAreTrackedSeparately()
	{
		if (!OperatingSystem.IsLinux()) return; // one and the same dir on Windows

		var tracker = NewTracker();
		var upper = MakeEntry(tracker, "Job_aaaaaaaa");
		var lower = MakeEntry(tracker, "job_aaaaaaaa");
		Assert.False(tracker.Get(lower)!.Promoted);

		_now += TimeSpan.FromHours(25);
		Sweep(tracker);
		Assert.False(Exists(upper));
		Assert.False(Exists(lower));
	}

	[Fact]
	public void DotPrefixedScriptEntries_ExpireLikeAnyOther()
	{
		var tracker = NewTracker();
		var key = MakeEntry(tracker, ".nightly_aaaaaaaa");
		_now += TimeSpan.FromHours(25);
		Assert.Equal(new[] { key }, tracker.SweepCandidates());
	}

	[Fact]
	public async Task ConcurrentSaves_AllCompleteWithTheLatestState()
	{
		var tracker = NewTracker();
		MakeEntry(tracker, "a_11111111");
		var first = tracker.SaveAsync();
		MakeEntry(tracker, "b_22222222");
		await Task.WhenAll(first, tracker.SaveAsync());

		var reloaded = NewTracker();
		Assert.NotNull(reloaded.Get("a_11111111"));
		Assert.NotNull(reloaded.Get("b_22222222"));
	}

	[Fact]
	public void KeepAlive_ExtendsRunningEntry_AndRejectsUnknownDirs()
	{
		var tracker = NewTracker();
		var key = MakeEntry(tracker);

		for (var i = 0; i < 5; i++)
		{
			_now += TimeSpan.FromHours(20);
			Assert.True(tracker.Touch(key));
			Sweep(tracker);
		}
		Assert.True(Exists(key)); // 100 hours, never idle for 24

		Assert.False(tracker.Touch("missing_bbbbbbbb"));
	}

	[Fact]
	public void UntrackedDirs_AreAdoptedFromFileTimes()
	{
		var old = Path.Combine(_cacheDir, "old_cccccccc");
		var recent = Path.Combine(_cacheDir, "recent_dddddddd");
		Directory.CreateDirectory(old);
		Directory.CreateDirectory(recent);
		Directory.SetLastWriteTimeUtc(old, _now - TimeSpan.FromDays(2));

		var tracker = NewTracker();
		Assert.Equal(new[] { "old_cccccccc" }, tracker.SweepCandidates());
	}

	[Fact]
	public async Task SaveAndReload_RoundTrips_CorruptFileStartsFresh()
	{
		var tracker = NewTracker();
		var key = MakeEntry(tracker);
		_now += TimeSpan.FromHours(25);
		Sweep(tracker);
		_now += TimeSpan.FromDays(1);
		MakeEntry(tracker, key);
		await tracker.SaveAsync();

		var reloaded = NewTracker().Get(key)!;
		Assert.True(reloaded.Promoted);
		Assert.Equal(2, reloaded.Hits);
		Assert.Equal("/scripts/job.cs", reloaded.Script);

		File.WriteAllText(Path.Combine(_cacheDir, CacheTracker.FileName), "{ not json");
		Directory.SetLastWriteTimeUtc(Path.Combine(_cacheDir, key), _now); // fake clock runs ahead of the disk
		var fresh = NewTracker();
		Assert.Null(fresh.Get(key));
		Assert.Empty(fresh.SweepCandidates()); // dir re-adopted, recently written
		Assert.NotNull(fresh.Get(key));
	}

	[Fact]
	public void ScriptCompiler_KeepsExpiredEntryWhileAProcessHasItsFilesMapped()
	{
		var scriptDir = Path.Combine(_cacheDir, "..", $"seashell-tracker-script-{Guid.NewGuid():N}");
		Directory.CreateDirectory(scriptDir);
		try
		{
			var script = Path.Combine(scriptDir, "service.cs");
			File.WriteAllText(script, "Console.WriteLine(\"service\");\n");
			var compiler = new ScriptCompiler(_cacheDir, () => _now);
			var compiled = compiler.Compile(script, Array.Empty<string>());
			Assert.True(compiled.Success, compiled.Error);
			var outputDir = Path.GetDirectoryName(compiled.RuntimeConfigPath)!;
			var dll = Path.Combine(outputDir, "service.dll");

			// A running script whose invoker sent no keep-alive: its assembly is mapped,
			// as the runtime loader does (Linux: /proc/self/maps; Windows: file in use).
			_now += TimeSpan.FromHours(25);
			using (var map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
				dll, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read))
			using (map.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read))
			{
				Assert.Equal(0, compiler.SweepCache());
				Assert.True(File.Exists(compiled.ManifestPath)); // nothing deleted from under it
			}

			_now += TimeSpan.FromHours(25); // the in-use check counted as a hit
			Assert.Equal(1, compiler.SweepCache());
			Assert.False(Directory.Exists(outputDir));
		}
		finally
		{
			try { Directory.Delete(scriptDir, recursive: true); } catch { }
		}
	}

	[Fact]
	public void ScriptCompiler_SweepsExpiredEntry_AndPromotesItsRecreation()
	{
		var scriptDir = Path.Combine(_cacheDir, "..", $"seashell-tracker-script-{Guid.NewGuid():N}");
		Directory.CreateDirectory(scriptDir);
		try
		{
			var script = Path.Combine(scriptDir, "weekly.cs");
			File.WriteAllText(script, "Console.WriteLine(\"weekly\");\n");
			var compiler = new ScriptCompiler(_cacheDir, () => _now);

			var first = compiler.Compile(script, Array.Empty<string>());
			Assert.True(first.Success, first.Error);
			var outputDir = Path.GetDirectoryName(first.RuntimeConfigPath)!;
			var key = Path.GetFileName(outputDir);
			Assert.True(compiler.TouchCache(outputDir));
			Assert.True(compiler.TouchCache(outputDir.Replace('\\', '/') + "/")); // same entry, other spelling
			Assert.False(compiler.TouchCache(scriptDir));

			_now += TimeSpan.FromHours(25);
			Assert.Equal(1, compiler.SweepCache());
			Assert.False(Directory.Exists(outputDir));

			_now += TimeSpan.FromDays(7);
			var again = compiler.Compile(script, Array.Empty<string>());
			Assert.True(again.Success, again.Error);
			Assert.True(Directory.Exists(outputDir));
			Assert.True(compiler.Tracker.Get(key)!.Promoted);
		}
		finally
		{
			try { Directory.Delete(scriptDir, recursive: true); } catch { }
		}
	}
}
