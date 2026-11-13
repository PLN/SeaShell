using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

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
		RunCommand("chmod", "+x", runPath);

		// Symlink to activate
		var servicePath = Directory.Exists("/var/service")
			? $"/var/service/{config.Name}"
			: $"/etc/service/{config.Name}";

		if (!Path.Exists(servicePath))
			RunCommand("ln", "-s", svDir, servicePath);

		Console.WriteLine($"runit service installed: {svDir}");
		Console.WriteLine($"Activated via symlink: {servicePath}");

		// runsvdir picks the link up on its next scan (every five seconds) and starts the
		// service. sv needs the supervisor that scan starts: wait for it, then sv start.
		var supervised = Path.Combine(svDir, "supervise", "ok");
		for (var i = 0; i < 30 && !File.Exists(supervised); i++)
			Thread.Sleep(500);
		return ServiceStart.AfterInstall(this, config);
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

	// sv start on a service that is up is a no-op with exit code 0
	public int Start(ServiceConfig config) => RunCommand("sv", "start", config.Name);
	public int Stop(ServiceConfig config) => RunCommand("sv", "stop", config.Name);
	public int Status(ServiceConfig config) => RunCommand("sv", "status", config.Name);

	private static int RunCommand(string command, params string[] arguments)
	{
		var psi = new ProcessStartInfo(command) { UseShellExecute = false };
		foreach (var arg in arguments)
			psi.ArgumentList.Add(arg);
		using var proc = Process.Start(psi)!;
		proc.WaitForExit();
		return proc.ExitCode;
	}
}
