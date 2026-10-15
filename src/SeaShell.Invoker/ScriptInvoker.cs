using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SeaShell.Installer;
using SeaShell.Ipc;
using SeaShell.Protocol;

namespace SeaShell.Invoker;

/// <summary>
/// Core script execution engine. Compiles scripts via the daemon, executes them,
/// and handles elevation, watch mode, and reload. This is the shared client-side
/// counterpart to the SeaShell Daemon.
///
/// CLI, Host, and ServiceHost are thin wrappers around this class — the only
/// fork point is <see cref="OutputMode"/> (Inherit for CLI, Capture for Host/ServiceHost).
/// </summary>
public sealed class ScriptInvoker
{
	private readonly Action<string>? _log;
	private readonly Action<string>? _verboseLog;

	public ScriptInvoker(Action<string>? log = null, Action<string>? verboseLog = null)
	{
		_log = log;
		_verboseLog = verboseLog;
	}

	// ── Compile ─────────────────────────────────────────────────────────

	/// <summary>Compile a script via the daemon. Returns null on failure.</summary>
	public async Task<CompiledScript?> CompileAsync(
		string scriptPath, string[] args, string daemonAddress,
		CancellationToken ct = default)
	{
		await using var conn = await TransportClient.ConnectAsync(daemonAddress, ct: ct);
		var request = new RunRequest(
			scriptPath, args,
			Environment.CurrentDirectory,
			Array.Empty<string>(),
			Environment.ProcessId);

		await conn.Channel.SendAsync(request, ct);
		var reply = await conn.Channel.ReceiveAsync(ct);
		if (reply == null) return null;

		var response = (RunResponse)reply.Value.Message;
		if (!response.Success)
		{
			_log?.Invoke(response.Error ?? "Compilation failed");
			return null;
		}

		return CompiledScript.FromRunResponse(response);
	}

	// ── Run ─────────────────────────────────────────────────────────────

	/// <summary>Compile and run a script. Handles elevation, watch mode, mutex, and reload.</summary>
	public async Task<ScriptResult> RunAsync(
		string scriptPath, string[] args, string daemonAddress,
		OutputMode output, ScriptConnection? connection = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environmentVars = null,
		bool windowMode = false,
		CancellationToken ct = default)
	{
		// Pre-compilation mutex check — instant exit for blocked callers
		ScriptMutex? mutex = null;
		var scan = DirectiveScanner.Scan(scriptPath);
		if (scan.Mutex != DirectiveScanner.MutexScope.None)
		{
			var identity = DirectiveScanner.ComputeIdentity(scriptPath);
			mutex = ScriptMutex.TryAcquire(identity, scan.Mutex, _log);
			if (mutex == null)
			{
				// Blocked — check if attach mode is enabled
				if (scan.MutexAttach)
					return await AttachToRunningInstanceAsync(identity, args, _log, ct);

				_log?.Invoke($"another instance is already running (mutex: {scan.Mutex.ToString().ToLowerInvariant()})");
				return new ScriptResult(200, "", $"Another instance is already running (mutex: {scan.Mutex.ToString().ToLowerInvariant()})");
			}
		}

		try
		{
		return await RunAsyncCore(scriptPath, args, daemonAddress, output, connection, workingDirectory, environmentVars, windowMode, ct);
		}
		finally
		{
			mutex?.Dispose();
		}
	}

	private async Task<ScriptResult> RunAsyncCore(
		string scriptPath, string[] args, string daemonAddress,
		OutputMode output, ScriptConnection? connection,
		string? workingDirectory, Dictionary<string, string>? environmentVars,
		bool windowMode, CancellationToken ct)
	{
		// Ensure daemon is running — address may resolve to a compatible higher version
		var resolvedAddress = await DaemonLauncher.EnsureRunningAsync(daemonAddress, _log, _verboseLog, ct);
		if (resolvedAddress == null)
			return new ScriptResult(1, "", "Daemon failed to start");

		// Compile via daemon — keep connection open for watch mode
		var conn = await TransportClient.ConnectAsync(resolvedAddress, ct: ct);
		var request = new RunRequest(
			scriptPath, args,
			Environment.CurrentDirectory,
			Array.Empty<string>(),
			Environment.ProcessId);

		await conn.Channel.SendAsync(request, ct);
		var reply = await conn.Channel.ReceiveAsync(ct);
		if (reply == null)
		{
			await conn.DisposeAsync();
			return new ScriptResult(1, "", "Daemon disconnected");
		}

		var response = (RunResponse)reply.Value.Message;
		if (!response.Success)
		{
			await conn.DisposeAsync();
			var error = response.Error ?? "Compilation failed";
			_log?.Invoke(error);
			return new ScriptResult(1, "", error);
		}

		var compiled = CompiledScript.FromRunResponse(response)!;

		// Dispatch based on compilation flags
		if (compiled.Elevated && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			await conn.DisposeAsync();

			if (IsCurrentProcessElevated())
				return await ExecuteDirectAsync(compiled, args, resolvedAddress, scriptPath, output, connection, workingDirectory, environmentVars, windowMode, ct);

			return await ExecuteElevatedAsync(compiled, args, resolvedAddress, output, ct);
		}

		if (compiled.Watch)
		{
			// Watch mode keeps the daemon connection open for HotSwapNotify
			return await ExecuteWatchAsync(conn, resolvedAddress, compiled, args, output, connection, workingDirectory, environmentVars, windowMode, ct);
		}

		await conn.DisposeAsync();
		return await ExecuteDirectAsync(compiled, args, resolvedAddress, scriptPath, output, connection, workingDirectory, environmentVars, windowMode, ct);
	}

