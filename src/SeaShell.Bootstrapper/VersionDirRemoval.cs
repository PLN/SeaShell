using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeaShell.Bootstrapper;

/// <summary>How removing a version directory went.</summary>
internal enum VersionDirRemovalResult
{
	Removed,

	/// <summary>Something still runs from it (holders known or not); nothing in it was touched.</summary>
	InUse,

	/// <summary>Held only by the process given as the expected holder (the relocating seashell); nothing was touched.</summary>
	HeldByExpected,

	/// <summary>Moved aside and found unused, but the delete failed part-way (what is left carries the <c>.removing-</c> name).</summary>
	PartlyRemoved,
}

/// <summary>
/// Remove a whole version directory without breaking what still runs from it,
/// with the daemon's version-sweep guard (<see cref="DirectoryInUse"/>):
/// <list type="number">
/// <item>Probe the directory. In use (Windows: a file that does not open for
///   writing with no sharing — a running exe or loaded dll is a mapped image and
///   does not stop a rename; Linux: a process maps, names or works in it): nothing
///   is touched and the holders are reported.</item>
/// <item>Rename it to <c>{dir}.removing-{time}</c>, so nothing new starts from it.
///   A failed rename (Windows: an open handle, a working directory) also leaves it
///   untouched.</item>
/// <item>Probe the moved copy again before each delete attempt: a process that
///   started from it between probe and rename puts it back under its old name,
///   untouched.</item>
/// </list>
/// Only a delete that fails after a clean probe (a file opened in between) can
/// leave part of a directory behind; that part keeps the <c>.removing-</c> name.
/// </summary>
internal static class VersionDirRemoval
{
	public const string RemovingMarker = ".removing-";

	/// <param name="dir">The version directory (or a <c>.removing-</c> leftover, see <see cref="RemoveLeftover"/>).</param>
	/// <param name="expectedHolder">
	/// Windows: a process allowed to hold it (the seashell that started a relocated
	/// uninstall and removes its own directory after it exits).
	/// </param>
	public static (VersionDirRemovalResult Result, string Detail) Remove(string dir, int? expectedHolder = null)
	{
		if (InUse(dir, expectedHolder) is { } busy) return busy;

		var aside = dir + RemovingMarker + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
		try
		{
			Directory.Move(dir, aside);
		}
		catch (Exception ex)
		{
			return (VersionDirRemovalResult.InUse, $"in use or locked (rename failed: {ex.Message})");
		}

		if (InUse(aside, expectedHolder) is { } started)
		{
			// Started from it between the probe and the rename: give it its path back.
			try { Directory.Move(aside, dir); }
			catch (Exception ex) { return (VersionDirRemovalResult.InUse, $"{started.Detail}; left untouched as {aside} (moving it back failed: {ex.Message})"); }
			return started;
		}
		return Delete(aside);
	}

	/// <summary>
	/// A <c>.removing-</c> directory an earlier attempt left: probe it again, delete
	/// it only when nothing uses it.
	/// </summary>
	public static (VersionDirRemovalResult Result, string Detail) RemoveLeftover(string dir) =>
		InUse(dir, expectedHolder: null) is { } busy ? busy : Delete(dir);

	private static (VersionDirRemovalResult Result, string Detail) Delete(string dir)
	{
		try
		{
			Directory.Delete(dir, true);
			return (VersionDirRemovalResult.Removed, "");
		}
		catch (Exception ex)
		{
			return (VersionDirRemovalResult.PartlyRemoved, $"{dir}: {ex.Message}");
		}
	}

	// In use, with the holders when Windows can name them. Linux ignores this
	// process: an uninstall run from the installed copy maps its own version
	// directory, and Linux lets a running image's directory go.
	private static (VersionDirRemovalResult Result, string Detail)? InUse(string dir, int? expectedHolder)
	{
		var reason = DirectoryInUse.Find(dir, ignorePid: Environment.ProcessId);
		if (reason == null) return null;
		if (!OperatingSystem.IsWindows()) return (VersionDirRemovalResult.InUse, "in use (a running process maps, names or works in it)");

		var holders = FileHolders.Find(SafeFiles(dir));
		var others = holders.Where(h => h.Pid != expectedHolder).ToArray();
		if (holders.Count > 0 && others.Length == 0)
			return (VersionDirRemovalResult.HeldByExpected, "");
		return (VersionDirRemovalResult.InUse, others.Length > 0
			? $"in use by {string.Join(", ", others.Select(h => h.ToString()))}"
			: $"in use ({reason})");
	}

	private static IEnumerable<string> SafeFiles(string dir)
	{
		try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories); }
		catch { return Array.Empty<string>(); }
	}
}
