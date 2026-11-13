using System;

namespace SeaShell.ServiceHost.Installers;

/// <summary>Platform-specific service registration.</summary>
public interface IServiceInstaller
{
	/// <summary>Register the service and start it.</summary>
	int Install(ServiceConfig config);
	int Uninstall(ServiceConfig config);
	/// <summary>Start the service. A service that is already running is a success.</summary>
	int Start(ServiceConfig config);
	int Stop(ServiceConfig config);
	int Status(ServiceConfig config);
}

/// <summary>Service configuration passed to installers.</summary>
public sealed record ServiceConfig(
	string Name,
	string DisplayName,
	string Description,
	string ExePath,
	string WorkingDirectory,
	string? User,
	string? Group);

internal static class ServiceStart
{
	/// <summary>
	/// Start a service that was just registered. The registration stays when the start
	/// fails; the exit code is the start's, so a caller sees that the service is not running.
	/// </summary>
	public static int AfterInstall(IServiceInstaller installer, ServiceConfig config)
	{
		var result = installer.Start(config);
		if (result == 0)
			Console.WriteLine($"Service '{config.Name}' started.");
		else
			Console.Error.WriteLine($"Service '{config.Name}' is installed but did not start (exit code {result}).");
		return result;
	}
}