	/// <summary>Execute an already-compiled script.</summary>
	public Task<ScriptResult> ExecuteAsync(
		CompiledScript compiled, string[] args,
		string? daemonAddress, string? scriptPath,
		OutputMode output, ScriptConnection? connection = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environmentVars = null,
		bool windowMode = false,
		CancellationToken ct = default)
	{
		return ExecuteDirectAsync(compiled, args, daemonAddress, scriptPath, output, connection, workingDirectory, environmentVars, windowMode, ct, precompiled: true);
	}

	// ── Direct execution ────────────────────────────────────────────────

	private async Task<ScriptResult> ExecuteDirectAsync(
		CompiledScript compiled, string[] args,
		string? daemonAddress, string? scriptPath,
		OutputMode output, ScriptConnection? connection,
		string? workingDirectory, Dictionary<string, string>? environmentVars,
		bool windowMode, CancellationToken ct, bool precompiled = false)
	{
		var reloadCount = 0;
		var restartCount = 0;
		var currentCompiled = compiled;
		string? stateBase64 = null;
		var consecutiveFastExits = 0;
		// Compiled earlier (ExecuteAsync): record the hit before starting, so the cache
		// sweep cannot take the entry from under the starting script.
		await using var keepAlive = await CacheKeepAlive.StartAsync(daemonAddress, compiled, touchNow: precompiled, ct);
		if (precompiled && !ArtifactsPresent(compiled))
			return new ScriptResult(1, "", "Compiled script is no longer in the cache (unused for over a day) — compile it again");

		if (compiled.Restart)
			_verboseLog?.Invoke("restart mode active");

		while (true)
		{
			var sw = Stopwatch.StartNew();

			var result = await RunOneInstanceAsync(
				currentCompiled, args, output, connection,
				reloadCount, stateBase64, watch: false,
				workingDirectory, environmentVars,
				restart: compiled.Restart, restartCount: restartCount,
				windowMode: windowMode, ct: ct);

			if (!result.ReloadRequested)
			{
				// Check restart: directive must be active, script must not have opted out,
				// and we must not have been cancelled (Ctrl+C)
				if (compiled.Restart && result.Restart && !ct.IsCancellationRequested)
				{
					restartCount++;
					sw.Stop();

					// Crash backoff: if the process lived less than 5 seconds, delay
					if (sw.Elapsed.TotalSeconds < 5)
					{
						consecutiveFastExits++;
						var delaySec = Math.Min(1 << consecutiveFastExits, 8); // 2, 4, 8, 8...
						_log?.Invoke($"restarting in {delaySec}s (fast exit #{consecutiveFastExits})...");
						try { await Task.Delay(delaySec * 1000, ct); }
						catch (OperationCanceledException) { return new ScriptResult(result.ExitCode, result.Stdout, result.Stderr, result.ExitDelay) { Cancelled = true }; }
					}
					else
					{
						consecutiveFastExits = 0;
						_log?.Invoke("restarting...");
					}

					continue;
				}

				return new ScriptResult(result.ExitCode, result.Stdout, result.Stderr, result.ExitDelay) { Cancelled = result.Cancelled };
			}

			// Script requested reload — recompile via daemon
			if (daemonAddress == null || scriptPath == null)
				return new ScriptResult(result.ExitCode, result.Stdout, result.Stderr, result.ExitDelay) { Cancelled = ct.IsCancellationRequested };

			_log?.Invoke($"reload requested ({result.ReloadReason ?? "script"})...");

			CompiledScript? recompiled = null;
			try
			{
				await using var conn = await TransportClient.ConnectAsync(daemonAddress, ct: ct);
				var runReq = new RunRequest(scriptPath, args, Environment.CurrentDirectory,
					Array.Empty<string>(), Environment.ProcessId, result.ClearCache);
				await conn.Channel.SendAsync(runReq, ct);
				var reply = await conn.Channel.ReceiveAsync(ct);
				if (reply != null)
				{
					var response = (RunResponse)reply.Value.Message;
					if (response.Success)
						recompiled = CompiledScript.FromRunResponse(response);
					else
						_log?.Invoke($"recompile failed: {response.Error}");
				}
			}
			catch { }

			if (recompiled == null)
				return new ScriptResult(result.ExitCode, result.Stdout, result.Stderr, result.ExitDelay) { Cancelled = ct.IsCancellationRequested };

			stateBase64 = result.State;
			currentCompiled = recompiled;
			keepAlive.Update(currentCompiled);
			reloadCount++;
		}
	}

