using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SeaShell.Cli;

/// <summary>
/// Registers/unregisters file associations for SeaShell scripts.
/// Uses HKCU\Software\Classes (per-user, no elevation needed).
///
/// Equivalent to:
///   assoc .cs=SeaShell_cs
///   ftype SeaShell_cs="C:\...\sea.exe" "%1" %*
/// </summary>
static class FileAssoc
{
	public static int Associate(string extension)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Console.WriteLine("File associations are Windows-only.");
			return 1;
		}

		if (!extension.StartsWith('.'))
			extension = "." + extension;

		var progId = $"SeaShell_{extension.TrimStart('.').Replace(".", "_")}";
		var exePath = GetExePathForExtension(extension);

		try
		{
			// Register the ProgID: SeaShell_cs (or SeaShell_csw, etc.)
			using var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}");
			progKey.SetValue("", $"SeaShell {extension} Script");

			using var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell\open\command");
			commandKey.SetValue("", $"\"{exePath}\" \"%1\" %*");

			// Associate the extension with the ProgID
			using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}");
			extKey.SetValue("", progId);

			// Notify the shell of the change
			SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

			Console.WriteLine($"  Associated {extension} -> {progId}");
			Console.WriteLine($"  Command: \"{exePath}\" \"%1\" %*");
			Console.WriteLine($"  To set as default: right-click a {extension} file -> Open with -> Always");
			if (extension.Equals(".csw", StringComparison.OrdinalIgnoreCase))
				Console.WriteLine($"  Task Scheduler: use \"{System.IO.Path.GetFileName(exePath)}\" as action, script path as argument");
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"  Failed: {ex.Message}");
			return 1;
		}
	}

	public static int Unassociate(string extension)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Console.WriteLine("File associations are Windows-only.");
			return 1;
		}

		if (!extension.StartsWith('.'))
			extension = "." + extension;

		var progId = $"SeaShell_{extension.TrimStart('.').Replace(".", "_")}";

		try
		{
			// Remove the extension association (only if it points to our ProgID)
			using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: false);
			if (extKey?.GetValue("")?.ToString() == progId)
				Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{extension}", throwOnMissingSubKey: false);

			// Remove the ProgID
			Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);

			SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

			Console.WriteLine($"  Removed {extension} association");
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"  Failed: {ex.Message}");
			return 1;
		}
	}

	private static string GetExePathForExtension(string extension)
	{
		// .csw → seaw.exe (Windows subsystem, zero flash)
		// .cs, .csc, everything else → sea.exe (Console subsystem)
		var isWindowExt = extension.Equals(".csw", StringComparison.OrdinalIgnoreCase);
		var targetExe = isWindowExt ? "seaw.exe" : "sea.exe";

		// Try to find the target exe next to the current binary
		var currentExe = Environment.ProcessPath;
		if (!string.IsNullOrEmpty(currentExe))
		{
			var dir = System.IO.Path.GetDirectoryName(currentExe);
			if (dir != null)
			{
				var candidate = System.IO.Path.Combine(dir, targetExe);
				if (System.IO.File.Exists(candidate))
					return candidate;
			}
		}

		// Fallback: AppContext.BaseDirectory
		return System.IO.Path.Combine(AppContext.BaseDirectory, targetExe);
	}

	[DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
