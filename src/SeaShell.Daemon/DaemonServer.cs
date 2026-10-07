using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SeaShell.Ipc;
using SeaShell.Protocol;
using SeaShell.Engine;

namespace SeaShell.Daemon;

/// <summary>
/// Accepts IPC connections and dispatches requests to handlers.
/// Holds a persistent connection to the Elevator worker (if connected).
/// </summary>
public sealed class DaemonServer : IAsyncDisposable
{
	private static readonly ILogger _log = Log.ForContext<DaemonServer>();

	private readonly TransportServer _server;
	private readonly ScriptCompiler _compiler = new();
	private readonly NuGetUpdater _updater;
	private readonly DateTime _startTime = DateTime.UtcNow;

	// The Elevator's persistent connection — null if no elevator is connected.
	private TransportStream? _elevator;
	private bool _elevatorIsElevated;
	private string? _elevatorVersion;
	private readonly SemaphoreSlim _elevatorLock = new(1, 1);
	// One SpawnRequest/SpawnResponse exchange at a time: the elevator channel has
	// a single reader and replies carry no request id, so concurrent exchanges
	// would read each other's responses.
	private readonly SemaphoreSlim _elevatorExchangeLock = new(1, 1);
	// Worst case per SpawnRequest: AwaitElevatorMs + queue wait + reply, kept below the
	// CLI's reply timeout (DaemonManager.RequestElevatedSpawnAsync). The CLI's
	// ForwardDeadlineTickCount additionally covers time before this handler ran.
	private static readonly TimeSpan ElevatorQueueTimeout = TimeSpan.FromSeconds(20);
	private static readonly TimeSpan ElevatorReplyTimeout = TimeSpan.FromSeconds(15);
	private TaskCompletionSource<bool>? _elevatorArrived;
	private DateTime? _elevatorConnectedTime;
	private long _elevatorLastActivityTicks;

	private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(8);
	// Matches the invoker's keep-alive (ScriptInvoker / CacheKeepAlive.Interval).
	private static readonly TimeSpan CacheKeepAliveInterval = TimeSpan.FromMinutes(10);
	private static readonly string Version = typeof(DaemonServer).Assembly.GetName().Version?.ToString(4) ?? "0.1.0";

	// Idle timeout — 0 means stay active (default).
	private long _lastActivityTicks;
	private int _activeConnections;

	/// <summary>
	/// How long the daemon stays alive with no active connections or requests.
	/// Zero (default) means stay active indefinitely.
	/// </summary>
	public TimeSpan IdleTimeout { get; set; }

	public DaemonServer(string address)
	{
		_server = new TransportServer(address);
		_updater = new NuGetUpdater(_compiler.NuGetResolver);
		_updater.Log += msg => _log.Information("{Message}", msg);
		_lastActivityTicks = DateTime.UtcNow.Ticks;
	}

	public async Task RunAsync(CancellationToken ct)
	{
		_server.Start();
		var addr = TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity, TransportEndpoint.CurrentVersion);
		_log.Information("Listening on {Address}", addr);

		// Write marker files so installers/tools can discover this daemon
		var versionDir = SeaShellPaths.VersionDir(Version);
		MarkerFile.Write(versionDir, MarkerFile.DaemonPid, Environment.ProcessId.ToString());
		MarkerFile.Write(versionDir, MarkerFile.DaemonPipe, addr);
		_log.Information("Marker files written to {VersionDir}", versionDir);

		if (IdleTimeout > TimeSpan.Zero)
			_log.Information("Idle timeout: {IdleTimeout}", IdleTimeout);

		// Background NuGet update check — every 8 hours
		_ = RunUpdateLoopAsync(ct);

		// Cache tracker persistence + expiry sweep
		_ = RunCacheMaintenanceLoopAsync(ct);

		Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