	/// <summary>
	/// All of a compiled script's files still exist. A binary run in place keeps its
	/// executable when its staged manifest expires, and would start without Sea context.
	/// </summary>
	private static bool ArtifactsPresent(CompiledScript compiled) =>
		new[] { compiled.AssemblyPath, compiled.ManifestPath, compiled.RuntimeConfigPath, compiled.DepsJsonPath }
			.All(path => path == null || File.Exists(path));

	// ── Elevated execution ──────────────────────────────────────────────

	private async Task<ScriptResult> ExecuteElevatedAsync(
		CompiledScript compiled, string[] args,
		string daemonAddress, OutputMode output,
		CancellationToken ct)
	{
		var restartCount = 0;
		var consecutiveFastExits = 0;
		await using var keepAlive = await CacheKeepAlive.StartAsync(daemonAddress, compiled, touchNow: false, ct);

		while (true)
		{
			var sw = Stopwatch.StartNew();
			var pipeName = $"seashell-{Guid.NewGuid():N}";

			var envVars = new List<string>
			{
				$"SEASHELL_PIPE={pipeName}",
				$"SEASHELL_CLI_PID={Environment.ProcessId}",
			};

			var spawnReq = new SpawnRequest(
				compiled.AssemblyPath,
				compiled.DepsJsonPath,
				compiled.RuntimeConfigPath,
				args,
				Environment.CurrentDirectory,
				envVars.ToArray(),
				Environment.ProcessId,
				DirectExe: compiled.DirectExe);

			var spawnResult = await DaemonManager.RequestElevatedSpawnAsync(daemonAddress, spawnReq);

			// Elevator not connected — try starting it, then retry with wait. Never
			// retry a request that may have reached an elevator: it could be running.
			if (!spawnResult.Success && !spawnResult.MayHaveStarted)
			{
				if (TryStartElevator())
				{
					_log?.Invoke("starting elevator...");
					var retryReq = spawnReq with { AwaitElevatorMs = 15_000 };
					spawnResult = await DaemonManager.RequestElevatedSpawnAsync(daemonAddress, retryReq);
				}
			}

			if (!spawnResult.Success)
			{
				_log?.Invoke("script requires elevation (//sea_elevate)");
				_log?.Invoke($"elevator: {spawnResult.Error}");
				return new ScriptResult(1, "", $"Elevation failed: {spawnResult.Error}");
			}

			try
			{
				var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
				pipe.Connect(10_000);

				await using var channel = new MessageChannel(pipe);
				await channel.SendAsync(BuildScriptInit(compiled, args,
					reloadCount: 0, launcherPid: Environment.ProcessId,
					restart: compiled.Restart, restartCount: restartCount,
					mutexScope: compiled.MutexScope, mutexAttach: compiled.MutexAttach,
					log: _log), ct);

				var (exitCode, _, exitDelay, scriptRestart) = await ReceiveUntilExitAsync(channel, ct);

				// Cancelled: the elevated script cannot be terminated from here, only asked to stop
				if (ct.IsCancellationRequested)
				{
					await TrySendAsync(channel, new ScriptStop());
					return new ScriptResult(exitCode, "", "", exitDelay) { Cancelled = true };
				}

				// Check restart
				if (compiled.Restart && scriptRestart && !ct.IsCancellationRequested)
				{
					restartCount++;
					sw.Stop();

					if (sw.Elapsed.TotalSeconds < 5)
					{
						consecutiveFastExits++;
						var delaySec = Math.Min(1 << consecutiveFastExits, 8);
						_log?.Invoke($"restarting elevated in {delaySec}s (fast exit #{consecutiveFastExits})...");
						try { await Task.Delay(delaySec * 1000, ct); }
						catch (OperationCanceledException) { return new ScriptResult(exitCode, "", "", exitDelay); }
					}
					else
					{
						consecutiveFastExits = 0;
						_log?.Invoke("restarting elevated...");
					}

					continue;
				}

				return new ScriptResult(exitCode, "", "", exitDelay);
			}
			catch (Exception ex)
			{
				return new ScriptResult(1, "", $"Elevated script communication failed: {ex.Message}");
			}
		}
	}

	private static bool TryStartElevator() =>
		DaemonTasks.TryRunElevatorTask();

	// ── Watch mode ──────────────────────────────────────────────────────

