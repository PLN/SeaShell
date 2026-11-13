using System;
using System.IO;

namespace SeaShell.Installer;

/// <summary>
/// Uninstall (Linux): remove the user-scope script mutex directory
/// (<see cref="SeaShellPaths.UserMutexDir"/>). Its lock files are flock'd by
/// running scripts — possibly of another SeaShell version the account still
/// uses — and deleting a held one would let a second instance start. So each
/// file is locked first and deleted only while this holds it; a file another
/// process holds stays, and so does the directory. A lock file nobody holds is
/// a leftover (a script normally deletes its own on exit).
/// </summary>
public static class MutexDirRemoval
{
	/// <summary>Outcome: <paramref name="Held"/> lock files are in use and were kept; <paramref name="Error"/> is set when something else failed.</summary>
	public readonly record struct Result(bool DirRemoved, int Held, string? Error);

	public static Result Remove(string dir)
	{
		if (!Directory.Exists(dir)) return new Result(true, 0, null);

		var held = 0;
		string? error = null;
		string[] files;
		try { files = Directory.GetFiles(dir); }
		catch (Exception ex) { return new Result(false, 0, ex.Message); }

		foreach (var file in files)
		{
			try
			{
				// Linux: FileShare.None is the same exclusive flock ScriptMutex takes.
				// (Windows, only for the tests: a file open without Delete sharing can't be deleted.)
				using var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite,
					OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None);
				File.Delete(file);
			}
			catch (FileNotFoundException) { }
			catch (IOException) { held++; }
			catch (Exception ex) { error ??= $"{Path.GetFileName(file)}: {ex.Message}"; }
		}

		if (held > 0 || error != null) return new Result(false, held, error);
		try
		{
			Directory.Delete(dir, false);
			return new Result(true, 0, null);
		}
		catch (IOException) when (Directory.Exists(dir))
		{
			// A script took a new lock in the meantime: it is in use.
			return new Result(false, 1, null);
		}
		catch (Exception ex) { return new Result(false, 0, ex.Message); }
	}
}
