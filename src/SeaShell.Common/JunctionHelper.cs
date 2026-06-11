using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SeaShell;

/// <summary>
/// Cross-platform directory junction/symlink management.
/// Windows: NTFS junctions (no privilege required for directories).
/// Linux: symbolic links via <see cref="Directory.CreateSymbolicLink"/>.
/// </summary>
public static class JunctionHelper
{
	/// <summary>
	/// Create or update a junction/symlink at <paramref name="junctionPath"/>
	/// pointing to <paramref name="targetPath"/>. Deletes any existing junction first.
	/// </summary>
	public static void CreateOrUpdate(string junctionPath, string targetPath)
	{
		// Remove existing junction/symlink (not a recursive directory delete —
		// just the reparse point / symlink entry itself). Also handles orphaned
		// links whose target no longer exists.
		if (IsJunction(junctionPath))
		{
			try { Directory.Delete(junctionPath, false); }
			catch (DirectoryNotFoundException) { }
			catch (IOException) { File.Delete(junctionPath); } // dangling symlink on Unix
		}
		else if (Directory.Exists(junctionPath))
		{
			// A real directory exists where the junction should be — refuse to delete
			// it silently. Caller should handle this case.
			throw new IOException(
				$"Cannot create junction: '{junctionPath}' is a real directory, not a junction.");
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			CreateWindowsJunction(junctionPath, targetPath);
		else
			Directory.CreateSymbolicLink(junctionPath, targetPath);
	}

	/// <summary>
	/// Read the target of a junction/symlink. Returns null if the path is not a junction,
	/// does not exist, or points at a target that no longer exists — an orphaned link
	/// reads as "no current version" so discovery falls back instead of trusting it.
	/// </summary>
	public static string? ReadTarget(string path)
	{
		try
		{
			var target = new DirectoryInfo(path).LinkTarget;
			if (target == null) return null;
			// LinkTarget may be relative to the link's directory.
			var resolved = Path.GetFullPath(target, Path.GetDirectoryName(Path.GetFullPath(path)) ?? target);
			return Directory.Exists(resolved) ? resolved : null;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Check whether the path is a junction (Windows) or symlink (Linux).
	/// Returns false if the path does not exist. True even when the link's
	/// target is gone — the entry itself is what's being asked about.
	/// </summary>
	public static bool IsJunction(string path)
	{
		try
		{
			return new DirectoryInfo(path).LinkTarget != null;
		}
		catch
		{
			return false;
		}
	}

	// ── Windows junction via mklink /J ─────────────────────────────────

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static void CreateWindowsJunction(string junctionPath, string targetPath)
	{
		// mklink /J creates an NTFS junction — no privilege required for directories.
		var psi = new ProcessStartInfo
		{
			FileName = "cmd.exe",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		psi.ArgumentList.Add("/c");
		psi.ArgumentList.Add("mklink");
		psi.ArgumentList.Add("/J");
		psi.ArgumentList.Add(junctionPath);
		psi.ArgumentList.Add(targetPath);

		using var proc = Process.Start(psi)!;
		proc.WaitForExit(10_000);

		if (proc.ExitCode != 0)
		{
			var stderr = proc.StandardError.ReadToEnd().Trim();
			throw new IOException($"mklink /J failed (exit {proc.ExitCode}): {stderr}");
		}
	}
}
