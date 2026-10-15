using System;
using System.IO;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Makes SeaShell reachable by the interactive user: the bin directory in the
/// Windows user PATH, or a ~/.local/bin symlink on Unix. Never applied to the
/// SYSTEM target — that would edit the invoking admin's HKCU, and SYSTEM
/// consumers find binaries via markers rather than PATH.
/// </summary>
internal static class PathEnvironment
{
	/// <summary>Put the current bin directory on the user's PATH.</summary>
	public static void Add(InstallTarget target)
	{
		var pathDir = SeaShellPaths.CurrentBinDir;

		if (target.IsWindows)
		{
			var existingPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
			if (!existingPath.Contains(pathDir, StringComparison.OrdinalIgnoreCase))
			{
				Environment.SetEnvironmentVariable("PATH", $"{pathDir};{existingPath}", EnvironmentVariableTarget.User);
				Console.WriteLine($"  Added {pathDir} to user PATH");
				Console.WriteLine("  NOTE: Restart your terminal for PATH changes to take effect.");
			}
		}
		else
		{
			var localBin = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".local", "bin");
			Directory.CreateDirectory(localBin);

			var seaLink = Path.Combine(localBin, "sea");
			var seaTarget = Path.Combine(pathDir, "sea");
			try
			{
				if (File.Exists(seaLink)) File.Delete(seaLink);
				File.CreateSymbolicLink(seaLink, seaTarget);
				Console.WriteLine($"  Symlinked sea -> {localBin}/sea");
			}
			catch (Exception ex) { Console.Error.WriteLine($"  Warning: symlink failed: {ex.Message}"); }
		}
	}

	/// <summary>Undo <see cref="Add"/>. Drops pre-0.5.0 layout entries too.</summary>
	public static void Remove(InstallTarget target)
	{
		if (target.IsWindows)
		{
			// Drop both the current junction path and the pre-0.5.0 flat bin path.
			StripEntries(new[]
			{
				SeaShellPaths.CurrentBinDir,
				Path.Combine(SeaShellPaths.DataDir, "bin"),
			});
		}
		else
		{
			var seaLink = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".local", "bin", "sea");
			if (File.Exists(seaLink))
			{
				try { File.Delete(seaLink); Console.WriteLine("  Removed symlink"); }
				catch { }
			}
		}
	}

	/// <summary>Drop a stale flat-layout bin entry left by a pre-0.5.0 install.</summary>
	public static void StripLegacyBinEntries(InstallTarget target) =>
		StripEntries(new[] { Path.Combine(target.DataDir, "bin") });

	/// <summary>Drop user-scope PATH entries under the pre-0.5.0 common install.</summary>
	public static void StripUserEntriesWhere(Func<string, bool> isUnwanted) =>
		Apply(EnvironmentVariableTarget.User, WindowsPathEntries.StripWhere(Read(EnvironmentVariableTarget.User), isUnwanted));

	/// <summary>Machine-scope PATH entries; needs elevation to write.</summary>
	public static void StripMachineEntriesWhere(Func<string, bool> isUnwanted) =>
		Apply(EnvironmentVariableTarget.Machine, WindowsPathEntries.StripWhere(Read(EnvironmentVariableTarget.Machine), isUnwanted));

	private static void StripEntries(string[] unwanted) =>
		Apply(EnvironmentVariableTarget.User,
			WindowsPathEntries.Strip(Read(EnvironmentVariableTarget.User), unwanted));

	// Reading the persisted PATH can be denied by policy; that is not an error
	// worth failing the install over.
	private static string Read(EnvironmentVariableTarget scope)
	{
		try { return Environment.GetEnvironmentVariable("PATH", scope) ?? ""; }
		catch (System.Security.SecurityException)
		{
			Console.WriteLine($"  PATH ({scope}): access denied, skipping");
			return "";
		}
	}

	private static void Apply(EnvironmentVariableTarget scope, PathStripResult stripped)
	{
		if (!stripped.Changed) return;

		try
		{
			Environment.SetEnvironmentVariable("PATH", stripped.Path, scope);
			Console.WriteLine($"  Removed stale PATH entries ({scope})");
		}
		catch (System.Security.SecurityException)
		{
			Console.WriteLine($"  PATH ({scope}): write denied, skipping");
		}
	}
}