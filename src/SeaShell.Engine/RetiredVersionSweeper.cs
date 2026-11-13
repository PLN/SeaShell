using System;
using System.Collections.Generic;
using System.IO;

namespace SeaShell.Engine;

/// <summary>
/// Removes retired version directories of one identity: <c>v{version}</c> dirs older
/// than the version <c>current</c> points to, with no <c>daemon.path</c> marker (no
/// longer installed) and nothing running from them. They are left behind when an
/// install's cleanup could not delete them — typically the old <c>seashell</c> or a
/// script was still running from the dir during an update.
///
/// A dir is touched only once nothing is positively found using it (the probes are
/// shared with the installer, see <see cref="DirectoryInUse"/>):
/// <list type="bullet">
/// <item>Linux: <see cref="CacheDirsInUse"/> with cwd — no process maps, names or has its
///   working directory under it.</item>
/// <item>Windows: every file under it opens for writing with no sharing. That fails for a
///   running exe and a loaded dll (both mapped as images; their processes need hold no
///   handle, so a rename of the dir does not fail for them) and for any file a process
///   has open. Then the dir is renamed into a trash dir, which also fails while a process
///   has its working directory in it.</item>
/// </list>
/// The trash copy is probed again before each delete attempt, so a process that started
/// from the dir between probe and rename keeps its files. A delete that fails is
/// retried on the next sweep. The current version, newer versions and the caller's own
/// version are never touched.
/// </summary>
public static class RetiredVersionSweeper
{
	/// <summary>Trash dir under the data dir; not a <c>v*</c> name, so version discovery ignores it.</summary>
	internal const string TrashDirName = ".retired";

	public enum Outcome
	{
		/// <summary>Moved out and deleted.</summary>
		Removed,
		/// <summary>Moved out; not deleted (fully) yet, retried next sweep.</summary>
		RemovedPartly,
		/// <summary>Still has a <c>daemon.path</c> marker: installed, not retired.</summary>
		SkippedInstalled,
		/// <summary>The caller's own version (a daemon older than <c>current</c> still running).</summary>
		SkippedOwnVersion,
		/// <summary>A process runs from or has files open in the dir; left untouched.</summary>
		SkippedInUse,
	}

	/// <summary>What happened to one candidate (a version older than <c>current</c>).</summary>
	public sealed record Entry(Version Version, string Directory, Outcome Outcome, string Detail);

	/// <summary>
	/// Sweep <paramref name="dataDir"/>. Returns one entry per version older than
	/// <c>current</c>; empty when there is no readable <c>current</c> link, since then
	/// nothing can be called retired.
	/// </summary>
	public static IReadOnlyList<Entry> Sweep(string dataDir, Version? ownVersion)
	{
		var results = new List<Entry>();
		var current = VersionDiscovery.GetCurrentVersion(dataDir);
		if (current == null) return results;

		var candidates = new List<(Version Version, string Directory)>();
		foreach (var (ver, dir) in VersionDiscovery.EnumerateVersions(dataDir))
		{
			if (ver >= current) continue;
			if (ownVersion != null && ver == ownVersion)
				results.Add(new Entry(ver, dir, Outcome.SkippedOwnVersion, "this daemon runs from it"));
			else if (MarkerFile.Exists(dir, MarkerFile.DaemonPath))
				results.Add(new Entry(ver, dir, Outcome.SkippedInstalled,
					$"{MarkerFile.DaemonPath} marker present ({MarkerFile.Read(dir, MarkerFile.DaemonPath)})"));
			else
				candidates.Add((ver, dir));
		}

		if (candidates.Count > 0)
		{
			// Linux: one /proc scan for the whole data dir; names of v* children in use.
			var inUse = CacheDirsInUse.Find(dataDir, includeCwd: true);
			foreach (var (ver, dir) in candidates)
				results.Add(Remove(dataDir, ver, dir, inUse));
		}

		EmptyTrash(dataDir);
		return results;
	}

	private static Entry Remove(string dataDir, Version ver, string dir, HashSet<string> inUse)
	{
		var name = Path.GetFileName(dir);
		if (inUse.Contains(name))
			return new Entry(ver, dir, Outcome.SkippedInUse, "a running process maps, names or has its cwd under it");
		if (DirectoryInUse.FindOpenFile(dir) is { } busy)
			return new Entry(ver, dir, Outcome.SkippedInUse, busy);

		var trash = Path.Combine(dataDir, TrashDirName, $"{name}.{Guid.NewGuid():N}");
		try
		{
			Directory.CreateDirectory(Path.Combine(dataDir, TrashDirName));
			Directory.Move(dir, trash);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Windows: a process has a handle open in the dir (e.g. its cwd).
			return new Entry(ver, dir, Outcome.SkippedInUse, $"rename failed: {ex.GetType().Name}: {ex.Message}");
		}

		var failure = TryDelete(trash);
		return failure == null
			? new Entry(ver, dir, Outcome.Removed, $"via {trash}")
			: new Entry(ver, dir, Outcome.RemovedPartly, $"moved to {trash}; {failure}; retried next sweep");
	}

	/// <summary>Delete leftovers of earlier attempts; anything still in use waits for the next sweep.</summary>
	private static void EmptyTrash(string dataDir)
	{
		var trashDir = Path.Combine(dataDir, TrashDirName);
		try
		{
			var dirs = Directory.GetDirectories(trashDir);
			if (dirs.Length > 0)
			{
				var inUse = CacheDirsInUse.Find(trashDir, includeCwd: true);
				foreach (var dir in dirs)
					if (!inUse.Contains(Path.GetFileName(dir)))
						TryDelete(dir);
			}
			if (Directory.GetFileSystemEntries(trashDir).Length == 0)
				Directory.Delete(trashDir);
		}
		catch { }
	}

	/// <summary>Delete a trashed dir unless a file in it is in use. Null on success, else why not.</summary>
	private static string? TryDelete(string dir)
	{
		if (DirectoryInUse.FindOpenFile(dir) is { } busy)
			return busy;
		try
		{
			Directory.Delete(dir, recursive: true);
			return null;
		}
		catch (Exception ex)
		{
			return $"delete failed: {ex.GetType().Name}: {ex.Message}";
		}
	}
}
