using System;
using System.IO;

namespace SeaShell.Installer;

/// <summary>What a task in <c>\SeaShell\</c> is, by its name.</summary>
public enum SeaShellTaskKind
{
	/// <summary><c>SeaShell Daemon (identity) version</c>.</summary>
	Daemon,

	/// <summary><c>SeaShell Elevator (identity) version</c>.</summary>
	Elevator,

	/// <summary>
	/// <c>SeaShell Update (identity)</c>: the opt-in automatic update. It runs
	/// the very install that stops daemons and elevators, so nothing that ends
	/// or deletes those may ever match it.
	/// </summary>
	Update,

	/// <summary><c>{user} - {script}</c>: a script scheduled with <c>seashell schedule</c>.</summary>
	Script,

	/// <summary>Anything else; never touched.</summary>
	Other,
}

/// <summary>
/// Name rules for SeaShell's Task Scheduler entries. Tasks are named
/// <c>SeaShell Daemon (identity) version</c> / <c>SeaShell Elevator (identity) version</c>;
/// pre-0.6.0 names omit the version but carry the same identity parenthetical,
/// so matching on the prefix plus <c>(DisplayName)</c> still finds them. The
/// automatic update task is <c>SeaShell Update (identity)</c> (no version: its
/// action runs <c>current\bin\seashell.exe</c>, which survives version changes).
/// </summary>
public static class SeaShellTaskNames
{
	public const string DaemonPrefix = "SeaShell Daemon ";
	public const string ElevatorPrefix = "SeaShell Elevator ";
	public const string UpdatePrefix = "SeaShell Update ";

	/// <summary>Separator between user name and script stem of a scheduled script task.</summary>
	public const string ScriptSeparator = " - ";

	/// <summary>Task name without the <c>\SeaShell\</c> folder prefix.</summary>
	public static string NameOf(string taskPath) =>
		taskPath.StartsWith(TaskQuery.Folder, StringComparison.Ordinal)
			? taskPath[TaskQuery.Folder.Length..]
			: Path.GetFileName(taskPath);

	/// <summary>Full path of the automatic update task of an identity.</summary>
	public static string UpdateTaskPath(string displayName) => $"{TaskQuery.Folder}{UpdatePrefix}({displayName})";

	public static SeaShellTaskKind KindOf(string taskPath)
	{
		var name = NameOf(taskPath);
		if (name.StartsWith(DaemonPrefix, StringComparison.OrdinalIgnoreCase)) return SeaShellTaskKind.Daemon;
		if (name.StartsWith(ElevatorPrefix, StringComparison.OrdinalIgnoreCase)) return SeaShellTaskKind.Elevator;
		if (name.StartsWith(UpdatePrefix, StringComparison.OrdinalIgnoreCase)) return SeaShellTaskKind.Update;
		return name.Contains(ScriptSeparator, StringComparison.Ordinal) ? SeaShellTaskKind.Script : SeaShellTaskKind.Other;
	}

	/// <summary>
	/// True for a SeaShell daemon or elevator task (any identity/version) —
	/// the tasks an install stops, replaces and cleans up. Never the update
	/// task, and never a scheduled script.
	/// </summary>
	public static bool IsSeaShellTask(string taskPath) =>
		KindOf(taskPath) is SeaShellTaskKind.Daemon or SeaShellTaskKind.Elevator;

	/// <summary>True when the task was registered for the given target identity.</summary>
	public static bool IsForTarget(string taskPath, string targetDisplayName) =>
		NameOf(taskPath).Contains($"({targetDisplayName})", StringComparison.Ordinal);

	/// <summary>
	/// True when this task is a daemon/elevator task of the install target.
	/// Used both when stopping instances before a destructive install step and
	/// when sweeping orphan tasks during uninstall — a pass must never end or
	/// delete another identity's tasks, nor the update task that may be
	/// running this very install.
	/// </summary>
	public static bool MatchesTarget(string taskPath, string targetDisplayName) =>
		IsSeaShellTask(taskPath) && IsForTarget(taskPath, targetDisplayName);

	/// <summary>True for the automatic update task of the target identity.</summary>
	public static bool IsUpdateTaskOf(string taskPath, string targetDisplayName) =>
		KindOf(taskPath) == SeaShellTaskKind.Update && IsForTarget(taskPath, targetDisplayName);

	/// <summary>
	/// The script stem of a task scheduled by <paramref name="userName"/>
	/// (<c>{user} - {stem}</c>, as <c>ScriptScheduler</c> names them); null for
	/// any other task.
	/// </summary>
	public static string? ScriptOf(string taskPath, string userName)
	{
		if (KindOf(taskPath) != SeaShellTaskKind.Script) return null;
		var name = NameOf(taskPath);
		var prefix = userName + ScriptSeparator;
		return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length
			? name[prefix.Length..]
			: null;
	}
}
