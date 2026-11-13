using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SeaShell.ServiceHost.Installers;

public sealed class WindowsServiceInstaller : IServiceInstaller
{
	/// <summary>ERROR_SERVICE_ALREADY_RUNNING, the exit code of <c>sc start</c> on a running service.</summary>
	internal const int ErrorServiceAlreadyRunning = 1056;

	public int Install(ServiceConfig config)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Console.Error.WriteLine("Windows Service installer requires Windows");
			return 1;
		}

		var result = RunSc("create", config.Name, $"binPath={config.ExePath}", "start=auto", $"DisplayName={config.DisplayName}");
		if (result != 0) return result;

		RunSc("description", config.Name, config.Description);
		// Recovery: restart after 5s, 10s, 30s; reset failure count after 24h
		RunSc("failure", config.Name, "reset=86400", "actions=restart/5000/restart/10000/restart/30000");

		Console.WriteLine($"Service '{config.Name}' installed.");
		return ServiceStart.AfterInstall(this, config);
	}

	public int Uninstall(ServiceConfig config)
	{
		// A running service would only be marked for deletion and keep running
		RunSc("stop", config.Name);
		var result = RunSc("delete", config.Name);
		if (result == 0)
			Console.WriteLine($"Service '{config.Name}' removed.");
		return result;
	}

	public int Start(ServiceConfig config)
	{
		var (exitCode, output) = RunScCaptured("start", config.Name);
		if (exitCode == ErrorServiceAlreadyRunning)
			Console.WriteLine($"Service '{config.Name}' is already running.");
		else
			Console.Write(output);
		return StartExitCode(exitCode);
	}

	public int Stop(ServiceConfig config) => RunSc("stop", config.Name);
	public int Status(ServiceConfig config) => RunSc("query", config.Name);

	/// <summary><c>sc start</c> exit code as the result of <c>start</c>: already running is success.</summary>
	internal static int StartExitCode(int scExitCode) =>
		scExitCode == ErrorServiceAlreadyRunning ? 0 : scExitCode;

	private static int RunSc(params string[] arguments)
	{
		var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false };
		foreach (var arg in arguments)
			psi.ArgumentList.Add(arg);
		using var proc = Process.Start(psi)!;
		proc.WaitForExit();
		return proc.ExitCode;
	}

	private static (int ExitCode, string Output) RunScCaptured(params string[] arguments)
	{
		var psi = new ProcessStartInfo("sc.exe")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach (var arg in arguments)
			psi.ArgumentList.Add(arg);
		using var proc = Process.Start(psi)!;
		var stderr = proc.StandardError.ReadToEndAsync();
		var stdout = proc.StandardOutput.ReadToEnd();
		proc.WaitForExit();
		return (proc.ExitCode, stdout + stderr.Result);
	}
}
