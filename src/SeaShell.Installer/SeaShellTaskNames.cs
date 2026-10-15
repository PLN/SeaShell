using System;
using System.IO;

namespace SeaShell.Installer;

/// <summary>
/// Name rules for SeaShell's Task Scheduler entries. Tasks are named
/// <c>SeaShell Daemon (identity) version</c> / <c>SeaShell Elevator (identity) version</c>;
/// pre-0.6.0 names omit the version but carry the same identity parenthetical,
/// so matching on the prefix plus <c>(DisplayName)</c> still finds them.
/// </summary>
public static class SeaShellTaskNames
{
	public const string DaemonPrefix = "SeaShell Daemon ";
	public const string ElevatorPrefix = "SeaShell Elevator ";

	/// <summary>Task name without the <c>\SeaShell\</c> folder prefix.</summary>
	public static string NameOf(string taskPath) =>
		taskPath.StartsWith(TaskQuery.Folder, StringComparison.Ordinal)
			? taskPath[TaskQuery.Folder.Length..]
			: Path.GetFileName(taskPath);

	/// <summary>True for a SeaShell daemon or elevator task (any identity/version).</summary>
	public static bool IsSeaShellTask(string taskPath)
	{
		var name = NameOf(taskPath);
		return name.StartsWith(DaemonPrefix, StringComparison.OrdinalIgnoreCase)
			|| name.StartsWith(ElevatorPrefix, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>True when the task was registered for the given target identity.</summary>
	public static bool IsForTarget(string taskPath, string targetDisplayName) =>
		NameOf(taskPath).Contains($"({targetDisplayName})", StringComparison.Ordinal);

	/// <summary>
	/// True when this task belongs to SeaShell AND to the install target. Used
	/// both when stopping instances before a destructive install step and when
	/// sweeping orphan tasks during uninstall — a pass must never end or delete
	/// another identity's tasks.
	/// </summary>
	public static bool MatchesTarget(string taskPath, string targetDisplayName) =>
		IsSeaShellTask(taskPath) && IsForTarget(taskPath, targetDisplayName);
}