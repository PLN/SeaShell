using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SeaShell.Host;
using SeaShell.Invoker;

namespace SeaShell.ServiceHost;

/// <summary>
/// BackgroundService that compiles and runs a SeaShell script (or binary),
/// with crash recovery. NuGet updates are downloaded by the daemon's 8-hour cycle; with
/// <see cref="ServiceHostBuilder.ReloadOnPackageUpdate"/> the running script is reloaded
/// when one changes what it compiles to.
/// </summary>
internal sealed class ServiceHostWorker : BackgroundService
{
	private readonly ServiceHostOptions _options;
	private readonly ILogger<ServiceHostWorker> _logger;
	private readonly ScriptHost _host;
	private readonly ScriptConnection _connection = new();

	public ServiceHostWorker(ServiceHostOptions options, ILogger<ServiceHostWorker> logger)
	{
		_options = options;
		_logger = logger;
		_host = new ScriptHost(msg => logger.LogDebug("{Message}", msg));
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var reloadChecks = Task.CompletedTask;
		if (_options.ReloadInterval is { } interval)
		{
			_logger.LogInformation("Reload on package update: checking every {Interval}", interval);
			// Its own host, so that a failed check's compile error is logged as a warning
			var checkHost = new ScriptHost(msg => _logger.LogWarning("Package update check: {Message}", msg));
			var reload = new PackageUpdateReload(
				ct => checkHost.CompileAsync(_options.TargetPath, _options.Args, ct),
				() => _connection.Running,
				reason => _connection.RequestReload(reason),
				_logger);
			reloadChecks = reload.RunAsync(interval, stoppingToken);
		}

		// Main execution loop with crash recovery
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				_logger.LogInformation("Starting {Target}", _options.TargetPath);

				var result = await _host.RunAsync(
					_options.TargetPath,
					_options.Args,
					workingDirectory: _options.WorkingDirectory,
					environmentVars: _options.EnvironmentVars,
					connection: _connection,
					ct: stoppingToken);

				_logger.LogInformation("Script exited with code {ExitCode}", result.ExitCode);

				if (!string.IsNullOrEmpty(result.StandardError))
					_logger.LogWarning("Script stderr: {Stderr}", result.StandardError);

				if (stoppingToken.IsCancellationRequested)
					break;

				if (result.ExitCode != 0)
				{
					_logger.LogWarning("Script crashed, restarting in 5s...");
					await Task.Delay(5000, stoppingToken);
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Service error");
				try { await Task.Delay(5000, stoppingToken); }
				catch (OperationCanceledException) { break; }
			}
		}

		await reloadChecks;
	}
}
