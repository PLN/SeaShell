using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SeaShell.Engine;

/// <summary>
/// Tracks the compiled-script entries (output dirs) of one version's cache dir: when
/// each was created and last hit, and how long it is kept without a hit.
///
/// <list type="bullet">
/// <item>A new entry is kept for <see cref="FreshKeep"/> after its last hit.</item>
/// <item>An entry that comes back after it was gone (swept, or deleted behind our back)
///   within <see cref="RecordRetention"/> of its last hit is promoted and kept for
///   <see cref="PromotedKeep"/>: a weekly job stays warm, a one-off script is gone after
///   a day. Older history starts over as a new entry.</item>
/// <item>A record outlives its dir until <see cref="RecordRetention"/> after the last hit,
///   which is what lets a recreation be recognised.</item>
/// <item>An explicit clear (<c>clearCache</c>) is not an expiry: recreating keeps the
///   previous keep period. Repairing a dir in place does not promote either.</item>
/// </list>
///
/// The daemon owns the only instance per cache dir and keeps it in memory; <see cref="SaveAsync"/>
/// persists it off the request path. Losing the file is harmless — entries are re-adopted
/// from the dirs on disk and only promotion is forgotten. Callers hold the per-dir compile
/// lock around anything that also changes the dir; this class only guards its own state
/// and never holds its lock across file-system work.
/// </summary>
public sealed class CacheTracker
{
	private static readonly ILogger _log = Log.ForContext<CacheTracker>();

	public static readonly TimeSpan FreshKeep = TimeSpan.FromHours(24);
	public static readonly TimeSpan PromotedKeep = TimeSpan.FromDays(30);
	public static readonly TimeSpan RecordRetention = TimeSpan.FromDays(30);

	internal const string FileName = ".tracker.json";

	/// <summary>
	/// How entry names compare: as the file system does. On Linux "Job_…" and "job_…"
	/// are two dirs, and merging their records would leave one of them unswept.
	/// </summary>
	internal static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	public sealed class Entry
	{
		public string? Script { get; set; }
		public DateTime Created { get; set; }
		public DateTime LastHit { get; set; }
		public long Hits { get; set; }
		public bool Promoted { get; set; }
		/// <summary>When the dir was deleted; null while it exists.</summary>
		public DateTime? Removed { get; set; }
		/// <summary>Removed by an explicit clear rather than by expiry.</summary>
		public bool Cleared { get; set; }

		/// <summary>Tracker mutation count at the entry's last change (in memory only).</summary>
		internal long Generation;

		public Entry Clone() => (Entry)MemberwiseClone();
	}

	private sealed class TrackerFile
	{
		public Dictionary<string, Entry> Entries { get; set; } = new();
	}

	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private readonly string _cacheDir;
	private readonly string _path;
	private readonly Func<DateTime> _clock;
	private readonly object _lock = new();
	private readonly SemaphoreSlim _saveLock = new(1, 1);
	private readonly Dictionary<string, Entry> _entries = new(PathComparer);
	private long _generation;
	private bool _dirty;

	public CacheTracker(string cacheDir, Func<DateTime>? clock = null)
	{
		_cacheDir = cacheDir;
		_path = Path.Combine(cacheDir, FileName);
		_clock = clock ?? (() => DateTime.UtcNow);
		Load();
	}

	public static TimeSpan KeepFor(Entry e) => e.Promoted ? PromotedKeep : FreshKeep;

	/// <summary>Snapshot of one entry, or null.</summary>
	public Entry? Get(string key)
	{
		lock (_lock)
			return _entries.TryGetValue(key, out var e) ? e.Clone() : null;
	}

	/// <summary>
	/// A compile wrote the dir for <paramref name="key"/>. <paramref name="dirWasAbsent"/>:
	/// the dir did not exist before (a recreation); false means it was repaired in place.
	/// </summary>
	public void RecordCreated(string key, string? script, bool dirWasAbsent)
	{
		lock (_lock)
		{
			var now = _clock();
			if (_entries.TryGetValue(key, out var e))
			{
				if (e.Removed != null || dirWasAbsent)
				{
					if (now - e.LastHit > RecordRetention)
						e.Promoted = false;  // history too old: a new entry
					else if (!e.Cleared)
						e.Promoted = true;   // came back while remembered
				}
			}
			else
			{
				e = new Entry();
				_entries[key] = e;
			}

			e.Script = script ?? e.Script;
			e.Created = now;
			Hit(e, now);
		}
	}

	/// <summary>A compile was served from the existing dir.</summary>
	public void RecordHit(string key, string? script)
	{
		var adopted = Adopt(key);
		lock (_lock)
		{
			var e = GetOrAdd(key, adopted);
			e.Script ??= script;
			Hit(e, _clock());
		}
	}

	/// <summary>
	/// The dir is in use (keep-alive from an invoker, or found in use by the sweep).
	/// Returns false when the dir does not exist.
	/// </summary>
	public bool Touch(string key)
	{
		if (!Directory.Exists(Path.Combine(_cacheDir, key)))
			return false;
		var adopted = Adopt(key);
		lock (_lock)
		{
			var e = GetOrAdd(key, adopted);
			e.LastHit = _clock();
			e.Removed = null;
			e.Generation = ++_generation;
			_dirty = true;
			return true;
		}
	}

	/// <summary>The dir is gone, by the sweep or by an explicit clear.</summary>
	public void RecordRemoved(string key, bool explicitClear)
	{
		lock (_lock)
		{
			if (!_entries.TryGetValue(key, out var e)) return;
			if (e.Removed == null)
			{
				e.Removed = _clock();
				e.Cleared = explicitClear;
			}
			else if (explicitClear)
			{
				e.Cleared = true;
			}
			_dirty = true;
		}
	}

