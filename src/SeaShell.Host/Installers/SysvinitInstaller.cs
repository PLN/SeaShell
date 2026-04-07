using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace SeaShell.ServiceHost.Installers;

public sealed class SysvinitInstaller : IServiceInstaller
{
	public int Install(ServiceConfig config)
	{
		var initPath = $"/etc/init.d/{config.Name}";
		var template = LoadTemplate("sysvinit.init.template");

		var content = template
			.Replace("{name}", config.Name)
			.Replace("{displayName}", config.DisplayName)
			.Replace("{description}", config.Description)
			.Replace("{exePath}", config.ExePath)
			.Replace("{user}", config.User ?? Environment.UserName);

		File.WriteAllText(initPath, content);
		RunCommand("chmod", "+x", initPath);
		RunCommand("update-rc.d", config.Name, "defaults");

		Console.WriteLine($"sysvinit service installed: {initPath}");
		Console.WriteLine($"Run: service {config.Name} start");
		return 0;
	}

	public int Uninstall(ServiceConfig config)
	{
		RunCommand("service", config.Name, "stop");
		RunCommand("update-rc.d", "-f", config.Name, "remove");

		var initPath = $"/etc/init.d/{config.Name}";
		if (File.Exists(initPath))
			File.Delete(initPath);

		Console.WriteLine($"Service '{config.Name}' removed.");
		return 0;
	}

	public int Start(ServiceConfig config) => RunCommand("service", config.Name, "start");
	public int Stop(ServiceConfig config) => RunCommand("service", config.Name, "stop");
	public int Status(ServiceConfig config) => RunCommand("service", config.Name, "status");

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
