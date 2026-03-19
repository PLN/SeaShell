using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SeaShell.Cli;

static class ConsoleHelper
{
	/// <summary>
	/// If the console is ephemeral (double-clicked), show an interactive countdown
	/// before the window closes. The script controls the duration via Sea.ExitDelay.
	/// </summary>
	public static void ExitDelay(bool isConsoleEphemeral)
	{
		if (!isConsoleEphemeral) return;

		// Read the delay the script requested (written by Sea's ProcessExit handler)
		var delay = 7; // default
		try
		{
			var manifestPath = Environment.GetEnvironmentVariable("SEASHELL_MANIFEST");
			if (manifestPath == null)
			{
				// No manifest (compilation failed before artifacts were written).
				// Check if ScriptRunner stored it for us.
				manifestPath = ScriptRunner.LastManifestPath;
			}
			if (manifestPath != null)
			{
				var delayFile = manifestPath + ".exitdelay";
				if (File.Exists(delayFile))
				{
					int.TryParse(File.ReadAllText(delayFile).Trim(), out delay);
					try { File.Delete(delayFile); } catch { }
				}
			}
		}
		catch { }

		if (delay > 0 && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			ConsoleDelayer.Delay(delay);
	}

	/// <summary>
	/// Detect whether this console window will close when the process exits
	/// (e.g., double-clicked .exe with no parent shell).
	/// </summary>
	public static bool IsConsoleEphemeral()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return false; // Linux: no ephemeral console concern

		try
		{
			var list = new uint[8];
			var count = GetConsoleProcessList(list, (uint)list.Length);
			// Double-clicked: apphost (sea.exe) + dotnet runtime = 2
			// Terminal: shell (cmd/pwsh) + apphost + dotnet = 3+
			return count <= 2;
		}
		catch { return false; }
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
