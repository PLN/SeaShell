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
/// </summary>
public static class WindowsPathEntries
{
	/// <summary>Drop entries equal (case-insensitively) to one of <paramref name="unwanted"/>.</summary>
	public static PathStripResult Strip(string? existingPath, IEnumerable<string> unwanted) =>
		StripWhere(existingPath,
			entry => unwanted.Any(u => entry.Equals(u, StringComparison.OrdinalIgnoreCase)));

	/// <summary>Drop every entry the predicate rejects. Order and all other entries are preserved.</summary>
	public static PathStripResult StripWhere(string? existingPath, Func<string, bool> isUnwanted)
	{
		var existing = existingPath ?? "";
		var newPath = string.Join(';', existing.Split(';').Where(p => !isUnwanted(p)));
		return new PathStripResult(newPath, newPath != existing);
	}
}