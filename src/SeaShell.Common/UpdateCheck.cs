using System;
using System.Globalization;
using System.IO;

namespace SeaShell;

/// <summary>
/// The result of the daemon's periodic lookup of the latest SeaShell release,
/// kept in <c>{DataDir}/update.check</c>: line 1 = latest version found, line 2 =
/// ISO-8601 timestamp of the last successful check. Written only on success — a
/// failed lookup leaves the previous result in place. Read by <c>sea --status</c>
/// and the <c>seashell</c> dashboard.
/// </summary>
public static class UpdateCheck
{
	/// <summary>File name under the identity's data dir.</summary>
	public const string FileName = "update.check";

	/// <summary>Path of the file under an explicit data dir.</summary>
	public static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

	/// <summary>
	/// Read the file under <paramref name="dataDir"/>. Null when it is missing or
	/// line 1 is not a version. <see cref="UpdateCheckInfo.CheckedAt"/> is null when
	/// line 2 is missing or unparseable.
	/// </summary>
	public static UpdateCheckInfo? Read(string dataDir)
	{
		string[] lines;
		try { lines = File.ReadAllLines(PathIn(dataDir)); }
		catch { return null; }
		return Parse(lines);
	}

	/// <summary>Parse the file's lines (see <see cref="Read"/>).</summary>
	public static UpdateCheckInfo? Parse(string[] lines)
	{
		if (lines.Length == 0 || !System.Version.TryParse(lines[0].Trim(), out var latest))
			return null;
		DateTimeOffset? checkedAt = null;
		if (lines.Length > 1 && DateTimeOffset.TryParse(lines[1].Trim(), CultureInfo.InvariantCulture,
				DateTimeStyles.RoundtripKind, out var at))
			checkedAt = at;
		return new UpdateCheckInfo(latest, checkedAt);
	}

	/// <summary>
	/// Record a successful check. Written to a temp file and moved into place, so a
	/// reader never sees a half-written file.
	/// </summary>
	public static void Write(string dataDir, string latestVersion, DateTimeOffset checkedAt)
	{
		Directory.CreateDirectory(dataDir);
		var path = PathIn(dataDir);
		var tmp = $"{path}.{Environment.ProcessId}.tmp";
		File.WriteAllText(tmp,
			latestVersion.Trim() + "\n" + checkedAt.ToString("o", CultureInfo.InvariantCulture) + "\n");
		File.Move(tmp, path, overwrite: true);
	}

	/// <summary>
	/// True when <paramref name="available"/> is a higher version than
	/// <paramref name="installed"/>. Missing components count as zero, so
	/// <c>0.6.3</c> equals <c>0.6.3.0</c>.
	/// </summary>
	public static bool IsNewer(Version available, Version installed) =>
		Compare(available, installed) > 0;

	/// <summary>Same as <see cref="IsNewer(Version, Version)"/>; false when <paramref name="installed"/> does not parse.</summary>
	public static bool IsNewer(Version available, string? installed) =>
		System.Version.TryParse(installed, out var v) && IsNewer(available, v);

	private static int Compare(Version a, Version b)
	{
		int[] x = { a.Major, a.Minor, Math.Max(a.Build, 0), Math.Max(a.Revision, 0) };
		int[] y = { b.Major, b.Minor, Math.Max(b.Build, 0), Math.Max(b.Revision, 0) };
		for (var i = 0; i < x.Length; i++)
			if (x[i] != y[i]) return x[i].CompareTo(y[i]);
		return 0;
	}
}

/// <summary>Contents of <c>update.check</c>.</summary>
/// <param name="Latest">Latest SeaShell version found online (line 1).</param>
/// <param name="CheckedAt">Time of the last successful check (line 2), or null if unreadable.</param>
public sealed record UpdateCheckInfo(Version Latest, DateTimeOffset? CheckedAt)
{
	/// <summary>True when <see cref="Latest"/> is newer than <paramref name="installed"/>.</summary>
	public bool IsNewerThan(Version installed) => UpdateCheck.IsNewer(Latest, installed);

	/// <summary>True when <see cref="Latest"/> is newer than <paramref name="installed"/>; false if it does not parse.</summary>
	public bool IsNewerThan(string? installed) => UpdateCheck.IsNewer(Latest, installed);
}
