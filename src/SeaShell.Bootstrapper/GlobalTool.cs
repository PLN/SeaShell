using System;
using System.IO;
using System.Linq;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// The global <c>SeaShell</c> dotnet tool (<c>dotnet tool install -g</c>) of
/// the current account. Decisions come from <see cref="GlobalToolPolicy"/>.
/// </summary>
internal static class GlobalTool
{
	private static string StoreDir =>
		GlobalToolPolicy.StoreDirIn(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

	/// <summary>Highest installed version in the global tool store; null when not installed.</summary>
	public static Version? InstalledVersion()
	{
		try
		{
			if (!Directory.Exists(StoreDir)) return null;
			return Directory.GetDirectories(StoreDir)
				.Select(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : null)
				.Where(v => v != null)
				.Max();
		}
		catch { return null; }
	}

	/// <summary>This bootstrapper itself runs from the global tool store.</summary>
	public static bool RunningFromIt() => GlobalToolPolicy.IsInStore(AppContext.BaseDirectory, StoreDir);

	/// <summary>The global tool's command shim (<c>~/.dotnet/tools/seashell[.exe]</c>).</summary>
	public static string ShimPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools",
		OperatingSystem.IsWindows() ? "seashell.exe" : "seashell");

	/// <summary>
	/// The global tool, when it is newer than the user's installed version (or
	/// nothing is installed) and this process is not running from it.
	/// </summary>
	public static (string Path, Version Version, Version? Installed)? NewerThanInstalled()
	{
		if (RunningFromIt() || !File.Exists(ShimPath) || InstalledVersion() is not { } global) return null;
		var installed = VersionDiscovery.GetCurrentVersion(SeaShell.SeaShellIdentity.Current.DataDir);
		return installed == null || global > installed ? (ShimPath, global, installed) : null;
	}

	/// <summary>
	/// Remove an older global tool that the installed copy supersedes, unless
	/// this run (or the <c>seashell update</c> that started it) executes from it.
	/// </summary>
	public static void HandleOlderThan(string installingVersion, bool keepRequested)
	{
		if (!Version.TryParse(installingVersion, out var installing)) return;
		var global = InstalledVersion();
		var action = GlobalToolPolicy.Decide(global, installing, RunningFromIt() || keepRequested);
		if (action == GlobalToolAction.None) return;

		if (action == GlobalToolAction.Remove)
		{
			var dotnet = DotnetHost.Resolve();
			Console.WriteLine($"  Removing the older global SeaShell tool v{global} (superseded by the installed seashell)...");
			var output = EnvironmentProbe.RunProcess(dotnet, "tool uninstall -g SeaShell").Trim();
			if (InstalledVersion() == null)
			{
				Console.WriteLine("    removed");
				return;
			}
			Console.WriteLine($"    failed: {output}");
		}

		Console.WriteLine($"  NOTE: the global SeaShell tool v{global} is older than v{installingVersion} and can shadow");
		Console.WriteLine("        the installed 'seashell'. Remove it with: dotnet tool uninstall -g SeaShell");
	}
}
