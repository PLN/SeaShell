using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SeaShell.ServiceHost.Installers;

public sealed class WindowsServiceInstaller : IServiceInstaller
{
	public int Install(ServiceConfig config)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Console.Error.WriteLine("Windows Service installer requires Windows");
			return 1;
		}

		var result = RunSc($"create {config.Name} binPath=\"{config.ExePath}\" start=auto DisplayName=\"{config.DisplayName}\"");
		if (result != 0) return result;

		RunSc($"description {config.Name} \"{config.Description}\"");
		// Recovery: restart after 5s, 10s, 30s; reset failure count after 24h
		RunSc($"failure {config.Name} reset=86400 actions=restart/5000/restart/10000/restart/30000");

		Console.WriteLine($"Service '{config.Name}' installed.");
		return 0;
	}

	public int Uninstall(ServiceConfig config)
	{
		var result = RunSc($"delete {config.Name}");
		if (result == 0)
			Console.WriteLine($"Service '{config.Name}' removed.");
		return result;
	}

	public int Start(ServiceConfig config) => RunSc($"start {config.Name}");
	public int Stop(ServiceConfig config) => RunSc($"stop {config.Name}");
	public int Status(ServiceConfig config) => RunSc($"query {config.Name}");

	private static int RunSc(string arguments)
	{
		var psi = new ProcessStartInfo("sc.exe", arguments)
		{
			UseShellExecute = false,
		};
		using var proc = Process.Start(psi)!;
		proc.WaitForExit();
		return proc.ExitCode;
	}
}
