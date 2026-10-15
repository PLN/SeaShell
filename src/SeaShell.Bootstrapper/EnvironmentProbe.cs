using System;
using System.Diagnostics;
using System.IO;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Read-only probes of the machine and of already-installed binaries. Every
/// probe takes the state it needs as an argument — none of them read captured
/// install state.
/// </summary>
internal static class EnvironmentProbe
{
	/// <summary>
	/// Locate the <c>sea</c> launcher: this pass's extracted bin directory
	/// first, then the <c>current</c> junction of the ambient identity.
	/// </summary>
	public static string? FindSea(string binDir, bool isWindows)
	{
		var name = isWindows ? "sea.exe" : "sea";
		var path = Path.Combine(binDir, name);
		if (File.Exists(path)) return path;
		path = Path.Combine(SeaShellPaths.CurrentBinDir, name);
		if (File.Exists(path)) return path;
		return null;
	}

	/// <summary>Runtime identifier of the embedded archive to extract.</summary>
	public static string CurrentRid(bool isWindows)
	{
		if (isWindows) return "win-x64";
		try
		{
			var lddOutput = RunProcess("ldd", "--version");
			if (lddOutput.Contains("musl", StringComparison.OrdinalIgnoreCase))
				return "linux-musl-x64";
		}
		catch { }
		return "linux-x64";
	}

	/// <summary>Run a process and return stdout+stderr. Empty string on any failure.</summary>
	public static string RunProcess(string exe, string args)
	{
		try
		{
			var psi = new ProcessStartInfo(exe, args)
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			using var proc = Process.Start(psi);
			if (proc == null) return "";
			var output = proc.StandardOutput.ReadToEnd();
			var error = proc.StandardError.ReadToEnd();
			proc.WaitForExit(10_000);
			return output + error;
		}
		catch { return ""; }
	}

	/// <summary>
	/// Probe a named pipe without speaking the protocol. Returns a
	/// human-readable state: "reachable", "present (ACL-blocked)", or "not
	/// running". Used by <c>seashell status --system</c> to classify each
	/// enumerated pipe.
	/// </summary>
	public static string ProbeSystemPipe(string pipeName, bool isWindows)
	{
		if (!isWindows) return "not running";
		try
		{
			using var client = new System.IO.Pipes.NamedPipeClientStream(
				".", pipeName, System.IO.Pipes.PipeDirection.InOut);
			client.Connect(200);
			return "reachable";
		}
		catch (TimeoutException) { return "not running"; }
		catch (System.IO.FileNotFoundException) { return "not running"; }
		catch (UnauthorizedAccessException) { return "present (ACL-blocked)"; }
		catch { return "not running"; }
	}
}