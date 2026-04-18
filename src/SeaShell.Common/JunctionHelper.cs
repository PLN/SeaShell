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
		// just the reparse point / symlink entry itself).
		if (IsJunction(junctionPath))
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				Directory.Delete(junctionPath, false);
			else
				Directory.Delete(junctionPath, false);
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
	/// Read the target of a junction/symlink. Returns null if the path is not a junction
	/// or does not exist.
	/// </summary>
	public static string? ReadTarget(string path)
	{
		try
		{
			var info = new DirectoryInfo(path);
			if (!info.Exists) return null;
			var target = info.ResolveLinkTarget(returnFinalTarget: false);
			return target?.FullName;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Check whether the path is a junction (Windows) or symlink (Linux).
	/// Returns false if the path does not exist.
	/// </summary>
	public static bool IsJunction(string path)
	{
		try
		{
			var info = new DirectoryInfo(path);
			if (!info.Exists) return false;
			return info.Attributes.HasFlag(FileAttributes.ReparsePoint);
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
