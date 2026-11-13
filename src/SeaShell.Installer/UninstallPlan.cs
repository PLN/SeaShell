using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaShell.Installer;

/// <summary>A script scheduled with <c>seashell schedule</c>.</summary>
/// <param name="Name">Script stem, as <c>seashell schedule</c> lists it.</param>
/// <param name="Entry">The Windows task path, or the crontab line.</param>
public sealed record ScheduledScript(string Name, string Entry);

/// <summary>
/// Decisions of <c>seashell uninstall</c>. The scope follows the same sticky
/// rule as install (<see cref="ScopeResolver"/>); passes, elevation and the
/// alternate-account handling come from <see cref="InstallAllPlanner"/>.
///
/// Scheduled scripts belong to the user, not to the install: uninstall lists
/// them and keeps them (they work again after a reinstall), and removes them
/// only with <c>--purge</c>. The automatic-update entries are SeaShell's own
/// and always go.
/// </summary>
public static class UninstallPlanner
{
	/// <summary>
	/// The parts to remove. <c>--user</c> removes the user install and its
	/// elevator (a per-user part that cannot outlive it) and leaves SYSTEM /
	/// root; everything else removes exactly the resolved scope.
	/// </summary>
	public static ScopeParts PartsToRemove(ScopeResolution resolution) =>
		resolution.Source == ScopeSource.User
			? ScopeParts.User | (resolution.LeftBehind & ScopeParts.Elevator)
			: resolution.Parts;

	/// <summary>Parts that stay installed after removing <paramref name="removing"/> (for the closing note).</summary>
	public static ScopeParts LeftInPlace(ScopeResolution resolution, ScopeParts removing) =>
		(resolution.Parts | resolution.LeftBehind) & ~removing;

	/// <summary>The scripts <paramref name="userName"/> scheduled, from the task paths in <c>\SeaShell\</c>.</summary>
	public static IReadOnlyList<ScheduledScript> WindowsScripts(IEnumerable<string> taskPaths, string userName) =>
		taskPaths
			.Select(p => (Path: p, Name: SeaShellTaskNames.ScriptOf(p, userName)))
			.Where(t => t.Name != null)
			.Select(t => new ScheduledScript(t.Name!, t.Path))
			.ToArray();

	/// <summary>
	/// The deferred removal after a relocated uninstall (Windows): the version
	/// directory the original seashell held goes only if it is still retired
	/// when the removal runs, under the target's install lock. Null: remove it;
	/// otherwise why it stays — an immediate reinstall of the same version may
	/// have made it current again (or written its <c>daemon.path</c> marker)
	/// between the uninstall and the removal.
	/// </summary>
	public static string? KeepDeferredDir(Version? dirVersion, Version? current, bool hasDaemonMarker)
	{
		if (dirVersion == null) return "not a version directory";
		if (current != null && dirVersion == current) return $"installed again: current points to v{current}";
		if (hasDaemonMarker) return "installed again: it has a daemon.path marker";
		return null;
	}

	/// <summary>
	/// What is left in a data directory after an uninstall pass, for its verdict:
	/// the install lock (Linux: a file in the data directory) is held by the pass
	/// itself and goes when it is released; <paramref name="keptShared"/> are
	/// shared entries the pass kept because another process uses them (the
	/// script mutex directory), already reported.
	/// </summary>
	public static IReadOnlyList<string> DataDirLeftovers(IEnumerable<string> entryNames, IEnumerable<string>? keptShared = null)
	{
		var kept = new HashSet<string>(keptShared ?? Array.Empty<string>(), StringComparer.Ordinal) { InstallLock.FileName };
		return entryNames.Where(n => !kept.Contains(n)).ToArray();
	}

	/// <summary>
	/// Linux: the order of the passes. Root's pass first — it runs this
	/// bootstrapper through sudo, and when that is the installed copy the user
	/// pass deletes it.
	/// </summary>
	public static IReadOnlyList<ScopeParts> LinuxPassOrder(ScopeParts parts)
	{
		var order = new List<ScopeParts>();
		if ((parts & ScopeParts.System) != 0) order.Add(ScopeParts.System);
		if ((parts & ScopeParts.User) != 0) order.Add(ScopeParts.User);
		return order;
	}

	/// <summary>Tag of a scheduled-script crontab line (<c>ScriptScheduler</c>).</summary>
	public const string ScriptCronTag = "# seashell:";

	/// <summary>The scheduled scripts in a crontab (lines tagged <c># seashell:{stem}</c>).</summary>
	public static IReadOnlyList<ScheduledScript> CronScripts(IEnumerable<string> crontab) =>
		crontab
			.Select(l => (Line: l, Index: l.IndexOf(ScriptCronTag, StringComparison.Ordinal)))
			.Where(t => t.Index >= 0)
			.Select(t => new ScheduledScript(t.Line[(t.Index + ScriptCronTag.Length)..].Trim(), t.Line))
			.ToArray();

	/// <summary>
	/// The crontab after uninstall: the automatic-update line always goes;
	/// scheduled-script lines only with <paramref name="purge"/>. Every other
	/// line is the user's and stays as it is.
	/// </summary>
	public static List<string> CrontabAfter(IEnumerable<string> crontab, bool purge)
	{
		var scripts = purge ? CronScripts(crontab).Select(s => s.Entry).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
		return AutoUpdatePlan.WithCronLine(crontab.Where(l => !scripts.Contains(l)), null);
	}
}
