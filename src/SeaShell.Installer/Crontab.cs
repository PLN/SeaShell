using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SeaShell.Installer;

/// <summary>
/// The invoking identity's crontab (Linux). <see cref="Read"/> separates "no
/// crontab yet" (an empty list) from "could not run crontab" (null), so a
/// failed read is never written back as an empty table.
/// </summary>
public static class Crontab
{
	public static List<string>? Read()
	{
		try
		{
			var psi = new ProcessStartInfo("crontab", "-l")
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			using var proc = Process.Start(psi)!;
			var output = proc.StandardOutput.ReadToEnd();
			var error = proc.StandardError.ReadToEnd();
			proc.WaitForExit(5_000);
			if (proc.ExitCode == 0) return output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
			// "no crontab for user": an empty table, not an error.
			return error.Contains("no crontab", StringComparison.OrdinalIgnoreCase) ? new List<string>() : null;
		}
		catch { return null; }
	}

	public static bool Write(IReadOnlyList<string> lines)
	{
		var tmpFile = Path.GetTempFileName();
		try
		{
			var content = lines.ToList();
			while (content.Count > 0 && string.IsNullOrWhiteSpace(content[^1])) content.RemoveAt(content.Count - 1);
			File.WriteAllText(tmpFile, content.Count == 0 ? "" : string.Join("\n", content) + "\n");

			var psi = new ProcessStartInfo("crontab", tmpFile)
			{
				UseShellExecute = false,
				RedirectStandardError = true,
			};
			using var proc = Process.Start(psi)!;
			proc.WaitForExit(5_000);
			return proc.ExitCode == 0;
		}
		catch { return false; }
		finally
		{
			try { File.Delete(tmpFile); } catch { }
		}
	}
}
