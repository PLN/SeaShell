using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SeaShell.Installer;

/// <summary>
/// Registers/unregisters file associations for SeaShell scripts.
/// Uses HKCU\Software\Classes (per-user, no elevation needed).
///
/// Equivalent to:
///   assoc .cs=SeaShell_cs
///   ftype SeaShell_cs="C:\...\sea.exe" "%1" %*
/// </summary>
public static class FileAssoc
{
	public static int Associate(string extension, string? exePath = null, Action<string>? log = null)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			log?.Invoke("File associations are Windows-only.");
			return 1;
		}

		if (!extension.StartsWith('.'))
			extension = "." + extension;

		var progId = $"SeaShell_{extension.TrimStart('.').Replace(".", "_")}";
		exePath ??= GetExePathForExtension(extension);

		try
		{
			using var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}");
			progKey.SetValue("", $"SeaShell {extension} Script");

			using var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell\open\command");
			commandKey.SetValue("", $"\"{exePath}\" \"%1\" %*");

			using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}");
			extKey.SetValue("", progId);

			SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

			log?.Invoke($"Associated {extension} -> {progId}");
			log?.Invoke($"Command: \"{exePath}\" \"%1\" %*");
			log?.Invoke($"To set as default: right-click a {extension} file -> Open with -> Always");
			if (extension.Equals(".csw", StringComparison.OrdinalIgnoreCase))
				log?.Invoke($"Task Scheduler: use \"{Path.GetFileName(exePath)}\" as action, script path as argument");
			return 0;
		}
		catch (Exception ex)
		{
			log?.Invoke($"Failed: {ex.Message}");
			return 1;
		}
	}

	public static int Unassociate(string extension, Action<string>? log = null)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			log?.Invoke("File associations are Windows-only.");
			return 1;
		}

		if (!extension.StartsWith('.'))
			extension = "." + extension;

		var progId = $"SeaShell_{extension.TrimStart('.').Replace(".", "_")}";

		try
		{
			using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: false);
			if (extKey?.GetValue("")?.ToString() == progId)
				Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{extension}", throwOnMissingSubKey: false);

			Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);

			SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

			log?.Invoke($"Removed {extension} association");
			return 0;
		}
		catch (Exception ex)
		{
			log?.Invoke($"Failed: {ex.Message}");
			return 1;
		}
	}

	private static string GetExePathForExtension(string extension)
	{
		// .csw → seaw.exe (Windows subsystem, zero flash)
		// .cs, .csc, everything else → sea.exe (Console subsystem)
		var isWindowExt = extension.Equals(".csw", StringComparison.OrdinalIgnoreCase);
		var targetExe = isWindowExt ? "seaw.exe" : "sea.exe";

		// Prefer the current version's bin dir via the junction — survives upgrades.
		var candidate = Path.Combine(SeaShellPaths.CurrentBinDir, targetExe);
		if (File.Exists(candidate)) return candidate;

		// Fallback: AppContext.BaseDirectory (caller's directory)
		return Path.Combine(AppContext.BaseDirectory, targetExe);
	}

	[DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
