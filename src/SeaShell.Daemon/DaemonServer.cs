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
	private readonly SemaphoreSlim _elevatorLock = new(1, 1);
	private TaskCompletionSource<bool>? _elevatorArrived;

	private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(8);
	private static readonly string Version = typeof(DaemonServer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

	public DaemonServer(string address)
	{
		_server = new TransportServer(address);
		_updater = new NuGetUpdater(_compiler.NuGetResolver);
		_updater.Log += msg => _log.Information("{Message}", msg);
	}

	public async Task RunAsync(CancellationToken ct)
	{
		_server.Start();
		var addr = TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity);
		_log.Information("Listening on {Address}", addr);

		// Background NuGet update check — every 8 hours
		_ = RunUpdateLoopAsync(ct);

		while (!ct.IsCancellationRequested)
		{
			try
			{
				var conn = await _server.AcceptAsync(ct);
				_ = HandleConnectionAsync(conn, ct);
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

	private async Task HandleConnectionAsync(TransportStream conn, CancellationToken ct)
	{
		try
		{
			var result = await conn.Channel.ReceiveAsync(ct);
			if (result == null) { await conn.DisposeAsync(); return; }

			var (type, message) = result.Value;

			// Elevator registration — hold this connection, don't dispose it
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
				var response = HandleRun(runReq);
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
		}
	}

	// ── Elevator registration ───────────────────────────────────────────

	private async Task HandleElevatorHello(TransportStream conn, ElevatorHello hello, CancellationToken ct)
	{
		await _elevatorLock.WaitAsync(ct);
		try
		{
			// Dispose old elevator connection if any
			if (_elevator != null)
			{
				_log.Information("Replacing existing elevator connection");
				await _elevator.DisposeAsync();
			}

			_elevator = conn;
			_elevatorIsElevated = hello.IsElevated;
		}
		finally
		{
			_elevatorLock.Release();
		}

		_log.Information("Elevator connected (elevated={IsElevated})", hello.IsElevated);

		// Signal anyone waiting for elevator (e.g. SpawnRequest with AwaitElevatorMs)
		_elevatorArrived?.TrySetResult(true);

		await conn.Channel.SendAsync(new ElevatorAck(true, null), ct);

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

		watcher.Changed += file =>
		{
			_log.Information("File changed: {FileName}", Path.GetFileName(file));
			lastReason = "file_changed";
			changeSignal.Release();
		};

		// Background: read CLI messages (RecompileRequest from script-initiated reload)
		_ = Task.Run(async () =>
		{
			try
			{
				while (!ct.IsCancellationRequested)
				{
					var msg = await conn.Channel.ReceiveAsync(ct);
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
			}
			catch (OperationCanceledException) { }
			catch { }
		}, ct);

		await using (conn)
		{
			while (!ct.IsCancellationRequested)
			{
				// Wait for file change or recompile request
				try
				{
					await changeSignal.WaitAsync(ct);
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
					CompilationCache.ClearScript(
						SeaShellPaths.CacheDir,
						Path.GetFileNameWithoutExtension(request.ScriptPath));
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
							new HotSwapNotify("", null, null, null, "compile_failed"), ct); }
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
					recompile.DirectExe);

				try
				{
					await conn.Channel.SendAsync(notify, ct);
					_log.Debug("Hot-swap notify sent");
				}
				catch
				{
					_log.Information("CLI disconnected, ending watch");
					break;
				}
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

	private static readonly string? _daemonHash = ComputeSelfHash();

	private PingResponse MakePingResponse()
	{
		var uptime = (int)(DateTime.UtcNow - _startTime).TotalSeconds;
		return new PingResponse(Version, false, _elevator != null, uptime, 0,
			Environment.ProcessId, _daemonHash);
	}

	/// <summary>Compute hash of our own directory — matches DaemonManager.StageBinary's hash.</summary>
	private static string? ComputeSelfHash()
	{
		try
		{
			var ticks = 0L;
			foreach (var f in System.Linq.Enumerable.OrderBy(
				Directory.GetFiles(AppContext.BaseDirectory, "*.dll"), f => f))
				ticks += File.GetLastWriteTimeUtc(f).Ticks;
			return ticks.ToString("x");
		}
		catch { return null; }
	}

	private RunResponse HandleRun(RunRequest request)
	{
		_log.Information("Run request: {ScriptPath}", request.ScriptPath);

		if (request.ClearCache)
		{
			_log.Information("Clearing cache for {ScriptName}", Path.GetFileNameWithoutExtension(request.ScriptPath));
			_compiler.NuGetResolver.InvalidateCache();
			CompilationCache.ClearScript(
				SeaShellPaths.CacheDir,
				Path.GetFileNameWithoutExtension(request.ScriptPath));
		}

		var result = _compiler.Compile(request.ScriptPath, request.Args);
		if (!result.Success)
			return new RunResponse(false, false, false, null, null, null, null, 0, result.Error);

		return new RunResponse(
			true, result.Elevate, result.Watch,
			result.AssemblyPath,
			result.DepsJsonPath,
			result.RuntimeConfigPath,
			result.ManifestPath,
			0, null,
			result.StartupHookPath,
			result.DirectExe);
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

		try
		{
			await elevator.Channel.SendAsync(request, ct);
			var reply = await elevator.Channel.ReceiveAsync(ct);
			if (reply == null)
			{
				// Elevator disconnected
				await DetachElevator();
				return new SpawnResponse(false, 0, "Elevator disconnected during spawn");
			}
			return (SpawnResponse)reply.Value.Message;
		}
		catch (Exception ex)
		{
			await DetachElevator();
			return new SpawnResponse(false, 0, $"Elevator communication error: {ex.Message}");
		}
	}

	private async Task DetachElevator()
	{
		await _elevatorLock.WaitAsync();
		try
		{
			if (_elevator != null)
			{
				_log.Information("Elevator disconnected");
				await _elevator.DisposeAsync();
				_elevator = null;
			}
		}
		finally
		{
			_elevatorLock.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_elevator != null)
			await _elevator.DisposeAsync();
		await _server.DisposeAsync();
	}
}
