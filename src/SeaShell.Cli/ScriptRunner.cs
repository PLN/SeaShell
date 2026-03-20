using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
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
		// Ensure daemon is running
		if (!await TransportClient.ProbeAsync(daemonAddress))
		{
			DaemonManager.StartDaemon();
			for (int i = 0; i < 20; i++)
			{
				await Task.Delay(250);
				if (await TransportClient.ProbeAsync(daemonAddress))
					break;
			}
			if (!await TransportClient.ProbeAsync(daemonAddress))
			{
				Console.Error.WriteLine("sea: daemon failed to start");
				return 1;
			}
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
		await conn.SendAsync(Envelope.Wrap(request).ToBytes());
		var replyBytes = await conn.ReceiveAsync();
		if (replyBytes == null)
		{
			Console.Error.WriteLine("sea: daemon disconnected");
			return 1;
		}

		var response = Envelope.FromBytes(replyBytes).Unwrap<RunResponse>();
		if (!response.Success)
		{
			Console.Error.WriteLine(response.Error);
			return 1;
		}

		if (response.Elevated && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			if (DaemonManager.IsCurrentProcessElevated())
				return await RunDirectAsync(response, scriptArgs, isConsoleEphemeral);

			return await RunElevatedAsync(response, scriptArgs, isConsoleEphemeral, daemonAddress);
		}
		else if (response.Watch)
		{
			return await RunWatchAsync(conn, response, scriptArgs);
		}
		else
		{
			return await RunDirectAsync(response, scriptArgs, isConsoleEphemeral);
		}
	}

	// ── Direct execution (non-elevated, or already-elevated CLI) ────────

	public static async Task<int> RunDirectAsync(RunResponse response, string[] scriptArgs, bool isConsoleEphemeral = false)
	{
		var pipeName = $"seashell-{Guid.NewGuid():N}";

		var psi = BuildProcessStartInfo(response, scriptArgs);
		psi.Environment["SEASHELL_PIPE"] = pipeName;

		using var proc = Process.Start(psi)!;

		Console.CancelKeyPress += (_, e) =>
		{
			e.Cancel = true;
			try { proc.Kill(entireProcessTree: false); } catch { }
		};

		// Connect to script's pipe server (created by Sea.Initialize).
		// Connect() uses WaitNamedPipe internally — polls until server appears.
		var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		try
		{
			pipe.Connect(5000);
		}
		catch
		{
			Console.Error.WriteLine("sea: script pipe connect failed");
			await proc.WaitForExitAsync();
			return proc.ExitCode;
		}

		await using var channel = new MessageChannel(pipe);
		await channel.SendAsync(BuildScriptInit(response, scriptArgs, isConsoleEphemeral));

		// Read from pipe concurrently — the script's ProcessExit sends ScriptExit,
		// and PipeWriter.Flush blocks until we read. Must read before awaiting proc exit.
		var exitCode = await ReceiveUntilExitAsync(channel);
		await proc.WaitForExitAsync();
		return exitCode;
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

			// Single channel reader task — handles ScriptExit and ScriptState.
			// This is the ONLY reader on the PipeReader to avoid concurrent read corruption.
			var channelTask = ReceiveUntilExitOrStateAsync(scriptChannel);
			var hotSwapTask = daemonConn.ReceiveAsync();

			var completed = await Task.WhenAny(channelTask, hotSwapTask);

			if (completed == channelTask)
			{
				// Script exited on its own
				var (exitCode, _) = await channelTask;
				await currentProc.WaitForExitAsync();
				await scriptChannel.DisposeAsync();

				if (exitRequested) break;
				return currentProc.ExitCode;
			}

			// Hot-swap: daemon sent new artifacts
			var swapBytes = await hotSwapTask;
			if (swapBytes == null)
			{
				Console.Error.WriteLine("sea: daemon disconnected during watch");
				try { currentProc.Kill(entireProcessTree: false); } catch { }
				return 1;
			}

			var envelope = Envelope.FromBytes(swapBytes);
			if (envelope.Type != nameof(HotSwapNotify))
			{
				Console.Error.WriteLine($"sea: unexpected message during watch: {envelope.Type}");
				continue;
			}

			var notify = envelope.Unwrap<HotSwapNotify>();
			reloadCount++;
			Console.Error.WriteLine($"sea: reloading ({notify.Reason})...");

			// Send ScriptReload to the script. The script fires Reloading,
			// sends ScriptState, then exits (sending ScriptExit).
			// channelTask (the ONLY reader) reads both messages and returns.
			try { await scriptChannel.SendAsync(new ScriptReload()); } catch { }

			// Await the channel reader — it picks up ScriptState + ScriptExit
			stateBase64 = null;
			try
			{
				using var cts = new CancellationTokenSource(5000);
				var (_, state) = await channelTask.WaitAsync(cts.Token);
				stateBase64 = state;
			}
			catch { }

			// Ensure process is dead
			if (!currentProc.WaitForExit(3000))
			{
				try { currentProc.Kill(entireProcessTree: false); } catch { }
			}
			await currentProc.WaitForExitAsync();

			await scriptChannel.DisposeAsync();

			currentResponse = new RunResponse(
				true, false, true,
				notify.AssemblyPath,
				notify.DepsJsonPath,
				notify.RuntimeConfigPath,
				notify.ManifestPath,
				0, null);
		}

		return 0;
	}

	// ── Helpers ─────────────────────────────────────────────────────────

	private static ProcessStartInfo BuildProcessStartInfo(RunResponse response, string[] scriptArgs)
	{
		var psi = new ProcessStartInfo
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

			if (msg.Type == nameof(ScriptExit))
			{
				var exit = msg.Unwrap<ScriptExit>();
				LastExitDelay = exit.ExitDelay;
				return exit.ExitCode;
			}
		}
	}

	/// <summary>Receive messages until ScriptExit. Returns (exitCode, state).</summary>
	private static async Task<(int exitCode, string? state)> ReceiveUntilExitOrStateAsync(MessageChannel channel)
	{
		string? state = null;
		while (true)
		{
			var msg = await channel.ReceiveAsync();
			if (msg == null) return (1, state);

			if (msg.Type == nameof(ScriptExit))
			{
				var exit = msg.Unwrap<ScriptExit>();
				LastExitDelay = exit.ExitDelay;
				return (exit.ExitCode, state);
			}
			if (msg.Type == nameof(ScriptState))
			{
				var s = msg.Unwrap<ScriptState>();
				if (!string.IsNullOrEmpty(s.Data))
					state = s.Data;
			}
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
