using System;
using System.IO;

namespace SeaShell.Installer;

/// <summary>What to do about a global <c>SeaShell</c> dotnet tool during an install.</summary>
public enum GlobalToolAction
{
	/// <summary>No global tool, or one at least as new as this install: leave it.</summary>
	None,

	/// <summary>Older than this install and not in use by this run: <c>dotnet tool uninstall -g SeaShell</c>.</summary>
	Remove,

	/// <summary>Older, but this run is executing from it: print the one-line fix instead.</summary>
	Hint,
}

/// <summary>
/// An older global tool is superseded by the installed copy in
/// <c>current/bin</c> and would otherwise answer <c>seashell update</c> with
/// its old alias. It is removed unless this run is executing from it.
/// </summary>
public static class GlobalToolPolicy
{
	/// <summary><c>~/.dotnet/tools/.store/seashell</c>: one subdirectory per installed version.</summary>
	public static string StoreDirIn(string userProfile) =>
		Path.Combine(userProfile, ".dotnet", "tools", ".store", "seashell");

	/// <summary>True when <paramref name="path"/> lies inside the global tool's store.</summary>
	public static bool IsInStore(string? path, string storeDir)
	{
		if (string.IsNullOrEmpty(path)) return false;
		var full = Path.GetFullPath(path);
		var store = Path.GetFullPath(storeDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		return full.StartsWith(store, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	/// <param name="globalVersion">Installed global tool version; null when there is none.</param>
	/// <param name="installingVersion">Version this run installs.</param>
	/// <param name="inUseByThisRun">
	/// This bootstrapper, or the <c>seashell update</c> that started it, runs
	/// from the global tool.
	/// </param>
	public static GlobalToolAction Decide(Version? globalVersion, Version installingVersion, bool inUseByThisRun)
	{
		if (globalVersion == null || globalVersion >= installingVersion) return GlobalToolAction.None;
		return inUseByThisRun ? GlobalToolAction.Hint : GlobalToolAction.Remove;
	}
}