		while (!ct.IsCancellationRequested)
		{
			try
			{
				// Both always-on and idle-timeout modes share a single accept call.
				// In idle mode, maxWait wakes us to recheck idle state without
				// disturbing the listener pool (returns null on expiry); a real
				// connection returns non-null and is dispatched.
				TimeSpan? maxWait = null;
				if (IdleTimeout > TimeSpan.Zero)
				{
					var lastActivity = new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
					var idle = DateTime.UtcNow - lastActivity;

					if (idle >= IdleTimeout && Volatile.Read(ref _activeConnections) == 0)
					{
						_log.Information("Idle timeout reached ({IdleTimeout}), shutting down", IdleTimeout);
						return;
					}

					var wait = IdleTimeout - idle;
					maxWait = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
				}

				var conn = await _server.AcceptAsync(ct, maxWait);
				if (conn == null) continue; // idle tick — recheck timeout

				TouchActivity();
				// Off the accept loop: if the request is already buffered, the handler
				// runs synchronously up to and through a compile, and the next client
				// would not be accepted until it finished.
				_ = Task.Run(() => HandleConnectionAsync(conn, ct));
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_log.Error(ex, "Accept error");
			}
		}
	}

	private void TouchActivity() =>
		Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

	private async Task HandleConnectionAsync(TransportStream conn, CancellationToken ct)
	{
		Interlocked.Increment(ref _activeConnections);
		try
		{
			var result = await conn.Channel.ReceiveAsync(ct);
			if (result == null) { await conn.DisposeAsync(); return; }

			var (type, message) = result.Value;

			// Elevator registration — hold this connection, don't dispose it.
			// HandleElevatorHello stores the connection and returns quickly,
			// so the active connection count drops back naturally via finally.
			if (type == MessageType.ElevatorHello)
			{
				await HandleElevatorHello(conn, (ElevatorHello)message, ct);
				return; // connection stays open
			}

			// REPL session — hold this connection for the session lifetime
			if (type == MessageType.ReplStartReq)
			{
				await HandleReplSession(conn, (ReplStartRequest)message, ct);
				return; // connection stays open until REPL ends
			}

			// Run request — may become a persistent watch connection
			if (type == MessageType.RunRequest)
			{
				var runReq = (RunRequest)message;
				RunResponse response;
				try
				{
					response = HandleRun(runReq);
				}
				catch (Exception ex)
				{
					// A compile-path exception must still produce a response —
					// otherwise the client waits forever on a reply that never comes.
					_log.Error(ex, "Run request failed: {ScriptPath}", runReq.ScriptPath);
					response = new RunResponse(false, false, false, null, null, null, null, 0,
						$"Daemon error: {ex.Message}");
				}
				await conn.Channel.SendAsync(response, ct);

				// If watch mode, keep connection open and push HotSwapNotify on changes
				if (response.Success && response.Watch)
				{
					await HandleWatchMode(conn, runReq, ct);
					return; // connection stays open
				}

				await conn.DisposeAsync();
				return;
			}

			// Simple request-response — dispose connection when done
			await using (conn)
			{
				switch (type)
				{
					case MessageType.SpawnRequest:
						var spawnResp = await SpawnElevatedAsync((SpawnRequest)message, ct);
						await conn.Channel.SendAsync(spawnResp, ct);
						break;

					case MessageType.PingRequest:
						await conn.Channel.SendAsync(MakePingResponse(), ct);
						break;

					case MessageType.CacheTouchRequest:
						var touch = (CacheTouchRequest)message;
						await conn.Channel.SendAsync(new CacheTouchResponse(_compiler.TouchCache(touch.OutputDir)), ct);
						break;

					case MessageType.StopRequest:
						_log.Information("Stop requested");
						Environment.SetEnvironmentVariable("SEASHELL_STOP", "1");
						await conn.Channel.SendAsync(MakePingResponse(), ct);
						break;

					default:
						_log.Warning("Unknown message type: {Type}", type);
						await conn.Channel.SendAsync(
							new RunResponse(false, false, false, null, null, null, null, 0,
								$"Unknown message type: {type}"), ct);
						break;
				}
			}
		}
		catch (Exception ex)
		{
			_log.Error(ex, "Connection error");
			// The connection is in an unknown state — close it so the client
			// sees a disconnect instead of waiting on a response forever.
			try { await conn.DisposeAsync(); } catch { }
		}
		finally
		{
			Interlocked.Decrement(ref _activeConnections);
			TouchActivity();
		}
	}

	// ── Elevator registration ───────────────────────────────────────────

	private async Task HandleElevatorHello(TransportStream conn, ElevatorHello hello, CancellationToken ct)
	{
		// Acknowledge before publishing: once this is _elevator a spawn may use it,
		// and the elevator expects ElevatorAck as the first message it receives.
		await conn.Channel.SendAsync(new ElevatorAck(true, null), ct);

		TransportStream? replaced;
		await _elevatorLock.WaitAsync(ct);
		try
		{
			replaced = _elevator;
			if (replaced != null)
				_log.Information("Replacing existing elevator connection");

			_elevator = conn;
			_elevatorIsElevated = hello.IsElevated;
			_elevatorVersion = hello.Version;
			_elevatorConnectedTime = DateTime.UtcNow;
			_elevatorLastActivityTicks = DateTime.UtcNow.Ticks;
		}
		finally
		{
			_elevatorLock.Release();
		}

		_log.Information("Elevator connected (elevated={IsElevated})", hello.IsElevated);

		// Signal anyone waiting for elevator (e.g. SpawnRequest with AwaitElevatorMs)
		_elevatorArrived?.TrySetResult(true);

		// Tear down the old connection outside the lock. An exchange still running on
		// it fails and reports MayHaveStarted; the new elevator is already usable.
		if (replaced != null)
			await replaced.DisposeAsync();

		// The connection stays open. The Elevator sits in a receive loop
		// on its end, waiting for SpawnRequests from us.
	}

	// ── Background NuGet updates ────────────────────────────────────────

	private async Task RunUpdateLoopAsync(CancellationToken ct)
	{
		// Delay first check — let the daemon settle, don't hit the network at startup
		try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
		catch (OperationCanceledException) { return; }

		while (!ct.IsCancellationRequested)
		{
			try
			{
				_log.Information("Checking NuGet packages for updates...");
				var result = await _updater.CheckForUpdatesAsync(ct);
				_log.Information("NuGet update check: {Checked} checked, {Updated} updated, {Failed} failed", result.Checked, result.Updated, result.Failed);
				foreach (var pkg in result.UpdatedPackages)
					_log.Information("Updated: {Package}", pkg);
			}
			catch (OperationCanceledException) { return; }
			catch (Exception ex)
			{
				_log.Error(ex, "NuGet update error");
			}

			try { await Task.Delay(UpdateInterval, ct); }
			catch (OperationCanceledException) { return; }
		}
	}

	/// <summary>
	/// Persist the cache tracker when it changed (write-behind; the in-memory copy is
	/// authoritative) and sweep expired cache entries hourly. The first sweep waits
	/// until every running script has had a chance to send a keep-alive
	/// (<see cref="CacheKeepAliveInterval"/>): after a daemon restart the tracker only
	/// knows the hit times it last saved.
	/// </summary>
	private async Task RunCacheMaintenanceLoopAsync(CancellationToken ct)
	{
		var firstSweep = CacheKeepAliveInterval + TimeSpan.FromMinutes(5);
		var lastSweep = DateTime.MinValue;
		while (!ct.IsCancellationRequested)
		{
			try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
			catch (OperationCanceledException) { break; }

			try
			{
				var now = DateTime.UtcNow;
				if (now - _startTime >= firstSweep && now - lastSweep >= TimeSpan.FromHours(1))
				{
					lastSweep = now;
					var removed = await Task.Run(_compiler.SweepCache, ct);
					if (removed > 0)
						_log.Information("Cache sweep removed {Count} expired entries", removed);
				}
				await _compiler.Tracker.SaveAsync();
			}
			catch (OperationCanceledException) { break; }
			catch (Exception ex)
			{
				_log.Warning(ex, "Cache maintenance failed");
			}
		}
	}

	// ── Watch mode ──────────────────────────────────────────────────────

	private async Task HandleWatchMode(TransportStream conn, RunRequest request, CancellationToken ct)
	{
		_log.Information("Watch mode: {ScriptPath}", request.ScriptPath);

		// Get source file list from the latest compile
		var result = _compiler.Compile(request.ScriptPath, request.Args);
		var sourceFiles = result.Success
			? _compiler.GetLastResolvedSources(request.ScriptPath)
			: new[] { request.ScriptPath };

		using var watcher = new ScriptWatcher(sourceFiles);
		var changeSignal = new SemaphoreSlim(0);
		var pendingClearCache = false;
		var lastReason = "file_changed";

		// Cancelled when the CLI goes away. Without it the loop below only notices a
		// disconnect on the next file change, holding the connection (and the active
		// connection count that gates idle shutdown) until then.
		using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var sessionCt = sessionCts.Token;

		watcher.Changed += file =>
		{
			_log.Information("File changed: {FileName}", Path.GetFileName(file));
			lastReason = "file_changed";
			changeSignal.Release();
		};

		// Background: read CLI messages (RecompileRequest from script-initiated reload)
		var reader = Task.Run(async () =>
		{
			try
			{
				while (!sessionCt.IsCancellationRequested)
				{
					var msg = await conn.Channel.ReceiveAsync(sessionCt);
					if (msg == null) break;
					if (msg.Value.Type == MessageType.RecompileRequest)
					{
						var req = (RecompileRequest)msg.Value.Message;
						_log.Information("Script requested recompile (clearCache={ClearCache})", req.ClearCache);
						if (req.ClearCache) pendingClearCache = true;
						lastReason = "script_requested";
						changeSignal.Release();
					}
				}
				_log.Information("CLI disconnected, ending watch");
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				_log.Warning(ex, "Watch connection failed, ending watch");
			}
			finally
			{
				sessionCts.Cancel();
			}
		}, CancellationToken.None);

		await using (conn)
		{
			try
			{
				while (!sessionCt.IsCancellationRequested)
				{
					// Wait for file change or recompile request
					try
					{
						await changeSignal.WaitAsync(sessionCt);
					}
					catch (OperationCanceledException) { break; }

					// Drain any extra signals from rapid saves
					while (changeSignal.CurrentCount > 0)
						await changeSignal.WaitAsync(TimeSpan.Zero);

					var reason = lastReason;

					// Clear cache if script requested it
					if (pendingClearCache)
					{
						pendingClearCache = false;
						_compiler.NuGetResolver.InvalidateCache();
						_compiler.ClearCache(request.ScriptPath);
					}

					// Recompile
					_log.Information("Recompiling {ScriptName} ({Reason})", Path.GetFileName(request.ScriptPath), reason);
					var recompile = _compiler.Compile(request.ScriptPath, request.Args);

					if (!recompile.Success)
					{
						_log.Warning("Recompile failed: {Error}", recompile.Error);
						// For script-requested reloads, notify CLI so it doesn't hang
						if (reason == "script_requested")
						{
							try { await conn.Channel.SendAsync(
								new HotSwapNotify("", null, null, null, "compile_failed"), sessionCt); }
							catch { break; }
						}
						continue; // keep watching, don't swap
					}

					// Push HotSwapNotify to CLI
					var notify = new HotSwapNotify(
						recompile.AssemblyPath!,
						recompile.DepsJsonPath,
						recompile.RuntimeConfigPath,
						recompile.ManifestPath,
						reason,
						recompile.StartupHookPath,
						recompile.DirectExe,
						recompile.Restart);

					try
					{
						await conn.Channel.SendAsync(notify, sessionCt);
						_log.Debug("Hot-swap notify sent");
					}
					catch
					{
						_log.Information("CLI disconnected, ending watch");
						break;
					}
				}
			}
			finally
			{
				// Stop the reader before the connection is disposed under it.
				sessionCts.Cancel();
				await reader;
			}
		}

		_log.Debug("Watch mode ended");
	}

	// ── REPL ────────────────────────────────────────────────────────────

	private async Task HandleReplSession(TransportStream conn, ReplStartRequest request, CancellationToken ct)
	{
		_log.Information("REPL session started ({PackageCount} packages)", request.NuGetPackages.Length);

		var session = new ReplSession(_compiler.NuGetResolver, request.NuGetPackages);

		// Send ready response
		await conn.Channel.SendAsync(new ReplStartResponse(true, null), ct);

		// Eval loop — read requests, evaluate, send responses
		await using (conn)
		{
			while (!ct.IsCancellationRequested)
			{
				var msg = await conn.Channel.ReceiveAsync(ct);
				if (msg == null) break; // client disconnected

				if (msg.Value.Type == MessageType.ReplEvalReq)
				{
					var evalReq = (ReplEvalRequest)msg.Value.Message;
					var evalResult = await session.EvalAsync(evalReq.Code);
					await conn.Channel.SendAsync(evalResult, ct);
				}
				else
				{
					break; // unknown message, end session
				}
			}
		}

		_log.Information("REPL session ended");
	}

	// ── Request handlers ────────────────────────────────────────────────

	private PingResponse MakePingResponse()
	{
		var now = DateTime.UtcNow;
		var uptime = (int)(now - _startTime).TotalSeconds;
		var idle = (int)((now.Ticks - Interlocked.Read(ref _lastActivityTicks)) / TimeSpan.TicksPerSecond);
		var timeout = (int)IdleTimeout.TotalSeconds;
		var elevUptime = _elevatorConnectedTime.HasValue
			? (int)(now - _elevatorConnectedTime.Value).TotalSeconds : 0;
		var elevIdle = _elevatorConnectedTime.HasValue
			? (int)((now.Ticks - Interlocked.Read(ref _elevatorLastActivityTicks)) / TimeSpan.TicksPerSecond) : 0;
		// Exclude the connection carrying this ping — otherwise --status always
		// reports at least "1 active" and phantom operations get diagnosed.
		return new PingResponse(Version, false, _elevator != null, uptime,
			Math.Max(0, _activeConnections - 1),
			Environment.ProcessId, idle, timeout, _elevatorVersion,
			elevUptime, elevIdle);
	}

	private RunResponse HandleRun(RunRequest request)
	{
		_log.Information("Run request: {ScriptPath}", request.ScriptPath);

		if (request.ClearCache)
		{
			_log.Information("Clearing cache for {ScriptName}", Path.GetFileNameWithoutExtension(request.ScriptPath));
			_compiler.NuGetResolver.InvalidateCache();
			_compiler.ClearCache(request.ScriptPath);
		}

		var result = _compiler.Compile(request.ScriptPath, request.Args);
		if (!result.Success)
			return new RunResponse(false, false, false, null, null, null, null, 0, result.Error);

		_log.Debug("Compile: Restart={Restart} Watch={Watch} Elevate={Elevate} Mutex={Mutex}",
			result.Restart, result.Watch, result.Elevate, result.MutexScope);

		return new RunResponse(
			true, result.Elevate, result.Watch,
			result.AssemblyPath,
			result.DepsJsonPath,
			result.RuntimeConfigPath,
			result.ManifestPath,
			0, null,
			result.StartupHookPath,
			result.DirectExe,
			result.Restart,
			result.MutexScope,
			result.MutexAttach);
	}

	/// <summary>
	/// Forward a spawn request to the connected Elevator and wait for its response.
	/// Called by the compilation pipeline when a script has //sea_elevate.
	/// </summary>
	public async Task<SpawnResponse> SpawnElevatedAsync(SpawnRequest request, CancellationToken ct)
	{
		await _elevatorLock.WaitAsync(ct);
		TransportStream? elevator;
		try
		{
			elevator = _elevator;
		}
		finally
		{
			_elevatorLock.Release();
		}

		// Wait for elevator to connect if requested (CLI started the elevator task)
		if (elevator == null && request.AwaitElevatorMs > 0)
		{
			_log.Information("Waiting up to {Ms}ms for elevator to connect...", request.AwaitElevatorMs);
			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			_elevatorArrived = tcs;
			try
			{
				await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(request.AwaitElevatorMs), ct);
			}
			catch (TimeoutException)
			{
				_log.Warning("Elevator did not connect in time");
				return new SpawnResponse(false, 0, "Elevator did not connect in time");
			}
			finally
			{
				_elevatorArrived = null;
			}

			// Re-acquire elevator reference
			await _elevatorLock.WaitAsync(ct);
			try { elevator = _elevator; }
			finally { _elevatorLock.Release(); }
		}

		if (elevator == null)
			return new SpawnResponse(false, 0, "No elevator connected");

		// Everything up to the send is "not forwarded": failures are safe to retry.
		// Queue at most ElevatorQueueTimeout, and never past the CLI's forward deadline.
		var queueWait = ElevatorQueueTimeout;
		if (request.ForwardDeadlineTickCount > 0)
		{
			var untilDeadline = TimeSpan.FromMilliseconds(request.ForwardDeadlineTickCount - Environment.TickCount64);
			if (untilDeadline < queueWait)
				queueWait = untilDeadline > TimeSpan.Zero ? untilDeadline : TimeSpan.Zero;
		}
		if (!await _elevatorExchangeLock.WaitAsync(queueWait, ct))
			return new SpawnResponse(false, 0, "Elevator busy: earlier elevated spawns did not finish in time");
		var sent = false;
		try
		{
			if (request.ForwardDeadlineTickCount > 0 && Environment.TickCount64 > request.ForwardDeadlineTickCount)
				return new SpawnResponse(false, 0, "Spawn request expired before it could be forwarded");

			// A failed exchange ahead of us may have detached the elevator, or it
			// may have re-registered on a new connection — use the current one.
			await _elevatorLock.WaitAsync(ct);
			try { elevator = _elevator; }
			finally { _elevatorLock.Release(); }
			if (elevator == null)
				return new SpawnResponse(false, 0, "No elevator connected");

			// The elevator only starts a process, so a reply is near-immediate. A
			// timeout leaves the channel mid-frame — the connection is unusable after.
			// From here on the elevator may already have started the process.
			using var replyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			replyCts.CancelAfter(ElevatorReplyTimeout);

			sent = true;
			await elevator.Channel.SendAsync(request, replyCts.Token);
			var reply = await elevator.Channel.ReceiveAsync(replyCts.Token);
			if (reply == null)
			{
				// Elevator disconnected
				await DetachElevator(elevator);
				return new SpawnResponse(false, 0, "Elevator disconnected during spawn", MayHaveStarted: true);
			}
			Interlocked.Exchange(ref _elevatorLastActivityTicks, DateTime.UtcNow.Ticks);
			return (SpawnResponse)reply.Value.Message;
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "Elevator spawn exchange failed");
			// Only a failed exchange says anything about the connection's health.
			if (sent && elevator != null)
				await DetachElevator(elevator);
			return new SpawnResponse(false, 0, $"Elevator communication error: {ex.Message}", MayHaveStarted: sent);
		}
		finally
		{
			_elevatorExchangeLock.Release();
		}
	}

	/// <summary>
	/// Drop <paramref name="failed"/> if it is still the registered elevator. If the
	/// elevator re-registered meanwhile, HandleElevatorHello already disposed the old
	/// connection and the new one stays.
	/// </summary>
	private async Task DetachElevator(TransportStream failed)
	{
		var detached = false;
		await _elevatorLock.WaitAsync();
		try
		{
			if (_elevator == failed)
			{
				_log.Information("Elevator disconnected");
				_elevator = null;
				_elevatorConnectedTime = null;
				detached = true;
			}
		}
		finally
		{
			_elevatorLock.Release();
		}

		// Tear down outside the lock so a replacement elevator can register meanwhile.
		if (detached)
			await failed.DisposeAsync();
	}

	public async ValueTask DisposeAsync()
	{
		if (_elevator != null)
			await _elevator.DisposeAsync();
		await _server.DisposeAsync();
		await _compiler.Tracker.SaveAsync();

		// Clean up marker files
		var versionDir = SeaShellPaths.VersionDir(Version);
		MarkerFile.Delete(versionDir, MarkerFile.DaemonPid);
		MarkerFile.Delete(versionDir, MarkerFile.DaemonPipe);
	}
}
