using System;

namespace SeaShell.Installer;

/// <summary>
/// Whether the caller has the privileges a pass needs. A SYSTEM pass must be
/// elevated (gsudo on Windows, sudo on Linux); a user pass needs nothing.
/// </summary>
public static class ElevationPolicy
{
	/// <summary>
	/// True when the pass may run. Note this is keyed on the <c>--system</c>
	/// request, not on the target: the SYSTEM pass of <c>install --all</c> is
	/// reached from an already-elevated user pass and is not separately gated.
	/// </summary>
	public static bool IsSatisfied(bool systemMode, bool isElevatedOrRoot) =>
		!systemMode || isElevatedOrRoot;
}