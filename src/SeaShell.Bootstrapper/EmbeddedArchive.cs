using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

namespace SeaShell.Bootstrapper;

/// <summary>
/// The per-RID SeaShell archive embedded at pack time (<c>-p:EmbedArchives=true</c>).
/// A bootstrapper with an archive for the current RID "carries" its own
/// version: <c>install</c> installs exactly that. The copy shipped inside the
/// install (<c>current/bin/seashell</c>) and a dev build carry none, and
/// <c>install</c> then behaves as <c>update</c>.
/// </summary>
internal static class EmbeddedArchive
{
	/// <summary>
	/// Suffix of a file moved aside because it could not be overwritten (a
	/// running process maps it). Removed by the next install of that directory.
	/// </summary>
	public const string SetAsideMarker = ".seashell-old-";

	private static Assembly Assembly => typeof(EmbeddedArchive).Assembly;

	/// <summary>Manifest resource name of the archive for <paramref name="rid"/>, or null.</summary>
	public static string? ResourceFor(string rid) =>
		Assembly.GetManifestResourceNames()
			.FirstOrDefault(n => n.Contains(rid, StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip"));

	/// <summary>True when this bootstrapper carries an archive for the current RID.</summary>
	public static bool CarriesCurrentRid(bool isWindows) =>
		ResourceFor(EnvironmentProbe.CurrentRid(isWindows)) != null;

	/// <summary>
	/// The files of the archive for <paramref name="rid"/> (relative path,
	/// uncompressed length); null when this bootstrapper carries none.
	/// </summary>
	public static IReadOnlyCollection<(string Path, long Length)>? Manifest(string rid)
	{
		var resourceName = ResourceFor(rid);
		if (resourceName == null) return null;
		using var stream = Assembly.GetManifestResourceStream(resourceName)!;
		using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
		return zip.Entries
			.Where(e => !e.FullName.EndsWith('/'))
			.Select(e => (e.FullName, e.Length))
			.ToArray();
	}

	/// <summary>
	/// Extract the archive for <paramref name="rid"/> into <paramref name="binDir"/>.
	/// False (with the reason printed) when the package carries no archive for
	/// this RID or a file cannot be written.
	///
	/// A repair or reinstall of the same version writes into a directory that
	/// running scripts may still execute from. Files that are already
	/// identical are left alone. A file that differs but cannot be overwritten
	/// (Windows refuses to overwrite a mapped image, yet allows renaming it)
	/// is moved aside and replaced; the running process keeps using the old
	/// copy, and the next install removes it.
	/// </summary>
	public static bool ExtractTo(string rid, string binDir)
	{
		var resourceName = ResourceFor(rid);
		if (resourceName == null)
		{
			Console.Error.WriteLine($"  ERROR: No embedded archive for {rid}");
			Console.Error.WriteLine($"  Available: {string.Join(", ", Assembly.GetManifestResourceNames())}");
			return false;
		}

		RemoveSetAsideFiles(binDir);

		Console.WriteLine($"  Extracting {rid} archive...");
		using var stream = Assembly.GetManifestResourceStream(resourceName)!;
		using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
		int written = 0, unchanged = 0;
		var setAside = new List<string>();
		foreach (var entry in zip.Entries)
		{
			if (entry.FullName.EndsWith('/')) continue;
			var dest = Path.Combine(binDir, entry.FullName);
			try
			{
				var destDir = Path.GetDirectoryName(dest);
				if (destDir != null) Directory.CreateDirectory(destDir);
				if (SameContent(entry, dest)) { unchanged++; continue; }

				try
				{
					entry.ExtractToFile(dest, overwrite: true);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					if (!MoveAside(dest, ex)) return false;
					entry.ExtractToFile(dest, overwrite: false);
					setAside.Add(entry.FullName);
				}
				written++;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"  ERROR: could not write {dest}: {ex.GetType().Name}: {ex.Message}");
				PrintHolders(dest);
				return false;
			}
		}

		Console.WriteLine(unchanged > 0
			? $"  Extracted {written} files ({unchanged} already identical)."
			: $"  Extracted {written} files.");
		if (setAside.Count > 0)
		{
			Console.WriteLine($"  {setAside.Count} file(s) in use by running processes were moved aside and replaced:");
			Console.WriteLine($"    {string.Join(", ", setAside)}");
			Console.WriteLine("    (those processes keep their copy; the next install removes it)");
		}
		return true;
	}

	private static bool SameContent(ZipArchiveEntry entry, string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Length != entry.Length) return false;
			using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var packed = entry.Open();
			var a = new byte[81920];
			var b = new byte[81920];
			while (true)
			{
				var read = packed.ReadAtLeast(a, a.Length, throwOnEndOfStream: false);
				if (read == 0) return existing.ReadByte() == -1;
				if (existing.ReadAtLeast(b, read, throwOnEndOfStream: false) != read) return false;
				if (!a.AsSpan(0, read).SequenceEqual(b.AsSpan(0, read))) return false;
			}
		}
		catch { return false; }
	}

	private static bool MoveAside(string dest, Exception overwriteError)
	{
		var aside = dest + SetAsideMarker + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
		try
		{
			File.Move(dest, aside);
			return true;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"  ERROR: cannot replace {dest}:");
			Console.Error.WriteLine($"         overwrite failed ({overwriteError.Message})");
			Console.Error.WriteLine($"         and moving it aside failed ({ex.Message}).");
			PrintHolders(dest);
			Console.Error.WriteLine("         The install stopped here: files extracted before this one are already replaced, the");
			Console.Error.WriteLine("         rest of this directory is as it was. End that process and run the install again.");
			return false;
		}
	}

	private static void PrintHolders(string path)
	{
		var holders = FileHolders.Find(new[] { path });
		Console.Error.WriteLine(holders.Count > 0
			? $"         In use by: {string.Join(", ", holders.Select(h => h.ToString()))}"
			: "         No process holding it could be identified.");
	}

	// Leftovers of an earlier install whose processes have exited since.
	private static void RemoveSetAsideFiles(string binDir)
	{
		try
		{
			if (!Directory.Exists(binDir)) return;
			foreach (var file in Directory.EnumerateFiles(binDir, "*" + SetAsideMarker + "*", SearchOption.AllDirectories))
			{
				try { File.Delete(file); } catch { } // still mapped: next time
			}
		}
		catch { }
	}
}
