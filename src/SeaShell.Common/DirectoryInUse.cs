using System;
using System.IO;

namespace SeaShell;

/// <summary>
/// Whether anything still runs from, or has files open in, a directory that is
/// about to be removed as a whole (a retired version directory, an uninstalled
/// one). Shared by the daemon's version sweep and the installer, so both prove
/// a directory unused the same way before touching any of it:
/// <list type="bullet">
/// <item>Windows (<see cref="FindOpenFile"/>): every file under it opens for writing
///   with no sharing. A running exe and a loaded dll are image sections, which refuse
///   write access although their process need hold no handle — so a successful rename
///   of the directory proves nothing for them; any open handle refuses sharing.</item>
/// <item>Linux (<see cref="CacheDirsInUse"/> with cwd): no process maps, names or has
///   its working directory under it.</item>
/// </list>
/// A probe is a snapshot: callers move the directory aside first and probe the moved
/// copy again before each delete attempt, so a process that started from it in
/// between keeps its files.
/// </summary>
public static class DirectoryInUse
{
	/// <summary>
	/// Why <paramref name="dir"/> counts as in use, or null when nothing was found
	/// using it. <paramref name="ignorePid"/> (Linux): a process not to count — the
	/// caller itself, when it runs from the directory it removes (Linux lets it).
	/// </summary>
	public static string? Find(string dir, int? ignorePid = null)
	{
		if (OperatingSystem.IsWindows()) return FindOpenFile(dir);
		if (!OperatingSystem.IsLinux()) return null;

		var parent = Path.GetDirectoryName(Path.GetFullPath(dir).TrimEnd('/'));
		if (parent == null) return null;
		return CacheDirsInUse.Find(parent, includeCwd: true, ignorePid).Contains(Path.GetFileName(dir.TrimEnd('/')))
			? $"{dir}: a running process maps, names or has its working directory under it"
			: null;
	}

	/// <summary>
	/// Windows: the first file under <paramref name="dir"/> that cannot be opened for
	/// writing with no sharing, as a reason string; null when every file can. Any
	/// failure counts as in use, including one to enumerate the dir. Null on other
	/// platforms.
	/// </summary>
	public static string? FindOpenFile(string dir)
	{
		if (!OperatingSystem.IsWindows()) return null;
		var options = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			AttributesToSkip = 0, // hidden and system files too
			IgnoreInaccessible = false,
		};
		try
		{
			foreach (var file in Directory.EnumerateFiles(dir, "*", options))
			{
				try { new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose(); }
				catch (Exception ex) { return $"{file} is in use: {ex.GetType().Name}: {ex.Message}"; }
			}
			return null;
		}
		catch (Exception ex)
		{
			return $"{dir} could not be listed: {ex.GetType().Name}: {ex.Message}";
		}
	}
}
