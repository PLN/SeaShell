using System;
using System.Diagnostics;
using System.IO;

namespace SeaShell.ServiceHost.Installers;

public sealed class RunitInstaller : IServiceInstaller
{
	public int Install(ServiceConfig config)
	{
		var svDir = $"/etc/sv/{config.Name}";
		Directory.CreateDirectory(svDir);

		var runScript = $"""
			#!/bin/sh
			exec chpst -u {config.User ?? Environment.UserName} {config.ExePath}
			""";

		var runPath = Path.Combine(svDir, "run");
		File.WriteAllText(runPath, runScript);
		RunCommand("chmod", $"+x {runPath}");

		// Symlink to activate
		var servicePath = Directory.Exists("/var/service")
			? $"/var/service/{config.Name}"
			: $"/etc/service/{config.Name}";

		if (!Path.Exists(servicePath))
			RunCommand("ln", $"-s {svDir} {servicePath}");

		Console.WriteLine($"runit service installed: {svDir}");
		Console.WriteLine($"Activated via symlink: {servicePath}");
		return 0;
	}

	public int Uninstall(ServiceConfig config)
	{
		// Remove symlink first (stops supervision)
		foreach (var dir in new[] { "/var/service", "/etc/service" })
		{
			var link = $"{dir}/{config.Name}";
			if (Path.Exists(link))
				RunCommand("rm", link);
		}

		var svDir = $"/etc/sv/{config.Name}";
		if (Directory.Exists(svDir))
			Directory.Delete(svDir, recursive: true);

		Console.WriteLine($"Service '{config.Name}' removed.");
		return 0;
	}

	public int Start(ServiceConfig config) => RunCommand("sv", $"start {config.Name}");
	public int Stop(ServiceConfig config) => RunCommand("sv", $"stop {config.Name}");
	public int Status(ServiceConfig config) => RunCommand("sv", $"status {config.Name}");

	private static int RunCommand(string command, string arguments)
	{
		var psi = new ProcessStartInfo(command, arguments) { UseShellExecute = false };
		using var proc = Process.Start(psi)!;
		proc.WaitForExit();
		return proc.ExitCode;
	}
}
