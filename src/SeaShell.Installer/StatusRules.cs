using System;
using System.Collections.Generic;

namespace SeaShell.Installer;

/// <summary>What <c>seashell status</c> concludes from the data it collected.</summary>
public enum StatusAdvice
{
	/// <summary>An older version is installed; the bundled bootstrapper can update it.</summary>
	UpdateAvailable,

	/// <summary>Installed version matches the bootstrapper.</summary>
	UpToDate,

	/// <summary>Nothing installed yet.</summary>
	NotInstalled,

	/// <summary>The installed version is newer than the bootstrapper: nothing to offer.</summary>
	BootstrapperOlder,
}

/// <summary>
/// Interpretation rules for the status views: parsing the installed version
/// out of <c>sea --version</c>, reading daemon liveness out of
/// <c>sea --status</c>, and deciding what to tell the user to do next.
/// </summary>
public static class StatusRules
{
	/// <summary>
	/// Pull the version out of <c>sea --version</c> output
	/// (e.g. "SeaShell v0.6.2.279"). Falls back to the raw output when no
	/// version marker is present.
	/// </summary>
	public static string ParseInstalledVersion(string seaVersionOutput) =>
		seaVersionOutput.Contains('v')
			? seaVersionOutput[(seaVersionOutput.LastIndexOf('v') + 1)..].Trim()
			: seaVersionOutput;

	/// <summary>True when <c>sea --status</c> reports a live daemon.</summary>
	public static bool DaemonReportsRunning(string seaStatusOutput) =>
		seaStatusOutput.Contains(", up ", StringComparison.Ordinal);

	/// <summary>
	/// True when the registered SYSTEM daemon task belongs to a different
	/// version than the <c>current</c> junction — a drift worth warning about,
	/// but only when both sides are actually known.
	/// </summary>
	public static bool TaskVersionDrifts(Version? currentVersion, Version? taskVersion) =>
		currentVersion != null && taskVersion != null && taskVersion != currentVersion;

	/// <summary>
	/// Which closing line the status view should print. Versions are compared
	/// as versions: an older bootstrapper (<c>dnx SeaShell@older status</c>)
	/// must not offer a downgrade as an update.
	/// </summary>
	public static StatusAdvice Advise(bool installed, string? installedVersion, string bootstrapperVersion)
	{
		if (!installed) return StatusAdvice.NotInstalled;
		if (!Version.TryParse(installedVersion, out var inst) || !Version.TryParse(bootstrapperVersion, out var mine))
			return StatusAdvice.UpToDate;
		return UpdateCheck.IsNewer(mine, inst) ? StatusAdvice.UpdateAvailable
			: UpdateCheck.IsNewer(inst, mine) ? StatusAdvice.BootstrapperOlder
			: StatusAdvice.UpToDate;
	}

	/// <summary>
	/// The dashboard's closing lines. <paramref name="carries"/>: the
	/// bootstrapper has an archive of its own to compare with.
	/// <paramref name="check"/>: the daemon's last release check
	/// (<c>update.check</c>, see <see cref="UpdateCheck"/>), or null when there
	/// is none. A release newer than everything here is announced; otherwise a
	/// check on record means "up to date" as of its time; without one the
	/// dashboard says how to check.
	/// </summary>
	public static IReadOnlyList<string> ClosingLines(
		bool installed, string? installedVersion, string bootstrapperVersion, bool carries, UpdateCheckInfo? check)
	{
		if (!installed) return new[] { "Run 'seashell install' to get started" };

		var lines = new List<string>();
		var advice = carries ? Advise(installed, installedVersion, bootstrapperVersion) : StatusAdvice.UpToDate;
		if (advice == StatusAdvice.UpdateAvailable)
			lines.Add($"Update: v{installedVersion} -> v{bootstrapperVersion}  (run 'seashell update')");
		else if (advice == StatusAdvice.BootstrapperOlder)
			lines.Add($"Installed v{installedVersion} is newer than this bootstrapper (v{bootstrapperVersion})");

		var online = check != null
			&& check.IsNewerThan(installedVersion)
			&& (!carries || !Version.TryParse(bootstrapperVersion, out var mine) || check.IsNewerThan(mine));
		if (online)
			lines.Add($"Update available: v{check!.Latest} (run 'seashell update')");
		else if (advice == StatusAdvice.UpToDate)
			lines.Add(check?.CheckedAt is { } at
				? $"Up to date (checked {at.ToLocalTime():yyyy-MM-dd HH:mm})"
				: check != null || carries
					? "Up to date"
					: "Run 'seashell update' to check online for a newer version");
		return lines;
	}

	/// <summary>
	/// The <c>update:</c> row of <c>sea --status</c>. The dashboard forwards
	/// that output but says the same thing in its own closing line, so it
	/// leaves this row out instead of announcing an update twice.
	/// </summary>
	public static bool IsUpdateNoticeRow(string seaStatusLine) =>
		seaStatusLine.TrimStart().StartsWith("update:", StringComparison.Ordinal);
}
