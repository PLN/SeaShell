using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SeaShell.Invoker;

namespace SeaShell.ServiceHost;

/// <summary>
/// <see cref="ServiceHostBuilder.ReloadOnPackageUpdate"/>: on a timer, asks the daemon to
/// compile the workload again — an ordinary compile request, a cache hit while nothing
/// changed — and compares the cache directory it answers with the one the running instance
/// was started from. The directory is named after a hash of the sources and the resolved
/// direct package versions, so it moves when the daemon's updater has downloaded a newer
/// version of a versionless <c>//sea_nuget</c> package (or a source file was edited). Then
/// the workload is reloaded the way <c>Sea.RequestReload()</c> does it.
/// </summary>
internal sealed class PackageUpdateReload
{
	public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);

	private readonly Func<CancellationToken, Task<CompiledScript?>> _compile;
	private readonly Func<CompiledScript?> _running;
	private readonly Func<string, bool> _requestReload;
	private readonly ILogger _logger;

	/// <param name="compile">Compile request for the workload; null when it failed.</param>
	/// <param name="running">What the running instance was started from; null before the first start.</param>
	/// <param name="requestReload">Reload the running instance; false if that is not possible now.</param>
	/// <param name="logger">Gets every check's outcome: changes at Information, failures at Warning.</param>
	public PackageUpdateReload(
		Func<CancellationToken, Task<CompiledScript?>> compile,
		Func<CompiledScript?> running,
		Func<string, bool> requestReload,
		ILogger logger)
	{
		_compile = compile;
		_running = running;
		_requestReload = requestReload;
		_logger = logger;
	}

	public async Task RunAsync(TimeSpan interval, CancellationToken ct)
	{
		while (true)
		{
			try { await Task.Delay(interval, ct); }
			catch (OperationCanceledException) { return; }

			try { await CheckAsync(ct); }
			catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Package update check failed; next check in {Interval}", interval);
			}
		}
	}

	/// <summary>One check. True if a reload was requested.</summary>
	internal async Task<bool> CheckAsync(CancellationToken ct)
	{
		var running = _running();
		if (running == null)
		{
			_logger.LogDebug("Package update check: no reloadable instance running");
			return false;
		}

		var latest = await _compile(ct);
		if (latest == null)
		{
			_logger.LogWarning("Package update check: compile failed; the running instance continues");
			return false;
		}

		var runningDir = OutputDir(running);
		var latestDir = OutputDir(latest);
		if (SameDir(runningDir, latestDir))
		{
			_logger.LogDebug("Package update check: unchanged ({Dir})", runningDir);
			return false;
		}

		_logger.LogInformation("Package update check: compile result changed ({Running} -> {Latest}); reloading",
			runningDir, latestDir);
		if (_requestReload("package update"))
			return true;

		_logger.LogWarning("Package update check: the running instance cannot be reloaded now; trying again at the next check");
		return false;
	}

	/// <summary>The cache entry a compile result lives in (as <c>CacheKeepAlive</c> anchors it).</summary>
	internal static string OutputDir(CompiledScript compiled) =>
		Path.GetDirectoryName(Path.GetFullPath(compiled.ManifestPath ?? compiled.AssemblyPath))!;

	private static bool SameDir(string a, string b) =>
		string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
