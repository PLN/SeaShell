namespace SeaShell.ServiceHost.Installers;

/// <summary>Platform-specific service registration.</summary>
public interface IServiceInstaller
{
	int Install(ServiceConfig config);
	int Uninstall(ServiceConfig config);
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