	/// <summary>
	/// Reconcile with the disk and return the keys whose dirs the sweep should delete.
	/// Adopts untracked dirs, marks dirs deleted behind our back as removed, and drops
	/// records 30 days after their last hit.
	/// </summary>
	public IReadOnlyList<string> SweepCandidates()
	{
		// The listing is a snapshot: a compile may finish after it. Only records not
		// changed since then are judged by it (a counter, not the clock, which can step).
		long scanGeneration;
		lock (_lock) scanGeneration = _generation;
		var onDisk = ListEntryDirs();
		var unknown = new List<string>();
		lock (_lock)
			unknown.AddRange(onDisk.Where(k => !_entries.ContainsKey(k)));
		var adopted = unknown.ToDictionary(k => k, Adopt, PathComparer);

		lock (_lock)
		{
			var now = _clock();
			foreach (var (key, e) in adopted)
				GetOrAdd(key, e);

			foreach (var (key, e) in _entries)
			{
				if (e.Removed == null && !onDisk.Contains(key) && e.Generation <= scanGeneration)
				{
					e.Removed = now; // deleted behind our back
					_dirty = true;
				}
			}

			var stale = _entries
				.Where(kv => kv.Value.Removed != null && !onDisk.Contains(kv.Key)
					&& now - kv.Value.LastHit > RecordRetention)
				.Select(kv => kv.Key)
				.ToList();
			foreach (var key in stale)
				_entries.Remove(key);
			if (stale.Count > 0) _dirty = true;

			return _entries.Keys.Where(k => onDisk.Contains(k) && IsExpired(_entries[k], now)).ToList();
		}
	}

	/// <summary>Re-check right before deleting; call under the per-dir compile lock.</summary>
	public bool ShouldDelete(string key)
	{
		if (!Directory.Exists(Path.Combine(_cacheDir, key)))
			return false;
		lock (_lock)
			return _entries.TryGetValue(key, out var e) && IsExpired(e, _clock());
	}

	// A dir an explicit clear could not fully delete (the requesting script still held
	// it) is expired too, so the sweep retries it. A removal mark alone is not: the sweep
	// removes dirs atomically, so a marked dir that exists was recreated meanwhile.
	private static bool IsExpired(Entry e, DateTime now) =>
		(e.Removed != null && e.Cleared) || now - e.LastHit > KeepFor(e);

	/// <summary>
	/// Persist if anything changed. Saves are serialized, so a shutdown save waits for one
	/// already in flight instead of returning before it is on disk.
	/// </summary>
	public async Task SaveAsync()
	{
		await _saveLock.WaitAsync().ConfigureAwait(false);
		try
		{
			Dictionary<string, Entry> snapshot;
			lock (_lock)
			{
				if (!_dirty) return;
				snapshot = _entries.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), PathComparer);
				_dirty = false;
			}

			try
			{
				var json = JsonSerializer.Serialize(new TrackerFile { Entries = snapshot }, JsonOpts);
				var tmp = _path + ".tmp";
				await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
				File.Move(tmp, _path, overwrite: true);
			}
			catch (Exception ex)
			{
				_log.Warning(ex, "Could not save cache tracker {Path}", _path);
				lock (_lock) _dirty = true; // retry on the next save
			}
		}
		finally
		{
			_saveLock.Release();
		}
	}

	private void Hit(Entry e, DateTime now)
	{
		e.Generation = ++_generation;
		e.LastHit = now;
		e.Hits++;
		e.Removed = null;  // a hit means the dir is there and in use
		e.Cleared = false;
		_dirty = true;
	}

	private Entry GetOrAdd(string key, Entry adopted)
	{
		if (_entries.TryGetValue(key, out var e)) return e;
		adopted.Generation = ++_generation;
		_entries[key] = adopted;
		_dirty = true;
		return adopted;
	}

	/// <summary>
	/// Best guess for a dir the tracker never saw (tracker file lost, or written by an
	/// older build): created and last used per the file system. Reads file times, so
	/// called outside the lock; the result is discarded when the key is already known.
	/// </summary>
	private Entry Adopt(string key)
	{
		var now = _clock();
		var dir = Path.Combine(_cacheDir, key);
		try
		{
			var created = Directory.GetCreationTimeUtc(dir);
			var lastHit = Directory.GetFiles(dir, "*.complete").Select(File.GetLastWriteTimeUtc)
				.DefaultIfEmpty(Directory.GetLastWriteTimeUtc(dir)).Max();
			return new Entry { Created = created, LastHit = lastHit > now ? now : lastHit };
		}
		catch (IOException)
		{
			return new Entry { Created = now, LastHit = now };
		}
	}

	private void Load()
	{
		try
		{
			if (File.Exists(_path))
			{
				var file = JsonSerializer.Deserialize<TrackerFile>(File.ReadAllText(_path), JsonOpts);
				foreach (var (key, e) in file?.Entries ?? new())
					_entries[key] = e;
			}
		}
		catch (Exception ex)
		{
			// Rebuilt from the dirs on disk by the next sweep; only promotion is lost.
			_log.Warning(ex, "Cache tracker {Path} unreadable, starting fresh", _path);
			_entries.Clear();
		}
	}

	/// <summary>Name of the dir the sweep moves entries into before deleting them.</summary>
	internal const string TrashDirName = ".trash";

	/// <summary>Entry dirs only — not the sweep's trash.</summary>
	private HashSet<string> ListEntryDirs()
	{
		try
		{
			return Directory.GetDirectories(_cacheDir)
				.Select(d => Path.GetFileName(d)!)
				.Where(n => !PathComparer.Equals(n, TrashDirName))
				.ToHashSet(PathComparer);
		}
		catch (DirectoryNotFoundException)
		{
			return new HashSet<string>(PathComparer);
		}
	}
}
