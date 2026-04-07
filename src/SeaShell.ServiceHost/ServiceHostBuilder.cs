using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SeaShell.ServiceHost.Installers;

namespace SeaShell.ServiceHost;

/// <summary>
/// Fluent builder for creating a SeaShell-powered system service.
///
/// Usage:
///   return await new ServiceHostBuilder()
///       .ServiceName("myservice")
///       .RunScript("main.cs")
///       .EnableNuGetUpdates(TimeSpan.FromHours(8))
///       .RunAsync(args);
/// </summary>
public sealed class ServiceHostBuilder
{
	private string _serviceName = "seashell-service";
	private string _displayName = "SeaShell Service";
	private string _description = "SeaShell script service";
	private string? _scriptPath;
	private string? _assemblyPath;
	private string[]? _scriptArgs;
	private TimeSpan? _updateInterval;
	private string? _workingDirectory;
	private Dictionary<string, string>? _environmentVars;

	/// <summary>Set the service name (used for registration with the init system).</summary>
	public ServiceHostBuilder ServiceName(string name) { _serviceName = name; return this; }

	/// <summary>Set the display name (human-readable, shown in service managers).</summary>
	public ServiceHostBuilder DisplayName(string name) { _displayName = name; return this; }

	/// <summary>Set the service description.</summary>
	public ServiceHostBuilder Description(string desc) { _description = desc; return this; }

	/// <summary>Run a SeaShell script as the service workload.</summary>
	public ServiceHostBuilder RunScript(string scriptPath, params string[] args)
	{
		_scriptPath = scriptPath;
		_assemblyPath = null;
		_scriptArgs = args;
		return this;
	}

	/// <summary>Run a pre-compiled .dll or .exe as the service workload.</summary>
	public ServiceHostBuilder RunAssembly(string assemblyPath, params string[] args)
	{
		_assemblyPath = assemblyPath;
		_scriptPath = null;
		_scriptArgs = args;
		return this;
	}

	/// <summary>Enable background NuGet update checks on the given interval.</summary>
	public ServiceHostBuilder EnableNuGetUpdates(TimeSpan interval)
	{
		_updateInterval = interval;
		return this;
	}

	/// <summary>Set the working directory for the script process. Defaults to the exe's directory.</summary>
	public ServiceHostBuilder WorkingDirectory(string dir)
	{
		_workingDirectory = dir;
		return this;
	}

	/// <summary>Set an environment variable for the script process.</summary>
	public ServiceHostBuilder EnvironmentVariable(string key, string value)
	{
		_environmentVars ??= new Dictionary<string, string>();
		_environmentVars[key] = value;
		return this;
	}

	/// <summary>Set multiple environment variables for the script process.</summary>
	public ServiceHostBuilder EnvironmentVariables(IDictionary<string, string> vars)
	{
		_environmentVars ??= new Dictionary<string, string>();
		foreach (var kv in vars)
			_environmentVars[kv.Key] = kv.Value;
		return this;
	}

	/// <summary>
	/// Build the host and run. Handles both service mode (foreground) and
	/// management commands (install, uninstall, start, stop, status).
	/// </summary>
	public async Task<int> RunAsync(string[] args)
	{
		var targetPath = _scriptPath ?? _assemblyPath
			?? throw new InvalidOperationException("No script or assembly specified. Call RunScript() or RunAssembly().");

		// Resolve relative script/assembly paths against the exe's directory, not CWD.
		// When SCM/systemd starts a service, CWD is system32 or / — relative paths
		// must resolve against where the binary actually lives.
		var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
		if (!Path.IsPathRooted(targetPath))
			targetPath = Path.GetFullPath(Path.Combine(exeDir, targetPath));

		var workingDirectory = _workingDirectory ?? exeDir;

		// Management commands
		if (args.Length > 0)
		{
			var command = args[0].ToLowerInvariant();
			if (command is "install" or "uninstall" or "start" or "stop" or "status")
				return HandleManagementCommand(command);
		}

		// Service mode — build and run the .NET Generic Host
		var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

		// Platform-specific service integration
		var initSystem = InitSystemDetector.Detect();
		if (initSystem == InitSystem.WindowsService)
			builder.Services.AddWindowsService(o => o.ServiceName = _serviceName);
		else if (initSystem == InitSystem.Systemd)
			builder.Services.AddSystemd();

		// Register our worker
		builder.Services.AddSingleton(new ServiceHostOptions(
			targetPath, _scriptArgs ?? Array.Empty<string>(), _updateInterval,
			workingDirectory, _environmentVars));
		builder.Services.AddHostedService<ServiceHostWorker>();

		var host = builder.Build();
		await host.RunAsync();
		return 0;
	}

	private int HandleManagementCommand(string command)
	{
		var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path");
		var config = new ServiceConfig(
			_serviceName,
			_displayName,
			_description,
			exePath,
			Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
			null, null);

		var installer = CreateInstaller();
		return command switch
		{
			"install" => installer.Install(config),
			"uninstall" => installer.Uninstall(config),
			"start" => installer.Start(config),
			"stop" => installer.Stop(config),
			"status" => installer.Status(config),
			_ => throw new ArgumentException($"Unknown command: {command}"),
		};
	}

	private static IServiceInstaller CreateInstaller()
	{
		return InitSystemDetector.Detect() switch
		{
			InitSystem.WindowsService => new WindowsServiceInstaller(),
			InitSystem.Systemd => new SystemdInstaller(),
			InitSystem.Runit => new RunitInstaller(),
			InitSystem.OpenRc => new OpenRcInstaller(),
			InitSystem.Sysvinit => new SysvinitInstaller(),
			_ => throw new PlatformNotSupportedException(
				"Could not detect init system. Supported: Windows Service, systemd, runit, OpenRC, sysvinit."),
		};
	}
}

/// <summary>Internal options record passed to the worker via DI.</summary>
internal sealed record ServiceHostOptions(
	string TargetPath,
	string[] Args,
	TimeSpan? UpdateInterval,
	string WorkingDirectory,
	Dictionary<string, string>? EnvironmentVars);
