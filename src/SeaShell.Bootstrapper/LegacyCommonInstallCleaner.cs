using System;
using System.IO;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Remove leftover pre-0.5.0 common-install state: C:\ProgramData\seashell on
/// Windows, /etc/profile.d/seashell.sh on Linux. PATH entries are scrubbed
/// from user scope always; from machine scope only when elevated. The on-disk
/// directory/file is removed only when we can — otherwise just log.
/// </summary>
internal static class LegacyCommonInstallCleaner
{
	public static void Clean(bool isWindows)
	{
		if (isWindows)
		{
			PathEnvironment.StripUserEntriesWhere(LegacyInstallPaths.IsUnderProgramDataSeashell);
			var legacyDir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
				"seashell");

			if (Elevation.IsElevated(true))
			{
				PathEnvironment.StripMachineEntriesWhere(LegacyInstallPaths.IsUnderProgramDataSeashell);
				if (Directory.Exists(legacyDir))
				{
					try
					{
						Directory.Delete(legacyDir, recursive: true);
						Console.WriteLine($"  Removed legacy installation: {legacyDir}");
					}
					catch (Exception ex)
					{
						Console.WriteLine($"  Warning: could not remove {legacyDir}: {ex.Message}");
					}
				}
			}
			else if (Directory.Exists(legacyDir))
			{
				Console.WriteLine($"  NOTE: {legacyDir} still exists; re-run elevated to remove.");
			}
		}
		else
		{
			const string sysProfile = "/etc/profile.d/seashell.sh";
			if (!File.Exists(sysProfile)) return;
			if (Elevation.IsElevatedOrRoot(false))
			{
				try
				{
					File.Delete(sysProfile);
					Console.WriteLine($"  Removed legacy: {sysProfile}");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"  Warning: could not remove {sysProfile}: {ex.Message}");
				}
			}
			else
			{
				Console.WriteLine($"  NOTE: {sysProfile} exists; re-run with sudo to remove.");
			}
		}
	}
}