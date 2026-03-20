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
			var bytes = await conn.ReceiveAsync(ct);
			if (bytes == null) { await conn.DisposeAsync(); return; }

			var envelope = Envelope.FromBytes(bytes);

			// Elevator registration — hold this connection, don't dispose it
			if (envelope.Type == nameof(ElevatorHello))
			{
				await HandleElevatorHello(conn, envelope.Unwrap<ElevatorHello>(), ct);
				return; // connection stays open
			}

			// REPL session — hold this connection for the session lifetime
			if (envelope.Type == nameof(ReplStartRequest))
			{
				await HandleReplSession(conn, envelope.Unwrap<ReplStartRequest>(), ct);
				return; // connection stays open until REPL ends
			}

			// Run request — may become a persistent watch connection
			if (envelope.Type == nameof(RunRequest))
			{
				var runReq = envelope.Unwrap<RunRequest>();
				var response = await HandleRunAsync(runReq);
				await conn.SendAsync(response.ToBytes(), ct);

				// If watch mode, keep connection open and push HotSwapNotify on changes
				var runResp = response.Unwrap<RunResponse>();
				if (runResp.Success && runResp.Watch)
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
				Envelope response;
				if (envelope.Type == nameof(SpawnRequest))
				{
					// CLI asking us to forward to the Elevator
					var spawnResp = await SpawnElevatedAsync(envelope.Unwrap<SpawnRequest>(), ct);
					response = Envelope.Wrap(spawnResp);
				}
				else
				{
					response = envelope.Type switch
					{
						nameof(PingRequest) => HandlePing(),
						nameof(StopRequest) => HandleStop(),
						_ => Envelope.Wrap(new RunResponse(false, false, false, null, null, null, null, 0,
							$"Unknown message type: {envelope.Type}"))
					};
				}

				await conn.SendAsync(response.ToBytes(), ct);
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

		var ack = Envelope.Wrap(new ElevatorAck(true, null));
		await conn.SendAsync(ack.ToBytes(), ct);

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

		watcher.Changed += file =>
		{
			_log.Information("File changed: {FileName}", Path.GetFileName(file));
			changeSignal.Release();
		};

		await using (conn)
		{
			while (!ct.IsCancellationRequested)
			{
				// Wait for file change (or cancellation)
				try
				{
					await changeSignal.WaitAsync(ct);
				}
				catch (OperationCanceledException) { break; }

				// Drain any extra signals from rapid saves
				while (changeSignal.CurrentCount > 0)
					await changeSignal.WaitAsync(TimeSpan.Zero);

				// Recompile
				_log.Information("Recompiling {ScriptName}", Path.GetFileName(request.ScriptPath));
				var recompile = _compiler.Compile(request.ScriptPath, request.Args);

				if (!recompile.Success)
				{
					_log.Warning("Recompile failed: {Error}", recompile.Error);
					continue; // keep watching, don't swap
				}

				// Push HotSwapNotify to CLI
				var notify = new HotSwapNotify(
					recompile.AssemblyPath!,
					recompile.DepsJsonPath,
					recompile.RuntimeConfigPath,
					recompile.ManifestPath,
					"file_changed");

				try
				{
					await conn.SendAsync(Envelope.Wrap(notify).ToBytes(), ct);
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
		await conn.SendAsync(Envelope.Wrap(new ReplStartResponse(true, null)).ToBytes(), ct);

		// Eval loop — read requests, evaluate, send responses
		await using (conn)
		{
			while (!ct.IsCancellationRequested)
			{
				var bytes = await conn.ReceiveAsync(ct);
				if (bytes == null) break; // client disconnected

				var envelope = Envelope.FromBytes(bytes);
				if (envelope.Type == nameof(ReplEvalRequest))
				{
					var evalReq = envelope.Unwrap<ReplEvalRequest>();
					var result = await session.EvalAsync(evalReq.Code);
					await conn.SendAsync(Envelope.Wrap(result).ToBytes(), ct);
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

	private Envelope HandlePing()
	{
		var uptime = (int)(DateTime.UtcNow - _startTime).TotalSeconds;
		return Envelope.Wrap(new PingResponse(Version, false, _elevator != null, uptime, 0));
	}

	private async Task<Envelope> HandleRunAsync(RunRequest request)
	{
		_log.Information("Run request: {ScriptPath}", request.ScriptPath);

		var result = _compiler.Compile(request.ScriptPath, request.Args);
		if (!result.Success)
			return Envelope.Wrap(new RunResponse(false, false, false, null, null, null, null, 0, result.Error));

		// If the script needs elevation, forward to the Elevator
		// Always return artifacts — CLI decides how to spawn.
		// If //sea_elevate, Elevated flag is set so CLI can handle it
		// (spawn directly if already elevated, use Elevator if available, or error).
		return Envelope.Wrap(new RunResponse(
			true, result.Elevate, result.Watch,
			result.AssemblyPath,
			result.DepsJsonPath,
			result.RuntimeConfigPath,
			result.ManifestPath,
			0, null));
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

		if (elevator == null)
			return new SpawnResponse(false, 0, "No elevator connected. Is the SeaShell Elevator task running?");

		try
		{
			await elevator.SendAsync(Envelope.Wrap(request).ToBytes(), ct);
			var replyBytes = await elevator.ReceiveAsync(ct);
			if (replyBytes == null)
			{
				// Elevator disconnected
				await DetachElevator();
				return new SpawnResponse(false, 0, "Elevator disconnected during spawn");
			}
			return Envelope.FromBytes(replyBytes).Unwrap<SpawnResponse>();
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

	private Envelope HandleStop()
	{
		_log.Information("Stop requested");
		Environment.SetEnvironmentVariable("SEASHELL_STOP", "1");
		return Envelope.Wrap(new PingResponse(Version, false, _elevator != null, 0, 0));
	}

	public async ValueTask DisposeAsync()
	{
		if (_elevator != null)
			await _elevator.DisposeAsync();
		await _server.DisposeAsync();
	}
}
