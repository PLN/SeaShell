using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Ipc;
using SeaShell.Protocol;

namespace SeaShell.Invoker;

/// <summary>
/// While a script runs from a compiled cache entry, tells the daemon every
/// <see cref="Interval"/> that the entry is still in use. The daemon's cache sweep
/// deletes entries that have not been hit for a day (or 30 days once promoted);
/// without this a long-running script — a service, a watch session — would have its
/// files deleted from under it. Concurrent runs of one script each ping; that is fine.
/// Failures are ignored: an older daemon, a busy one, or one that is restarting
/// simply misses a beat, and the daemon delays its first sweep after start by more
/// than one interval.
/// </summary>
internal sealed class CacheKeepAlive : IAsyncDisposable
{
	public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

	private readonly string? _daemonAddress;
	private readonly TimeSpan _interval;
	private readonly CancellationTokenSource _cts = new();
	private Task _loop = Task.CompletedTask;
	private volatile string? _outputDir;

	/// <summary>
	/// Result of the touch sent at start: true = a live cache entry, false = the daemon
	/// does not know the dir (expired, or not a cache dir), null = not sent or no answer.
	/// </summary>
	public bool? Known { get; private set; }

	private CacheKeepAlive(string? daemonAddress, CompiledScript compiled, TimeSpan interval)
	{
		_daemonAddress = daemonAddress;
		_interval = interval;
		Update(compiled);
	}

	/// <summary>
	/// Start keeping <paramref name="compiled"/>'s cache entry alive. With
	/// <paramref name="touchNow"/>, first records a hit and waits for the answer — for
	/// scripts compiled earlier, so the sweep cannot take the entry while the script
	/// starts. Right after a compile that is redundant: the compile was the hit.
	/// </summary>
	public static Task<CacheKeepAlive> StartAsync(string? daemonAddress, CompiledScript compiled, bool touchNow, CancellationToken ct = default) =>
		StartAsync(daemonAddress, compiled, touchNow, Interval, ct);

	internal static async Task<CacheKeepAlive> StartAsync(string? daemonAddress, CompiledScript compiled, bool touchNow, TimeSpan interval, CancellationToken ct = default)
	{
		var keepAlive = new CacheKeepAlive(daemonAddress, compiled, interval);
		if (daemonAddress != null)
		{
			if (touchNow)
				keepAlive.Known = await keepAlive.TouchAsync(ct).ConfigureAwait(false);
			keepAlive._loop = keepAlive.RunAsync(keepAlive._cts.Token);
		}
		return keepAlive;
	}

	/// <summary>
	/// The script now runs from another entry (reload, hot swap). The entry is the dir
	/// holding the manifest: for a binary run in place, the assembly is outside the
	/// cache but its staged manifest is not.
	/// </summary>
	public void Update(CompiledScript compiled)
	{
		var anchor = compiled.ManifestPath ?? compiled.AssemblyPath;
		_outputDir = Path.GetDirectoryName(Path.GetFullPath(anchor));
	}

	private async Task RunAsync(CancellationToken ct)
	{
		try
		{
			while (true)
			{
				await Task.Delay(_interval, ct).ConfigureAwait(false);
				await TouchAsync(ct).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async Task<bool?> TouchAsync(CancellationToken ct)
	{
		var dir = _outputDir;
		if (dir == null) return null;
		try
		{
			await using var conn = await TransportClient.ConnectAsync(_daemonAddress!, timeoutMs: 2000, ct).ConfigureAwait(false);
			var reply = await conn.RequestAsync(new CacheTouchRequest(dir), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
			return (reply?.Message as CacheTouchResponse)?.Known;
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return null; // missed beat — next interval
		}
	}

	public async ValueTask DisposeAsync()
	{
		_cts.Cancel();
		await _loop.ConfigureAwait(false);
		_cts.Dispose();
	}
}
