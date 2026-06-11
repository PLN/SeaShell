using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeaShell;

/// <summary>
/// Discovers installed SeaShell versions by enumerating <c>v*</c> directories
/// under <see cref="SeaShellPaths.DataDir"/>. Replaces the manifest-based
/// version lookup that <c>ServiceManifest</c> previously provided.
/// </summary>
public static class VersionDiscovery
{
	/// <summary>
	/// Enumerate all installed version directories, sorted newest-first.
	/// Each entry contains the parsed <see cref="Version"/> and the full directory path.
	/// </summary>
	public static IReadOnlyList<(Version Version, string Directory)> EnumerateVersions() =>
		EnumerateVersions(SeaShellPaths.DataDir);

	/// <summary>
	/// Enumerate installed versions under an explicit data dir (e.g. a target
	/// identity's). Returns empty on access denied rather than throwing — a
	/// non-elevated caller probing SYSTEM's data dir just sees nothing.
	/// </summary>
	public static IReadOnlyList<(Version Version, string Directory)> EnumerateVersions(string dataDir)
	{
		string[] dirs;
		try
		{
			if (!Directory.Exists(dataDir))
				return Array.Empty<(Version, string)>();
			dirs = Directory.GetDirectories(dataDir, "v*");
		}
		catch
		{
			return Array.Empty<(Version, string)>();
		}

		var results = new List<(Version Version, string Directory)>();
		foreach (var dir in dirs)
		{
			var dirName = Path.GetFileName(dir);
			if (ParseVersionDir(dirName) is { } ver)
				results.Add((ver, dir));
		}

		results.Sort((a, b) => b.Version.CompareTo(a.Version)); // newest first
		return results;
	}

	/// <summary>
	/// Find the directory for a specific version string (e.g. "0.5.0.222").
	/// Returns null if no matching directory exists.
	/// </summary>
	public static string? FindVersion(string versionStr)
	{
		var dir = SeaShellPaths.VersionDir(versionStr);
		return Directory.Exists(dir) ? dir : null;
	}

	/// <summary>
	/// Parse a version directory name (e.g. "v0.5.0.222") into a <see cref="Version"/>.
	/// Returns null if the name doesn't match the expected pattern.
	/// </summary>
	public static Version? ParseVersionDir(string dirName)
	{
		if (dirName.Length < 2 || dirName[0] != 'v')
			return null;
		return Version.TryParse(dirName.AsSpan(1), out var ver) ? ver : null;
	}

	/// <summary>
	/// Get the version that the <c>current</c> junction points to.
	/// Returns null if the junction doesn't exist or its target isn't a valid version dir.
	/// </summary>
	public static Version? GetCurrentVersion() => GetCurrentVersion(SeaShellPaths.DataDir);

	/// <summary>Same, under an explicit data dir.</summary>
	public static Version? GetCurrentVersion(string dataDir)
	{
		var target = JunctionHelper.ReadTarget(SeaShellPaths.CurrentDirIn(dataDir));
		if (target == null) return null;
		return ParseVersionDir(Path.GetFileName(target));
	}
}
