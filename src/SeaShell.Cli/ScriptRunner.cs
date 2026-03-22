using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SeaShell.Ipc;
using SeaShell.Protocol;

namespace SeaShell.Cli;

static class ScriptRunner
{
	/// <summary>
	/// Exit delay (seconds) reported by the last script via ScriptExit.
	/// Used by ConsoleHelper.ExitDelay for ephemeral consoles.
	/// </summary>
	internal static int LastExitDelay = 7;

	/// <summary>
	/// Full script execution flow: ensure daemon, compile, handle elevation/watch/direct.
	/// </summary>
	public static async Task<int> RunScriptAsync(string scriptPath, string[] scriptArgs, string daemonAddress, bool isConsoleEphemeral)
	{
		// Ensure daemon is running (and matches current build)
		await EnsureDaemonAsync(daemonAddress);
		if (!await TransportClient.ProbeAsync(daemonAddress))
		{
			Console.Error.WriteLine("sea: daemon failed to start");
			return 1;
		}

		// Send run request — the Daemon compiles and returns artifacts
		var request = new RunRequest(
			scriptPath,
			scriptArgs,
			Environment.CurrentDirectory,
			Array.Empty<string>(),
			Environment.ProcessId
		);

		await using var conn = await TransportClient.ConnectAsync(daemonAddress);
		await conn.Channel.SendAsync(request);
		var reply = await conn.Channel.ReceiveAsync();
		if (reply == null)
		{
			Console.Error.WriteLine("sea: daemon disconnected");
			return 1;
		}

		var response = (RunResponse)reply.Value.Message;
		if (!response.Success)
		{
			Console.Error.WriteLine(response.Error);
			return 1;
		}

		if (response.Elevated && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			if (DaemonManager.IsCurrentProcessElevated())
				return await RunDirectAsync(response, scriptArgs, daemonAddress, scriptPath, isConsoleEphemeral);

			return await RunElevatedAsync(response, scriptArgs, isConsoleEphemeral, daemonAddress);
		}
		else if (response.Watch)
		{
			return await RunWatchAsync(conn, response, scriptArgs);
		}
		else
		{
			return await RunDirectAsync(response, scriptArgs, daemonAddress, scriptPath, isConsoleEphemeral);
		}
	}

	// ── Direct execution (non-elevated, or already-elevated CLI) ────────

	public static async Task<int> RunDirectAsync(RunResponse response, string[] scriptArgs,
		string? daemonAddress = null, string? scriptPath = null, bool isConsoleEphemeral = false)
	{
		var reloadCount = 0;
		var currentResponse = response;
		string? stateBase64 = null;
		Process? currentProc = null;

		Console.CancelKeyPress += (_, e) =>
		{
			e.Cancel = true;
			try { currentProc?.Kill(entireProcessTree: false); } catch { }
		};

		while (true)
		{
			var pipeName = $"seashell-{Guid.NewGuid():N}";
			var psi = BuildProcessStartInfo(currentResponse, scriptArgs);
			psi.Environment["SEASHELL_PIPE"] = pipeName;

			currentProc = Process.Start(psi)!;

			// Connect to script's pipe server (created by Sea.Initialize).
			var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
			try
			{
				pipe.Connect(5000);
			}
			catch
			{
				Console.Error.WriteLine("sea: script pipe connect failed");
				await currentProc.WaitForExitAsync();
				return currentProc.ExitCode;
			}

			await using var channel = new MessageChannel(pipe);
			await channel.SendAsync(BuildScriptInit(currentResponse, scriptArgs,
				isConsoleEphemeral, reloadCount: reloadCount, stateBase64: stateBase64));

			// Read from pipe with reload awareness
			var reloadRequests = Channel.CreateUnbounded<(string? reason, bool clearCache)>();
			var channelTask = ReceiveUntilExitOrStateAsync(channel, reloadRequests.Writer);
			var reloadTask = reloadRequests.Reader.ReadAsync().AsTask();

			var completed = await Task.WhenAny(channelTask, reloadTask);

			if (completed == channelTask)
			{
				// Script exited on its own
				var (exitCode, _) = await channelTask;
				await currentProc.WaitForExitAsync();
				return exitCode;
			}

			// Script requested reload — open new daemon connection to recompile
			if (daemonAddress == null || scriptPath == null)
			{
				// No daemon address — can't recompile, just wait for exit
				var (exitCode, _) = await channelTask;
				await currentProc.WaitForExitAsync();
				return exitCode;
			}

			var (reason, clearCache) = await reloadTask;
			Console.Error.WriteLine($"sea: reload requested ({reason ?? "script"})...");

			// Recompile via daemon
			var runReq = new RunRequest(scriptPath, scriptArgs, Environment.CurrentDirectory,
				Array.Empty<string>(), Environment.ProcessId, clearCache);
			RunResponse? newResponse = null;

			try
			{
				await using var conn = await TransportClient.ConnectAsync(daemonAddress);
				await conn.Channel.SendAsync(runReq);
				var reply = await conn.Channel.ReceiveAsync();
				if (reply != null)
					newResponse = (RunResponse)reply.Value.Message;
			}
			catch { }

			if (newResponse == null || !newResponse.Success)
			{
				Console.Error.WriteLine($"sea: recompile failed{(newResponse?.Error != null ? $": {newResponse.Error}" : "")}");
				// Keep running old script — wait for exit or next reload request
				var (exitCode, _) = await channelTask;
				await currentProc.WaitForExitAsync();
				return exitCode;
			}

			reloadCount++;

			// Signal old script to reload, collect state, kill
			try { await channel.SendAsync(new ScriptReload()); } catch { }

			stateBase64 = null;
			try
			{
				using var cts = new CancellationTokenSource(5000);
				var (_, state) = await channelTask.WaitAsync(cts.Token);
				stateBase64 = state;
			}
			catch { }

			if (!currentProc.WaitForExit(3000))
				try { currentProc.Kill(entireProcessTree: false); } catch { }
			await currentProc.WaitForExitAsync();

			currentResponse = newResponse;
		}
	}