	private async Task<ScriptResult> ExecuteWatchAsync(
		TransportStream daemonConn, string daemonAddress, CompiledScript initial, string[] args,
		OutputMode output, ScriptConnection? connection,
		string? workingDirectory, Dictionary<string, string>? environmentVars,
		bool windowMode, CancellationToken ct)
	{
		var reloadCount = 0;
		var restartCount = 0;
		var consecutiveFastExits = 0;
		var currentCompiled = initial;
		string? stateBase64 = null;
		await using var keepAlive = await CacheKeepAlive.StartAsync(daemonAddress, initial, touchNow: false, ct);

		// One receive on the daemon connection at a time, kept across script instances:
		// a second one would queue behind it and the notification it gets would be lost
		var hotSwapTask = daemonConn.Channel.ReceiveAsync().AsTask();

		try
		{
			while (!ct.IsCancellationRequested)
			{
				var sw = Stopwatch.StartNew();
				var pipeName = $"seashell-{Guid.NewGuid():N}";
				var psi = BuildProcessStartInfo(currentCompiled, args, output, workingDirectory, environmentVars, windowMode);
				psi.Environment["SEASHELL_PIPE"] = pipeName;

				using var script = ScriptProcess.Start(psi, containTree: output == OutputMode.Capture);
				var proc = script.Process;

				var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
				MessageChannel scriptChannel;
				try
				{
					await pipe.ConnectAsync(5000, ct);
					scriptChannel = new MessageChannel(pipe);

					if (connection != null) connection.Channel = scriptChannel;

					await scriptChannel.SendAsync(BuildScriptInit(currentCompiled, args,
						reloadCount: reloadCount, stateBase64: stateBase64, watch: true,
						restart: initial.Restart, restartCount: restartCount,
						mutexScope: initial.MutexScope, mutexAttach: initial.MutexAttach,
						windowMode: windowMode, log: _log), ct);
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					if (connection != null) connection.Channel = null;
					return await StopCancelledAsync(script, channel: null);
				}

				// Channel reader with reload request signaling
				var reloadRequests = Channel.CreateUnbounded<(string? reason, bool clearCache)>();
				var channelTask = ReceiveUntilExitOrStateAsync(scriptChannel, connection, reloadRequests.Writer, ct);
				HotSwapNotify? swapNotify = null;
				var restarting = false;

				// Inner event loop for this process instance. Cancellation ends channelTask.
				while (true)
				{
					var reloadTask = reloadRequests.Reader.ReadAsync().AsTask();
					var completed = await Task.WhenAny(channelTask, hotSwapTask, reloadTask);

					if (completed == channelTask)
					{
						var (exitCode, _, exitDelay, scriptRestart) = await channelTask;

						// Check restart in watch mode
						if (initial.Restart && scriptRestart && !ct.IsCancellationRequested)
						{
							restartCount++;
							var elapsed = sw.Elapsed;

							if (connection != null) connection.Channel = null;
							await scriptChannel.DisposeAsync();
							TryKillAfterTimeout(proc);

							if (elapsed.TotalSeconds < 5)
							{
								consecutiveFastExits++;
								var delaySec = Math.Min(1 << consecutiveFastExits, 8);
								_log?.Invoke($"restarting in {delaySec}s (fast exit #{consecutiveFastExits})...");
								try { await Task.Delay(delaySec * 1000, ct); }
								catch (OperationCanceledException) { break; }
							}
							else
							{
								consecutiveFastExits = 0;
								_log?.Invoke("restarting...");
							}

							restarting = true;
							break; // break inner loop, outer while creates new process
						}

						// Cancellation ends the channel read without telling the script: stop it
						if (!await script.WaitForExitAsync(ct))
							return await StopCancelledAsync(script, scriptChannel);
						var (so, se) = await script.CollectOutputAsync();
						return new ScriptResult(script.ExitCode, so, se, exitDelay);
					}

					if (completed == reloadTask)
					{
						var (reason, clearCache) = await reloadTask;
						_log?.Invoke($"reload requested ({reason ?? "script"})...");
						try { await daemonConn.Channel.SendAsync(new RecompileRequest(clearCache), ct); }
						catch (OperationCanceledException) when (ct.IsCancellationRequested)
						{
							continue; // channelTask ends too: the script is stopped there
						}
						catch
						{
							_log?.Invoke("daemon disconnected");
							return new ScriptResult(1, "", "Daemon disconnected during watch");
						}
						continue;
					}

					// HotSwapNotify from daemon
					var swapResult = await hotSwapTask;
					if (swapResult == null)
					{
						_log?.Invoke("daemon disconnected during watch");
						return new ScriptResult(1, "", "Daemon disconnected during watch");
					}
					hotSwapTask = daemonConn.Channel.ReceiveAsync().AsTask(); // this one is consumed

					if (swapResult.Value.Type != MessageType.HotSwapNotify) continue;

					var notify = (HotSwapNotify)swapResult.Value.Message;
					if (string.IsNullOrEmpty(notify.AssemblyPath))
					{
						_log?.Invoke("recompile failed, continuing...");
						continue;
					}

					swapNotify = notify;
					break;
				}

				if (swapNotify == null)
				{
					if (restarting) continue; // a new process for the same compiled script
					break;
				}

				reloadCount++;
				_log?.Invoke($"reloading ({swapNotify.Reason})...");

				// Signal old script to reload, collect state, kill
				await TrySendAsync(scriptChannel, new ScriptReload());

				stateBase64 = null;
				try
				{
					using var cts = new CancellationTokenSource(5000);
					var (_, state, _, _) = await channelTask.WaitAsync(cts.Token);
					stateBase64 = state;
				}
				catch { }

				if (connection != null) connection.Channel = null;
				await scriptChannel.DisposeAsync();

				// Cancelled meanwhile: end this instance and what it started instead of swapping
				if (await EndForReloadAsync(script, ct))
					return await StopCancelledAsync(script, channel: null);

				currentCompiled = new CompiledScript(
					swapNotify.AssemblyPath,
					swapNotify.DepsJsonPath,
					swapNotify.RuntimeConfigPath,
					swapNotify.ManifestPath,
					swapNotify.StartupHookPath,
					swapNotify.DirectExe,
					false, true,
					swapNotify.Restart);
				keepAlive.Update(currentCompiled);
			}
		}
		finally
		{
			await daemonConn.DisposeAsync();
		}

		return new ScriptResult(0, "", "") { Cancelled = ct.IsCancellationRequested };
	}

