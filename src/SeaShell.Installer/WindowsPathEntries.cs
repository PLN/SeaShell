using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaShell.Installer;

/// <summary>
/// A PATH rewrite: the candidate new value, and whether it differs from what
/// was there (an unchanged value must not be written back).
/// </summary>
public readonly record struct PathStripResult(string Path, bool Changed);

/// <summary>
/// Pure PATH-string editing. The Windows user/machine PATH is one
/// semicolon-joined string, so the decision of what to drop is separated from
/// the registry-backed read/write that applies it.
///
/// The string edited is the RAW registry value: entries such as
/// <c>%USERPROFILE%\go\bin</c> stay literal, and only the entries being added,
/// moved or removed change. Comparisons see each entry through
/// <c>expand</c> (environment-variable expansion in production), so a literal
/// <c>%USERPROFILE%\.dotnet\tools</c> is still recognised as the tools directory.
/// </summary>
public static class WindowsPathEntries
{
	/// <summary>Drop entries that are the same directory as one of <paramref name="unwanted"/>.</summary>
	public static PathStripResult Strip(string? existingPath, IEnumerable<string> unwanted, Func<string, string>? expand = null) =>
		StripWhere(existingPath, entry => unwanted.Any(u => SameDirectory(entry, u)), expand);

	/// <summary>
	/// Drop every entry the predicate rejects; the predicate sees the expanded
	/// entry. Order and all other entries (in their raw form) are preserved.
	/// </summary>
	public static PathStripResult StripWhere(string? existingPath, Func<string, bool> isUnwanted, Func<string, string>? expand = null)
	{
		expand ??= static e => e;
		var existing = existingPath ?? "";
		var newPath = string.Join(';', existing.Split(';').Where(p => !isUnwanted(expand(p))));
		return new PathStripResult(newPath, newPath != existing);
	}

	/// <summary>
	/// Make <paramref name="entry"/> appear before <paramref name="before"/>.
	/// Missing: prepended. Already ahead of it (or <paramref name="before"/>
	/// absent): unchanged. Behind it: moved to just in front of it, so every
	/// other entry keeps its place. Entries compare case-insensitively,
	/// ignoring a trailing backslash.
	/// </summary>
	public static PathStripResult EnsureBefore(string? existingPath, string entry, string before, Func<string, string>? expand = null)
	{
		expand ??= static e => e;
		bool SameDirectory(string a, string b) => WindowsPathEntries.SameDirectory(expand(a), b);
		var existing = existingPath ?? "";
		var entries = existing.Length == 0 ? new List<string>() : existing.Split(';').ToList();

		var entryIndex = entries.FindIndex(e => SameDirectory(e, entry));
		if (entryIndex < 0)
		{
			var prepended = existing.Length == 0 ? entry : $"{entry};{existing}";
			return new PathStripResult(prepended, true);
		}

		var beforeIndex = entries.FindIndex(e => SameDirectory(e, before));
		if (beforeIndex < 0 || entryIndex < beforeIndex)
			return new PathStripResult(existing, false);

		var kept = entries[entryIndex];
		entries.RemoveAll(e => SameDirectory(e, entry));
		entries.Insert(entries.FindIndex(e => SameDirectory(e, before)), kept);
		return new PathStripResult(string.Join(';', entries), true);
	}

	/// <summary>Same directory: case-insensitive, ignoring surrounding blanks and a trailing backslash.</summary>
	public static bool SameDirectory(string a, string b) =>
		string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
