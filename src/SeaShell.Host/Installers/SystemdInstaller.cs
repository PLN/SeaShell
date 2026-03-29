using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace SeaShell.ServiceHost.Installers;

public sealed class SystemdInstaller : IServiceInstaller
{
	public int Install(ServiceConfig config)
	{
		var unitPath = $"/etc/systemd/system/{config.Name}.service";
		var template = LoadTemplate("systemd.service.template");

		var content = template
			.Replace("{description}", config.Description)
			.Replace("{exePath}", config.ExePath)
			.Replace("{workDir}", config.WorkingDirectory)
			.Replace("{user}", config.User ?? Environment.UserName);

		File.WriteAllText(unitPath, content);

		RunCommand("systemctl", "daemon-reload");
		RunCommand("systemctl", "enable", config.Name);

		Console.WriteLine($"systemd unit installed: {unitPath}");
		Console.WriteLine($"Run: systemctl start {config.Name}");
		return 0;
	}

	public int Uninstall(ServiceConfig config)
	{
		RunCommand("systemctl", "stop", config.Name);
		RunCommand("systemctl", "disable", config.Name);

		var unitPath = $"/etc/systemd/system/{config.Name}.service";
		if (File.Exists(unitPath))
			File.Delete(unitPath);

		RunCommand("systemctl", "daemon-reload");
		Console.WriteLine($"Service '{config.Name}' removed.");
		return 0;
	}

	public int Start(ServiceConfig config) => RunCommand("systemctl", "start", config.Name);
	public int Stop(ServiceConfig config) => RunCommand("systemctl", "stop", config.Name);
	public int Status(ServiceConfig config) => RunCommand("systemctl", "status", config.Name);

	private static string LoadTemplate(string name)
	{
		var asm = Assembly.GetExecutingAssembly();
		var resourceName = $"SeaShell.Host.Templates.{name}";
		using var stream = asm.GetManifestResourceStream(resourceName);
		if (stream == null) throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}

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
