using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SeaShell.Host;

namespace SeaShell.ServiceHost;

/// <summary>
/// BackgroundService that compiles and runs a SeaShell script (or binary),
/// with optional NuGet update loop for zero-locking automatic updates.
/// </summary>
internal sealed class ServiceHostWorker : BackgroundService
{
	private readonly ServiceHostOptions _options;
	private readonly ILogger<ServiceHostWorker> _logger;
	private readonly ScriptHost _host = new();
	private readonly ScriptHost.ScriptConnection _connection = new();

	public ServiceHostWorker(ServiceHostOptions options, ILogger<ServiceHostWorker> logger)
	{
		_options = options;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// Start NuGet update loop if configured
		if (_options.UpdateInterval is { } interval)
			_ = RunUpdateLoopAsync(interval, stoppingToken);

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
					// Crash — wait before retry
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
	}

	private async Task RunUpdateLoopAsync(TimeSpan interval, CancellationToken ct)
	{
		// Delay first check
		try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
		catch (OperationCanceledException) { return; }

		var updater = _host.CreateUpdater();

		while (!ct.IsCancellationRequested)
		{
			try
			{
				_logger.LogDebug("Checking for NuGet updates...");
				var result = await updater.CheckForUpdatesAsync(ct);

				if (result.Updated > 0)
				{
					_logger.LogInformation("NuGet updates found ({Updated} packages). Script will recompile on next start.",
						result.Updated);
					foreach (var pkg in result.UpdatedPackages)
						_logger.LogInformation("  Updated: {Package}", pkg);

					// Signal the running script to reload (it will recompile with new packages)
					try { await _connection.SendAsync("reload", "service:lifecycle", ct); }
					catch { }
				}
			}
			catch (OperationCanceledException) { return; }
			catch (Exception ex)
			{
				_logger.LogError(ex, "NuGet update check failed");
			}

			try { await Task.Delay(interval, ct); }
			catch (OperationCanceledException) { return; }
		}
	}
}