	// ── Single instance ─────────────────────────────────────────────────

	private sealed record InstanceResult(
		int ExitCode, string Stdout, string Stderr, int ExitDelay,
		bool ReloadRequested, bool ClearCache, string? ReloadReason, string? State,
		bool Restart = true, bool Cancelled = false);

	private async Task<InstanceResult> RunOneInstanceAsync(
		CompiledScript compiled, string[] args,
		OutputMode output, ScriptConnection? connection,
		int reloadCount, string? stateBase64, bool watch,
		string? workingDirectory, Dictionary<string, string>? environmentVars,
		bool restart = false, int restartCount = 0,
		bool windowMode = false, CancellationToken ct = default)
	{
		var pipeName = $"seashell-{Guid.NewGuid():N}";
		var psi = BuildProcessStartInfo(compiled, args, output, workingDirectory, environmentVars, windowMode);
		psi.Environment["SEASHELL_PIPE"] = pipeName;

		// A captured run (Host) is cancelled together with everything the script started.
		// The CLI leaves that to the console: Ctrl+C reaches the script's children too.
		using var script = ScriptProcess.Start(psi, containTree: output == OutputMode.Capture);

		async Task<InstanceResult> CancelledAsync(MessageChannel? channel)
		{
			var r = await StopCancelledAsync(script, channel);
			if (connection != null) connection.Channel = null;
			return new InstanceResult(r.ExitCode, r.StandardOutput, r.StandardError, 0, false, false, null, null, Cancelled: true);
		}

		// Connect to script's pipe server
		var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		try
		{
			await pipe.ConnectAsync(5000, ct);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			return await CancelledAsync(channel: null);
		}
		catch
		{
			// Pipe connect failed — directives that require IPC cannot function
			if (restart)
			{
				_log?.Invoke("IPC pipe failed — //sea_restart requires IPC, aborting");
				TryKillAfterTimeout(script.Process);
				return new InstanceResult(1, "", "IPC pipe failed — //sea_restart requires IPC", 0, false, false, null, null, false);
			}

			// No directives need IPC — run without Sea context
			if (!await script.WaitForExitAsync(ct))
				return await CancelledAsync(channel: null);
			var (o, e) = await script.CollectOutputAsync();
			return new InstanceResult(script.ExitCode, o, e, 0, false, false, null, null);
		}

		await using var channel = new MessageChannel(pipe);
		if (connection != null) connection.Channel = channel;

		try
		{
			await channel.SendAsync(BuildScriptInit(compiled, args,
				reloadCount: reloadCount, stateBase64: stateBase64, watch: watch,
				restart: restart, restartCount: restartCount,
				mutexScope: compiled.MutexScope, mutexAttach: compiled.MutexAttach,
				windowMode: windowMode, log: _log), ct);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// The script never got its init: nothing to ask it over the channel
			return await CancelledAsync(channel: null);
		}

		// Drain channel with reload awareness
		var reloadRequests = Channel.CreateUnbounded<(string?, bool)>();
		var channelTask = ReceiveUntilExitOrStateAsync(channel, connection, reloadRequests.Writer, CancellationToken.None);

		var reloadTask = reloadRequests.Reader.ReadAsync(CancellationToken.None).AsTask();
		var exitTask = script.Process.WaitForExitAsync();

		var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var ctReg = ct.Register(() => cancelTcs.TrySetResult());

		var completed = await Task.WhenAny(exitTask, reloadTask, cancelTcs.Task);

		if (completed == exitTask)
		{
			var (stdout, stderr) = await script.CollectOutputAsync();
			var (_, _, exitDelay, scriptRestart) = await channelTask;
			if (connection != null) connection.Channel = null;
			return new InstanceResult(script.ExitCode, stdout, stderr, exitDelay, false, false, null, null, scriptRestart);
		}

		if (completed == cancelTcs.Task)
			return await CancelledAsync(channel);

		// Script requested reload
		var (reason, clearCache) = await reloadTask;

		// Signal script to save state and exit
		string? state = null;
		await TrySendAsync(channel, new ScriptReload());
		try
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(5000);
			var (_, s, _, _) = await channelTask.WaitAsync(cts.Token);
			state = s;
		}
		catch { }

