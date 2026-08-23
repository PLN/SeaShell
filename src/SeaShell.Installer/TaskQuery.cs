using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SeaShell.Installer;

/// <summary>
/// Thin wrapper over <c>schtasks.exe</c> for SeaShell's Task Scheduler entries.
/// All operations take a full task path (e.g. <c>\SeaShell\SeaShell Daemon (user) 0.5.0.222</c>)
/// to avoid the path/name ambiguity that used to split helpers across Bootstrapper and Invoker.
/// Windows-only; every method is a no-op or returns null/false on other platforms.
/// </summary>
public static class TaskQuery
{
	public const string Folder = @"\SeaShell\";

	/// <summary>
	/// Parse the version suffix from a versioned SeaShell task path, such as
	/// <c>\SeaShell\SeaShell Daemon (SYSTEM) 0.6.1.257</c>.
	/// </summary>
	public static Version? ParseVersionSuffix(string taskPath)
	{
		var name = taskPath.StartsWith(Folder, StringComparison.Ordinal)
			? taskPath[Folder.Length..]
			: Path.GetFileName(taskPath);
		var separator = name.LastIndexOf(' ');
		return separator >= 0 && Version.TryParse(name.AsSpan(separator + 1), out var version)
			? version
			: null;
	}

	/// <summary>Find a task under <see cref="Folder"/> whose name starts with <paramref name="namePrefix"/>.
	/// Returns the first match's full path, or null if none. Useful for looking up the current user's
	/// versioned task when the exact version isn't known.</summary>
	public static string? FindByPrefix(string namePrefix)
	{
		foreach (var path in EnumerateFolder())
		{
			var name = path.StartsWith(Folder, StringComparison.Ordinal)
				? path[Folder.Length..]
				: Path.GetFileName(path);
			if (name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
				return path;
		}
		return null;
	}

	/// <summary>Enumerate every task path registered under <see cref="Folder"/>.
	/// Returns an empty sequence on non-Windows or if schtasks fails.</summary>
	public static IEnumerable<string> EnumerateFolder()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) yield break;

		string? output = null;
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "schtasks.exe",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			psi.ArgumentList.Add("/Query");
			psi.ArgumentList.Add("/TN");
			psi.ArgumentList.Add(Folder);
			psi.ArgumentList.Add("/FO");
			psi.ArgumentList.Add("CSV");
			psi.ArgumentList.Add("/NH");

			using var proc = Process.Start(psi)!;
			output = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit(5_000);
			if (proc.ExitCode != 0) yield break;
		}
		catch { yield break; }

		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = line.Split(',');
			if (parts.Length < 1) continue;
			var path = parts[0].Trim('"', ' ', '\r');
			if (path.Length == 0) continue;
			yield return path;
		}
	}

	/// <summary>Query the state of a task by full path (e.g. "Running", "Ready"). Returns null if absent.</summary>
	public static string? QueryState(string taskPath)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "schtasks.exe",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			psi.ArgumentList.Add("/Query");
			psi.ArgumentList.Add("/TN");
			psi.ArgumentList.Add(taskPath);
			psi.ArgumentList.Add("/FO");
			psi.ArgumentList.Add("CSV");
			psi.ArgumentList.Add("/NH");

			using var proc = Process.Start(psi)!;
			var stdout = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit(5_000);
			if (proc.ExitCode != 0) return null;

			var parts = stdout.Trim().Split(',');
			return parts.Length >= 3 ? parts[2].Trim('"', ' ', '\r', '\n') : null;
		}
		catch { return null; }
	}

	/// <summary>Trigger a task to run. Returns true on schtasks exit 0.</summary>
	public static bool Run(string taskPath) => Invoke("/Run", taskPath);

	/// <summary>Stop a running task. Returns true on schtasks exit 0.</summary>
	public static bool End(string taskPath) => Invoke("/End", taskPath);

	/// <summary>Delete a task. Returns true on success, and also true if the task was already absent.</summary>
	public static bool Delete(string taskPath)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "schtasks.exe",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			psi.ArgumentList.Add("/Delete");
			psi.ArgumentList.Add("/TN");
			psi.ArgumentList.Add(taskPath);
			psi.ArgumentList.Add("/F");

			using var proc = Process.Start(psi)!;
			var stderr = proc.StandardError.ReadToEnd();
			proc.WaitForExit(10_000);
			if (proc.ExitCode == 0) return true;
			return stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
		}
		catch { return false; }
	}

	private static bool Invoke(string action, string taskPath)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "schtasks.exe",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			psi.ArgumentList.Add(action);
			psi.ArgumentList.Add("/TN");
			psi.ArgumentList.Add(taskPath);

			using var proc = Process.Start(psi)!;
			proc.WaitForExit(10_000);
			return proc.ExitCode == 0;
		}
		catch { return false; }
	}
}