	// ── Elevated execution via Elevator ─────────────────────────────────

	private static async Task<int> RunElevatedAsync(RunResponse response, string[] scriptArgs, bool isConsoleEphemeral, string daemonAddress)
	{
		var pipeName = $"seashell-{Guid.NewGuid():N}";

		var envVars = new List<string>
		{
			$"SEASHELL_PIPE={pipeName}",
			$"SEASHELL_CLI_PID={Environment.ProcessId}",
		};

		var spawnReq = new SpawnRequest(
			response.AssemblyPath!,
			response.DepsJsonPath,
			response.RuntimeConfigPath,
			scriptArgs,
			Environment.CurrentDirectory,
			envVars.ToArray(),
			Environment.ProcessId);

		var spawnResult = await DaemonManager.RequestElevatedSpawnAsync(daemonAddress, spawnReq);

		// Elevator not connected — try starting it via Task Scheduler,
		// then let the daemon wait for it to connect
		if (!spawnResult.Success && ScheduledTasks.TryRunElevatorTask())
		{
			Console.Error.WriteLine("sea: starting elevator...");
			var retryReq = spawnReq with { AwaitElevatorMs = 15_000 };
			spawnResult = await DaemonManager.RequestElevatedSpawnAsync(daemonAddress, retryReq);
		}

		if (!spawnResult.Success)
		{
			Console.Error.WriteLine("sea: script requires elevation (//sea_elevate)");
			Console.Error.WriteLine($"     elevator: {spawnResult.Error}");
			Console.Error.WriteLine("     fallback: gsudo sea script.cs");
			return 1;
		}

		// Ctrl+C propagates via shared console (script called AttachConsole)
		Console.CancelKeyPress += (_, e) => { e.Cancel = true; };

		try
		{
			var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
			pipe.Connect(10_000);

			await using var channel = new MessageChannel(pipe);
			await channel.SendAsync(BuildScriptInit(response, scriptArgs, isConsoleEphemeral, launcherPid: Environment.ProcessId));

			return await ReceiveUntilExitAsync(channel);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"sea: elevated script communication failed: {ex.Message}");
			return 1;
		}
	}

	// ── Watch mode ──────────────────────────────────────────────────────

	public static async Task<int> RunWatchAsync(TransportStream daemonConn, RunResponse initial, string[] scriptArgs)
	{
		var reloadCount = 0;
		var currentResponse = initial;
		Process? currentProc = null;
		MessageChannel? scriptChannel = null;
		string? stateBase64 = null;

		var exitRequested = false;
		Console.CancelKeyPress += (_, e) =>
		{
			e.Cancel = true;
			exitRequested = true;
			// Best-effort: signal script to stop, then kill
			try { scriptChannel?.SendAsync(new ScriptStop()).AsTask().Wait(1000); } catch { }
			try { currentProc?.Kill(entireProcessTree: false); } catch { }
		};

		while (!exitRequested)
		{
			var pipeName = $"seashell-{Guid.NewGuid():N}";

			var psi = BuildProcessStartInfo(currentResponse, scriptArgs);
			psi.Environment["SEASHELL_PIPE"] = pipeName;

			currentProc = Process.Start(psi)!;

			// Connect to script's pipe server
			var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
			pipe.Connect(5000);
			scriptChannel = new MessageChannel(pipe);

			await scriptChannel.SendAsync(BuildScriptInit(currentResponse, scriptArgs,
				isConsoleEphemeral: false, reloadCount: reloadCount,
				stateBase64: stateBase64, watch: true));

			// Channel reader with reload request signaling
			var reloadRequests = Channel.CreateUnbounded<(string? reason, bool clearCache)>();
			var channelTask = ReceiveUntilExitOrStateAsync(scriptChannel, reloadRequests.Writer);
			HotSwapNotify? swapNotify = null;

			// hotSwapTask must be created ONCE and reused — PipeReader doesn't
			// support concurrent reads. Only recreate after it completes.
			var hotSwapTask = daemonConn.Channel.ReceiveAsync().AsTask();

			// Inner event loop for this process instance
			while (!exitRequested)
			{
				var reloadTask = reloadRequests.Reader.ReadAsync().AsTask();

				var completed = await Task.WhenAny(channelTask, hotSwapTask, reloadTask);

				if (completed == channelTask)
				{
					// Script exited on its own
					var (exitCode, _) = await channelTask;
					await currentProc.WaitForExitAsync();
					await scriptChannel.DisposeAsync();
					if (exitRequested) break;
					return currentProc.ExitCode;
				}

				if (completed == reloadTask)
				{
					// Script requested reload — ask daemon to recompile
					var (reason, clearCache) = await reloadTask;
					Console.Error.WriteLine($"sea: reload requested ({reason ?? "script"})...");
					try { await daemonConn.Channel.SendAsync(new RecompileRequest(clearCache)); }
					catch
					{
						Console.Error.WriteLine("sea: daemon disconnected");
						try { currentProc.Kill(entireProcessTree: false); } catch { }
						return 1;
					}
					continue; // wait for HotSwapNotify from daemon
				}

				// completed == hotSwapTask (file change or recompile response from daemon)
				var swapResult = await hotSwapTask;
				if (swapResult == null)
				{
					Console.Error.WriteLine("sea: daemon disconnected during watch");
					try { currentProc.Kill(entireProcessTree: false); } catch { }
					return 1;
				}

				if (swapResult.Value.Type != MessageType.HotSwapNotify)
				{
					Console.Error.WriteLine($"sea: unexpected message during watch: {swapResult.Value.Type}");
					continue;
				}

				var notify = (HotSwapNotify)swapResult.Value.Message;

				// Compile failure? Skip swap, keep running.
				if (string.IsNullOrEmpty(notify.AssemblyPath))
				{
					Console.Error.WriteLine("sea: recompile failed, continuing...");
					continue;
				}

				swapNotify = notify;
				break; // exit inner loop to do the swap
			}

			if (swapNotify == null) break; // exitRequested or error

			reloadCount++;
			Console.Error.WriteLine($"sea: reloading ({swapNotify.Reason})...");

			// Signal old script to reload, collect state, kill
			try { await scriptChannel.SendAsync(new ScriptReload()); } catch { }

			stateBase64 = null;
			try
			{
				using var cts = new CancellationTokenSource(5000);
				var (_, state) = await channelTask.WaitAsync(cts.Token);
				stateBase64 = state;
			}
			catch { }

			if (!currentProc.WaitForExit(3000))
				try { currentProc.Kill(entireProcessTree: false); } catch { }
			await currentProc.WaitForExitAsync();

			await scriptChannel.DisposeAsync();

			currentResponse = new RunResponse(
				true, false, true,
				swapNotify.AssemblyPath,
				swapNotify.DepsJsonPath,
				swapNotify.RuntimeConfigPath,
				swapNotify.ManifestPath,
				0, null,
				swapNotify.StartupHookPath,
				swapNotify.DirectExe);
		}

		return 0;
	}

	// ── Helpers ─────────────────────────────────────────────────────────

	private static ProcessStartInfo BuildProcessStartInfo(RunResponse response, string[] scriptArgs)
	{
		ProcessStartInfo psi;

		if (response.DirectExe)
		{
			// Tier 2/3 .exe — run directly, not via dotnet exec
			psi = new ProcessStartInfo
			{
				FileName = response.AssemblyPath!,
				WorkingDirectory = Environment.CurrentDirectory,
			};
		}
		else
		{
			psi = new ProcessStartInfo
			{
				FileName = "dotnet",
				WorkingDirectory = Environment.CurrentDirectory,
			};
			psi.ArgumentList.Add("exec");
			if (response.RuntimeConfigPath != null)
			{
				psi.ArgumentList.Add("--runtimeconfig");
				psi.ArgumentList.Add(response.RuntimeConfigPath);
			}
			if (response.DepsJsonPath != null)
			{
				psi.ArgumentList.Add("--depsfile");
				psi.ArgumentList.Add(response.DepsJsonPath);
			}
			psi.ArgumentList.Add(response.AssemblyPath!);
		}

		// Startup hook for binaries that don't reference SeaShell.Script
		if (response.StartupHookPath != null)
			psi.Environment["DOTNET_STARTUP_HOOKS"] = response.StartupHookPath;

		foreach (var a in scriptArgs)
			psi.ArgumentList.Add(a);
		return psi;
	}

	private static ScriptInit BuildScriptInit(RunResponse response, string[] scriptArgs,
		bool isConsoleEphemeral, int launcherPid = 0, int reloadCount = 0,
		string? stateBase64 = null, bool watch = false)
	{
		// Read manifest data from the compiler's manifest file
		string? scriptPath = null;
		string[]? sources = null;
		Dictionary<string, string>? packages = null;
		string[]? assemblies = null;

		if (response.ManifestPath != null && File.Exists(response.ManifestPath))
		{
			try
			{
				var json = File.ReadAllText(response.ManifestPath);
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
			catch { }
		}

		return new ScriptInit(
			scriptPath,
			Environment.CurrentDirectory,
			scriptArgs,
			sources,
			packages,
			assemblies,
			isConsoleEphemeral,
			launcherPid,
			reloadCount,
			stateBase64,
			watch);
	}

	/// <summary>Receive messages until ScriptExit. Returns exit code.</summary>
	private static async Task<int> ReceiveUntilExitAsync(MessageChannel channel)
	{
		while (true)
		{
			var msg = await channel.ReceiveAsync();
			if (msg == null) return 1; // disconnected

			if (msg.Value.Type == MessageType.ScriptExit)
			{
				var exit = (ScriptExit)msg.Value.Message;
				LastExitDelay = exit.ExitDelay;
				return exit.ExitCode;
			}
		}
	}

	/// <summary>
	/// Receive messages until ScriptExit. Returns (exitCode, state).
	/// If reloadWriter is provided, signals it on ScriptReloadRequest (continues reading).
	/// </summary>
	private static async Task<(int exitCode, string? state)> ReceiveUntilExitOrStateAsync(
		MessageChannel channel, ChannelWriter<(string?, bool)>? reloadWriter = null)
	{
		string? state = null;
		while (true)
		{
			var msg = await channel.ReceiveAsync();
			if (msg == null) return (1, state);

			switch (msg.Value.Type)
			{
				case MessageType.ScriptExit:
					var exit = (ScriptExit)msg.Value.Message;
					LastExitDelay = exit.ExitDelay;
					return (exit.ExitCode, state);

				case MessageType.ScriptState:
					var s = (ScriptState)msg.Value.Message;
					if (!string.IsNullOrEmpty(s.Data))
						state = s.Data;
					break;

				case MessageType.ScriptReloadRequest:
					var req = (ScriptReloadRequest)msg.Value.Message;
					reloadWriter?.TryWrite((req.Reason, req.ClearCache));
					break;
			}
		}
	}

	// ── Daemon lifecycle ────────────────────────────────────────────────

	/// <summary>
	/// Ensure a daemon is running and matches the current build.
	/// Stages if needed, version-checks, restarts on mismatch.
	/// </summary>
	private static async Task EnsureDaemonAsync(string daemonAddress)
	{
		if (await TransportClient.ProbeAsync(daemonAddress))
		{
			// Daemon is running — check if it matches our staged hash
			var (sourceDir, _) = DaemonManager.FindDaemonSourcePublic();
			if (sourceDir != null)
			{
				var (_, hash) = DaemonManager.StageBinary(sourceDir, "daemon");
				var stopped = await DaemonManager.EnsureDaemonMatchesAsync(daemonAddress, hash);
				if (!stopped)
					return; // same version, already running
			}
		}

		// Not running (or was stopped for version mismatch) — start
		DaemonManager.StartDaemon();
		for (int i = 0; i < 20; i++)
		{
			await Task.Delay(250);
			if (await TransportClient.ProbeAsync(daemonAddress))
				return;
		}
	}

	/// <summary>Manifest data shape for deserializing .sea.json files.</summary>
	private sealed class ManifestData
	{
		public string? ScriptPath { get; set; }
		public string[]? Sources { get; set; }
		public Dictionary<string, string>? Packages { get; set; }
		public string[]? Assemblies { get; set; }
	}
}
