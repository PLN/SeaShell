using System;
using System.IO;

namespace SeaShell.Installer;

/// <summary>
/// Recognizes leftovers of the pre-0.5.0 "common install", which put binaries
/// under <c>%ProgramData%\seashell</c> and put that directory on the PATH.
/// </summary>
public static class LegacyInstallPaths
{
	/// <summary>The pre-0.5.0 common-install root, or null where it cannot be resolved.</summary>
	public static string? ProgramDataSeashellRoot()
	{
		var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		return string.IsNullOrEmpty(programData) ? null : Path.Combine(programData, "seashell");
	}

	/// <summary>True when a PATH entry points at the common-install root or anything under it.</summary>
	public static bool IsUnderProgramDataSeashell(string pathEntry) =>
		IsUnderRoot(pathEntry, ProgramDataSeashellRoot());

	/// <summary>
	/// Same test against an explicit root, with environment-variable expansion
	/// (PATH entries can be stored unexpanded, e.g. <c>%COMMON_APPDATA%\seashell\bin</c>).
	/// A trailing separator on the entry is not significant; a deeper path that
	/// merely shares the root's name prefix is not a match.
	/// </summary>
	public static bool IsUnderRoot(string pathEntry, string? root)
	{
		if (string.IsNullOrWhiteSpace(pathEntry)) return false;
		string expanded;
		try { expanded = Environment.ExpandEnvironmentVariables(pathEntry); }
		catch { expanded = pathEntry; }
		if (string.IsNullOrEmpty(root)) return false;
		var trimmed = expanded.TrimEnd('\\', '/');
		return trimmed.Equals(root, StringComparison.OrdinalIgnoreCase)
			|| trimmed.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}
}