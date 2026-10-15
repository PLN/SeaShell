using System;

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

	/// <summary>Which closing line the status view should print.</summary>
	public static StatusAdvice Advise(bool installed, string? installedVersion, string bootstrapperVersion) =>
		installed && installedVersion != null && installedVersion != bootstrapperVersion
			? StatusAdvice.UpdateAvailable
			: installed ? StatusAdvice.UpToDate : StatusAdvice.NotInstalled;
}