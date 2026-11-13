using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>A relaunched pass's exit code and, when it got that far, its summary.</summary>
internal readonly record struct RelaunchResult(int ExitCode, InstallRunSummary? Summary);

/// <summary>
/// Runs passes in a second instance of this bootstrapper: elevated through
/// UAC on Windows (one prompt, output streamed back through a tailed log),
/// as root through sudo on Linux. The relaunch always gets its passes
/// explicitly (<c>--pass</c>), so it never rewrites the recorded scope; it
/// hands its <see cref="InstallRunSummary"/> back through a result file and
/// the invoking process records.
/// </summary>
internal static class PassRelaunch
{
	/// <summary>Arguments after the command line of this bootstrapper itself.</summary>
	public static List<string> PassArguments(ScopeParts passes, ScopeParts detect, bool ifNeeded, bool keepGlobalTool, string resultFile, string command = "install")
	{
		var args = new List<string> { command, "--pass", ScopeTokens.FormatList(passes) };
		if (detect != ScopeParts.None) { args.Add("--detect"); args.Add(ScopeTokens.FormatList(detect)); }
		if (ifNeeded) args.Add("--if-needed");
		if (keepGlobalTool) args.Add("--keep-global-tool");
		args.Add("--result-file");
		args.Add(resultFile);
		return args;
	}

	/// <summary>Windows: relaunch the install passes elevated (UAC prompt) and stream the child's output into this console.</summary>
	public static Task<RelaunchResult> ElevatedAsync(ScopeParts passes, ScopeParts detect, bool ifNeeded, bool keepGlobalTool) =>
		ElevatedAsync(resultFile => PassArguments(passes, detect, ifNeeded, keepGlobalTool, resultFile), ScopeTokens.Describe(passes | detect));

	/// <summary>
	/// Windows: run this bootstrapper elevated with the arguments
	/// <paramref name="buildArguments"/> returns for the result file, stream
	/// the child's output into this console and read its summary back.
	/// </summary>
	public static async Task<RelaunchResult> ElevatedAsync(Func<string, List<string>> buildArguments, string description)
	{
		var self = DotnetHost.Self();
		if (self == null) return new RelaunchResult(1, null);

		var id = Guid.NewGuid().ToString("N");
		var childLog = Path.Combine(Path.GetTempPath(), $"seashell-install-{id}.log");
		var resultFile = CreateResultFile(id);

		var psi = new ProcessStartInfo
		{
			FileName = self.FileName,
			UseShellExecute = true,        // required for the UAC verb
			Verb = "runas",
			WindowStyle = ProcessWindowStyle.Hidden,
		};
		foreach (var a in self.PrefixArguments) psi.ArgumentList.Add(a);
		foreach (var a in buildArguments(resultFile)) psi.ArgumentList.Add(a);
		psi.ArgumentList.Add("--log-file");
		psi.ArgumentList.Add(childLog);
		psi.ArgumentList.Add("--invoker-sid");
		psi.ArgumentList.Add(SeaShellIdentity.Current.Token);

		Console.WriteLine($"  Requesting elevation (UAC prompt) for: {description}...");
		Process child;
		try
		{
			child = Process.Start(psi)!;
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			Console.Error.WriteLine($"  ERROR: elevation was declined or failed ({ex.Message}).");
			TryDelete(resultFile);
			return new RelaunchResult(1, null);
		}

		await TailLogUntilExitAsync(childLog, child);
		return new RelaunchResult(child.ExitCode, ReadResult(resultFile));
	}

	/// <summary>
	/// Linux: run the root pass through sudo. <c>-H</c> gives root its own
	/// HOME (so its <c>~/.local/bin</c> links and NuGet configuration are
	/// root's, never the invoking user's); <c>DOTNET_ROOT</c> and <c>PATH</c>
	/// are passed so the apphost and <c>dotnet</c> resolve as they do here.
	/// </summary>
	public static RelaunchResult Sudo(bool ifNeeded, bool keepGlobalTool) =>
		Sudo(resultFile => PassArguments(ScopeParts.System, ScopeParts.None, ifNeeded, keepGlobalTool, resultFile), "root pass");

	/// <summary>Linux: run this bootstrapper through sudo with the given arguments (see <see cref="Sudo(bool, bool)"/>).</summary>
	public static RelaunchResult Sudo(Func<string, List<string>> buildArguments, string description)
	{
		var self = DotnetHost.Self();
		if (self == null) return new RelaunchResult(1, null);

		var resultFile = CreateResultFile(Guid.NewGuid().ToString("N"));
		var args = new List<string> { "-H", "env" };
		var dotnetRoot = DotnetHost.Root();
		if (!string.IsNullOrEmpty(dotnetRoot)) args.Add($"DOTNET_ROOT={dotnetRoot}");
		args.Add($"PATH={Environment.GetEnvironmentVariable("PATH") ?? ""}");
		args.Add(self.FileName);
		args.AddRange(self.PrefixArguments);
		args.AddRange(buildArguments(resultFile));

		Console.WriteLine();
		Console.WriteLine($"  Running the {description} through sudo:");
		Console.WriteLine($"    {DotnetHost.Describe("sudo", args)}");
		int exit;
		try
		{
			exit = DotnetHost.RunInherited("sudo", args);
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			Console.Error.WriteLine($"  ERROR: could not run sudo ({ex.Message}). The {description} did not run; root's install was not changed.");
			TryDelete(resultFile);
			return new RelaunchResult(1, null);
		}

		var summary = ReadResult(resultFile);
		if (exit != 0 && summary == null)
			Console.Error.WriteLine($"  ERROR: the {description} did not run (sudo exited {exit}); root's install was not changed.");
		return new RelaunchResult(exit, summary);
	}

	// Created by the invoker, in a directory of its own: the invoker owns both,
	// root can write into them, and a private (non-sticky, not world-writable)
	// directory keeps Linux fs.protected_regular from refusing root's write
	// to a file owned by someone else in /tmp.
	private static string CreateResultFile(string id)
	{
		var dir = Path.Combine(Path.GetTempPath(), $"seashell-relaunch-{id}");
		Directory.CreateDirectory(dir);
		var path = Path.Combine(dir, "result.txt");
		File.WriteAllText(path, "");
		return path;
	}

	private static InstallRunSummary? ReadResult(string path)
	{
		try
		{
			var content = File.ReadAllText(path);
			return string.IsNullOrWhiteSpace(content) ? null : InstallRunSummary.Parse(content);
		}
		catch { return null; }
		finally { TryDelete(path); }
	}

	private static void TryDelete(string resultFile)
	{
		try { File.Delete(resultFile); } catch { }
		try { Directory.Delete(Path.GetDirectoryName(resultFile)!, false); } catch { }
	}

	// Stream a log file the elevated child is appending to into this console,
	// until the child exits; then drain the remainder and clean up.
	private static async Task TailLogUntilExitAsync(string logPath, Process child)
	{
		long position = 0;

		async Task DrainAsync()
		{
			if (!File.Exists(logPath)) return;
			try
			{
				using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				if (fs.Length <= position) return;
				fs.Position = position;
				using var reader = new StreamReader(fs);
				var chunk = await reader.ReadToEndAsync();
				position = fs.Length;
				Console.Write(chunk);
			}
			catch (IOException) { } // file mid-create; retry next tick
		}

		while (!child.HasExited)
		{
			await DrainAsync();
			await Task.Delay(200);
		}
		await child.WaitForExitAsync(); // settle ExitCode
		await DrainAsync();
		try { File.Delete(logPath); } catch { }
	}
}
