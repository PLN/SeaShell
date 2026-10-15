using System;
using System.Runtime.InteropServices;

namespace SeaShell.Bootstrapper;

/// <summary>Privilege level of the calling process.</summary>
internal static class Elevation
{
	/// <summary>Windows: token is elevated. Always false off Windows.</summary>
	public static bool IsElevated(bool isWindows)
	{
		if (!isWindows || !OperatingSystem.IsWindows()) return false;
		try
		{
			using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
			var principal = new System.Security.Principal.WindowsPrincipal(identity);
			return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
		}
		catch { return false; }
	}

	/// <summary>Windows: elevated. Unix: effective uid is root.</summary>
	public static bool IsElevatedOrRoot(bool isWindows)
	{
		if (isWindows) return IsElevated(true);
		try
		{
			[DllImport("libc", EntryPoint = "geteuid")]
			static extern uint GetEuid();
			return GetEuid() == 0;
		}
		catch { return false; }
	}
}