		// Cancelled during the reload: end the script and what it started instead
		if (await EndForReloadAsync(script, ct))
			return await CancelledAsync(channel);

		var (so2, se2) = await script.CollectOutputAsync();
		// The run goes on with the reloaded instance, so a cancellation while this one's
		// output drained (a child holding it) still ends what it started
		if (ct.IsCancellationRequested)
			return await CancelledAsync(channel);
		if (connection != null) connection.Channel = null;
		return new InstanceResult(0, so2, se2, 0, true, clearCache, reason, state);
	}

	/// <summary>
	/// End a cancelled run: ask the script to stop when it is running and there is a channel
	/// to ask it over, give it the grace period, then terminate it — a captured run with
	/// everything it started — and collect its output.
	/// </summary>
	private static async Task<ScriptResult> StopCancelledAsync(ScriptProcess script, MessageChannel? channel)
	{
		if (channel != null && !script.Process.HasExited)
			await TrySendAsync(channel, new ScriptStop());
		await script.StopAsync(ScriptProcess.StopGracePeriod);
		var (stdout, stderr) = await script.CollectOutputAsync();
		return new ScriptResult(script.ExitCode, stdout, stderr) { Cancelled = true };
	}

	/// <summary>
	/// Make way for the reloaded instance: give the script three seconds to exit, then kill
	/// it. True if <paramref name="ct"/> fired before or during this — the caller then ends
	/// the script and what it started as for any cancellation, even if the script is gone.
	/// </summary>
	private static async Task<bool> EndForReloadAsync(ScriptProcess script, CancellationToken ct)
	{
		using (var exitWait = CancellationTokenSource.CreateLinkedTokenSource(ct))
		{
			exitWait.CancelAfter(3000);
			await script.WaitForExitAsync(exitWait.Token);
		}
		if (ct.IsCancellationRequested)
			return true;

		TryKillAfterTimeout(script.Process, 0);
		await script.WaitForExitAsync(ScriptProcess.KillWaitTimeout);
		return ct.IsCancellationRequested;
	}

	/// <summary>
	/// Send a control message (ScriptStop, ScriptReload) to the script. Bounded: a script
	/// that stopped reading, or a large host message ahead of it, cannot hold this up.
	/// </summary>
	private static async Task TrySendAsync<T>(MessageChannel channel, T message) where T : notnull
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		try { await channel.SendAsync(message, timeout.Token); } catch { }
	}

	// ── Attach client ───────────────────────────────────────────────────

	/// <summary>
	/// Connect to an already-running instance's attach pipe, send args + CWD,
	/// relay any messages, and wait for close. No compilation, no script process.
	/// </summary>
	private static async Task<ScriptResult> AttachToRunningInstanceAsync(
		string identity, string[] args, Action<string>? log, CancellationToken ct)
	{
		var pipeName = ScriptMutex.GetAttachPipeName(identity);
		log?.Invoke("attaching to running instance...");

		try
		{
			var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
			pipe.Connect(5000);

			await using var channel = new MessageChannel(pipe);

			// Send handshake
			await channel.SendAsync(new AttachHello(args, Environment.CurrentDirectory), ct);

			// Relay messages until close or timeout
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(30_000);

			while (!cts.IsCancellationRequested)
			{
				var result = await channel.ReceiveAsync(cts.Token);
				if (result == null) break;

				switch (result.Value.Type)
				{
					case MessageType.AttachClose:
						var close = (AttachClose)result.Value.Message;
						return new ScriptResult(close.ExitCode, "", "");

					case MessageType.AttachMessage:
						var msg = (AttachMessage)result.Value.Message;
						// Relay payload to stdout
						var text = System.Text.Encoding.UTF8.GetString(msg.Payload);
						System.Console.Write(text);
						break;
				}
			}

			return new ScriptResult(0, "", "");
		}
		catch (TimeoutException)
		{
			log?.Invoke("attach pipe not available (running instance may not support //sea_mutex_attach)");
			return new ScriptResult(200, "", "Attach pipe not available");
		}
		catch (OperationCanceledException)
		{
			return new ScriptResult(0, "", "");
		}
		catch (Exception ex)
		{
			log?.Invoke($"attach failed: {ex.Message}");
			return new ScriptResult(200, "", $"Attach failed: {ex.Message}");
		}
	}

	// ── Process lifecycle ───────────────────────────────────────────────

	/// <summary>Wait for the process to exit within <paramref name="timeoutMs"/>, then kill if still running.</summary>
	private static void TryKillAfterTimeout(Process proc, int timeoutMs = 3000)
	{
		try
		{
			if (!proc.WaitForExit(timeoutMs))
				proc.Kill(entireProcessTree: false);
		}
		catch { }
	}

	// ── Helpers ─────────────────────────────────────────────────────────

	internal static ProcessStartInfo BuildProcessStartInfo(
		CompiledScript compiled, string[] args, OutputMode output,
		string? workingDirectory = null,
		Dictionary<string, string>? environmentVars = null,
		bool windowMode = false)
	{
		var cwd = workingDirectory ?? Environment.CurrentDirectory;
		ProcessStartInfo psi;

		if (compiled.DirectExe)
		{
			psi = new ProcessStartInfo
			{
				FileName = compiled.AssemblyPath,
				WorkingDirectory = cwd,
			};
		}
		else
		{
			psi = new ProcessStartInfo
			{
				FileName = "dotnet",
				WorkingDirectory = cwd,
			};
			psi.ArgumentList.Add("exec");
			if (compiled.RuntimeConfigPath != null)
			{
				psi.ArgumentList.Add("--runtimeconfig");
				psi.ArgumentList.Add(compiled.RuntimeConfigPath);
			}
			if (compiled.DepsJsonPath != null)
			{
				psi.ArgumentList.Add("--depsfile");
				psi.ArgumentList.Add(compiled.DepsJsonPath);
			}
			psi.ArgumentList.Add(compiled.AssemblyPath);
		}

		if (output == OutputMode.Capture)
		{
			psi.UseShellExecute = false;
			psi.RedirectStandardOutput = true;
			psi.RedirectStandardError = true;
			psi.CreateNoWindow = true;
		}
		else if (windowMode)
		{
			// Window mode (seaw.exe): suppress console window on the child process.
			// Output is still inherited (not redirected) — it just goes nowhere since
			// the parent has no console.
			psi.UseShellExecute = false;
			psi.CreateNoWindow = true;
		}

		if (compiled.StartupHookPath != null)
			psi.Environment["DOTNET_STARTUP_HOOKS"] = compiled.StartupHookPath;

		if (environmentVars != null)
		{
			foreach (var (key, value) in environmentVars)
				psi.Environment[key] = value;
		}

		foreach (var a in args)
			psi.ArgumentList.Add(a);

		return psi;
	}

	internal static ScriptInit BuildScriptInit(
		CompiledScript compiled, string[] args,
		int reloadCount = 0, int launcherPid = 0,
		string? stateBase64 = null, bool watch = false,
		bool isConsoleEphemeral = false,
		bool restart = false, int restartCount = 0,
		byte mutexScope = 0, bool mutexAttach = false,
		bool windowMode = false,
		Action<string>? log = null)
	{
		string? scriptPath = null;
		string[]? sources = null;
		Dictionary<string, string>? packages = null;
		string[]? assemblies = null;

		if (compiled.ManifestPath != null && File.Exists(compiled.ManifestPath))
		{
			try
			{
				var json = File.ReadAllText(compiled.ManifestPath);
				var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
				var manifest = JsonSerializer.Deserialize<ManifestData>(json, opts);
				if (manifest != null)
				{
					scriptPath = manifest.ScriptPath;
					sources = manifest.Sources;
					packages = manifest.Packages;
					assemblies = manifest.Assemblies;
				}
			}
			catch (Exception ex)
			{
				log?.Invoke($"manifest deserialization failed ({compiled.ManifestPath}): {ex.Message}");
			}
		}

		return new ScriptInit(
			scriptPath,
			Environment.CurrentDirectory,
			args,
			sources, packages, assemblies,
			isConsoleEphemeral,
			launcherPid,
			reloadCount,
			stateBase64,
			watch,
			restart,
			restartCount,
			mutexScope,
			mutexAttach,
			windowMode);
	}

	private static async Task<(int exitCode, string? state, int exitDelay, bool restart)> ReceiveUntilExitOrStateAsync(
		MessageChannel channel, ScriptConnection? connection,
		ChannelWriter<(string?, bool)>? reloadWriter, CancellationToken ct)
	{
		string? state = null;
		int exitDelay = 0;
		try
		{
			while (true)
			{
				var msg = await channel.ReceiveAsync(ct);
				if (msg == null) return (1, state, exitDelay, true);

				switch (msg.Value.Type)
				{
					case MessageType.ScriptExit:
						var exit = (ScriptExit)msg.Value.Message;
						return (exit.ExitCode, state, exit.ExitDelay, exit.Restart);

					case MessageType.ScriptState:
						var s = (ScriptState)msg.Value.Message;
						if (!string.IsNullOrEmpty(s.Data))
							state = s.Data;
						break;

					case MessageType.ScriptReloadRequest:
						var req = (ScriptReloadRequest)msg.Value.Message;
						reloadWriter?.TryWrite((req.Reason, req.ClearCache));
						break;

					case MessageType.ScriptMessage when connection != null:
						var sm = (ScriptMessage)msg.Value.Message;
						try { connection.RaiseMessageReceived(sm.Payload, sm.Topic); } catch { }
						break;

					case MessageType.ConsoleData:
						var cd = (ConsoleData)msg.Value.Message;
						await HandleConsoleDataAsync(channel, cd, ct);
						break;

					case MessageType.ConsoleControl:
						var cc = (ConsoleControl)msg.Value.Message;
						await HandleConsoleControlAsync(channel, cc, ct);
						break;
				}
			}
		}
		catch (OperationCanceledException)
		{
			return (1, state, exitDelay, true);
		}
		catch
		{
			return (1, state, exitDelay, true);
		}
	}

	private static async Task<(int exitCode, string? state, int exitDelay, bool restart)> ReceiveUntilExitAsync(
		MessageChannel channel, CancellationToken ct)
	{
		return await ReceiveUntilExitOrStateAsync(channel, null, null, ct);
	}

	// ── Console proxy (CLI side) ────────────────────────────────────────

	/// <summary>Handle ConsoleData from elevated script: stdout, stderr, stdin request.</summary>
	private static async Task HandleConsoleDataAsync(MessageChannel channel, ConsoleData cd, CancellationToken ct)
	{
		switch (cd.Stream)
		{
			case 0: // stdout
				Console.Out.Write(System.Text.Encoding.UTF8.GetString(cd.Data));
				break;
			case 1: // stderr
				Console.Error.Write(System.Text.Encoding.UTF8.GetString(cd.Data));
				break;
			case 2: // stdin request — read a line and send it back
				var line = Console.In.ReadLine() ?? "";
				try { await channel.SendAsync(new ConsoleData(2, System.Text.Encoding.UTF8.GetBytes(line)), ct); }
				catch { }
				break;
		}
	}

	/// <summary>Handle ConsoleControl from elevated script: execute console ops on the real terminal.</summary>
	private static async Task HandleConsoleControlAsync(MessageChannel channel, ConsoleControl cc, CancellationToken ct)
	{
		byte[]? response = null;

		try
		{
			switch (cc.Op)
			{
				// ── Fire-and-forget ops ──
				case 0: // SetTitle
					Console.Title = System.Text.Encoding.UTF8.GetString(cc.Data);
					break;
				case 2: // SetForegroundColor
					if (cc.Data.Length > 0) Console.ForegroundColor = (ConsoleColor)cc.Data[0];
					break;
				case 4: // SetBackgroundColor
					if (cc.Data.Length > 0) Console.BackgroundColor = (ConsoleColor)cc.Data[0];
					break;
				case 6: // ResetColor
					Console.ResetColor();
					break;
				case 7: // SetCursorPosition
					if (cc.Data.Length >= 8)
					{
						var left = BitConverter.ToInt32(cc.Data, 0);
						var top  = BitConverter.ToInt32(cc.Data, 4);
						Console.SetCursorPosition(left, top);
					}
					break;
				case 10: // SetCursorVisible
					if (cc.Data.Length > 0) Console.CursorVisible = cc.Data[0] != 0;
					break;
				case 15: // Clear
					Console.Clear();
					break;
				case 16: // Beep
					Console.Beep();
					break;

				// ── Request/response ops ──
				case 1: // GetTitle
					response = System.Text.Encoding.UTF8.GetBytes(
						RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Console.Title : "");
					break;
				case 3: // GetForegroundColor
					response = new[] { (byte)Console.ForegroundColor };
					break;
				case 5: // GetBackgroundColor
					response = new[] { (byte)Console.BackgroundColor };
					break;
				case 8: // GetCursorLeft
					response = BitConverter.GetBytes(Console.CursorLeft);
					break;
				case 9: // GetCursorTop
					response = BitConverter.GetBytes(Console.CursorTop);
					break;
				case 11: // GetBufferWidth
					response = BitConverter.GetBytes(Console.BufferWidth);
					break;
				case 12: // GetBufferHeight
					response = BitConverter.GetBytes(Console.BufferHeight);
					break;
				case 13: // GetWindowWidth
					response = BitConverter.GetBytes(Console.WindowWidth);
					break;
				case 14: // GetWindowHeight
					response = BitConverter.GetBytes(Console.WindowHeight);
					break;
				case 17: // ReadKey
					var intercept = cc.Data.Length > 0 && cc.Data[0] != 0;
					var key = Console.ReadKey(intercept);
					response = new byte[]
					{
						(byte)(key.KeyChar & 0xFF), (byte)(key.KeyChar >> 8),
						(byte)key.Key,
						(byte)key.Modifiers,
					};
					break;
				case 18: // KeyAvailable
					response = new[] { Console.KeyAvailable ? (byte)1 : (byte)0 };
					break;
				case 19: // GetCursorVisible
					response = new[] { Console.CursorVisible ? (byte)1 : (byte)0 };
					break;
			}
		}
		catch
		{
			// Console op failed (no console, etc.) — send empty response for request ops
			response ??= Array.Empty<byte>();
		}

		if (response != null)
		{
			try { await channel.SendAsync(new ConsoleControl(cc.Op, response), ct); }
			catch { }
		}
	}

	/// <summary>Check whether the current process is running elevated (admin/root).</summary>
	public static bool IsCurrentProcessElevated()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
			var principal = new System.Security.Principal.WindowsPrincipal(identity);
			return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
		}
		return Environment.UserName == "root";
	}
}
