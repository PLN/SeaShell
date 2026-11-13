using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// The <c>dotnet</c> host this bootstrapper runs on, and how to start this
/// bootstrapper again (self-elevation, the sudo pass).
/// </summary>
internal static class DotnetHost
{
	/// <summary>
	/// The <c>dotnet</c> recorded in a scheduled update entry (<c>--dotnet</c>);
	/// takes precedence over every lookup below.
	/// </summary>
	public static string? Override { get; set; }

	/// <summary>
	/// The dotnet muxer of the runtime this process runs on: the process
	/// itself under <c>dnx</c> / <c>dotnet seashell.dll</c>, else the root of
	/// the loaded runtime (<c>{root}/shared/Microsoft.NETCore.App/{version}</c>),
	/// then <c>DOTNET_ROOT</c>, then PATH.
	/// </summary>
	public static string Resolve()
	{
		if (!string.IsNullOrEmpty(Override)) return Override;
		var exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

		var processPath = Environment.ProcessPath;
		if (processPath != null && SelfCommand.IsDotnetHost(processPath)) return processPath;

		foreach (var root in CandidateRoots())
		{
			var candidate = Path.Combine(root, exeName);
			if (File.Exists(candidate)) return candidate;
		}

		foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			var candidate = Path.Combine(dir, exeName);
			if (File.Exists(candidate)) return candidate;
		}
		return "dotnet";
	}

	/// <summary>Directory of the resolved muxer, for <c>DOTNET_ROOT</c> on the sudo pass.</summary>
	public static string? Root()
	{
		var host = Resolve();
		return Path.IsPathRooted(host) ? Path.GetDirectoryName(host) : Environment.GetEnvironmentVariable("DOTNET_ROOT");
	}

	private static IEnumerable<string> CandidateRoots()
	{
		// typeof(object) lives in {root}/shared/Microsoft.NETCore.App/{version}/.
		var coreLibDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
		if (!string.IsNullOrEmpty(coreLibDir))
		{
			var root = Directory.GetParent(coreLibDir)?.Parent?.Parent;
			if (root != null) yield return root.FullName;
		}
		var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
		if (!string.IsNullOrEmpty(env)) yield return env;
	}

	/// <summary>
	/// How to run this bootstrapper again; null (with an error printed) when
	/// that cannot be determined.
	/// </summary>
	public static SelfCommandLine? Self()
	{
		var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
		var command = SeaShell.Installer.SelfCommand.Build(Environment.ProcessPath, entry);
		if (command == null)
			Console.Error.WriteLine($"seashell: cannot determine how to start this bootstrapper again (process {Environment.ProcessPath ?? "<unknown>"}, entry assembly {entry ?? "<unknown>"}).");
		return command;
	}

	/// <summary>Run a process with this console (output and exit code pass straight through).</summary>
	public static int RunInherited(string fileName, IEnumerable<string> arguments)
	{
		var psi = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };
		foreach (var a in arguments) psi.ArgumentList.Add(a);
		using var proc = Process.Start(psi)!;
		proc.WaitForExit();
		return proc.ExitCode;
	}

	/// <summary>
	/// Run a process without a console of its own, its stdout and stderr
	/// copied line by line into <paramref name="sink"/> (the quiet run's buffer).
	/// </summary>
	public static int RunCaptured(string fileName, IEnumerable<string> arguments, TextWriter sink)
	{
		var psi = new ProcessStartInfo
		{
			FileName = fileName,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		foreach (var a in arguments) psi.ArgumentList.Add(a);
		using var proc = new Process { StartInfo = psi };
		var gate = new object();
		void Copy(string? line) { if (line != null) lock (gate) sink.WriteLine(line); }
		proc.OutputDataReceived += (_, e) => Copy(e.Data);
		proc.ErrorDataReceived += (_, e) => Copy(e.Data);
		proc.Start();
		proc.BeginOutputReadLine();
		proc.BeginErrorReadLine();
		proc.WaitForExit();
		return proc.ExitCode;
	}

	/// <summary>
	/// Display form of a command line for diagnostics (update.log records it):
	/// exact on Windows (<see cref="WindowsCommandLine"/>), simply quoted elsewhere.
	/// </summary>
	public static string Describe(string fileName, IEnumerable<string> arguments) =>
		OperatingSystem.IsWindows()
			? WindowsCommandLine.Join(new[] { fileName }.Concat(arguments))
			: string.Join(' ', new[] { fileName }.Concat(arguments).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}
