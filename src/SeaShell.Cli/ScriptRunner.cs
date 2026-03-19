using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Protocol;
using SeaShell.Cli;

namespace SeaShell.Cli;

static class ScriptRunner
{
	/// <summary>
	/// Stores the manifest path from the last RunDirect call, so ExitDelay
	/// can read it even when the environment variable wasn't set.
	/// </summary>
	internal static string? LastManifestPath;

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

		// Send run request — the Daemon handles everything (including elevation)
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
			{
				// Already elevated (e.g., gsudo sea file.cs) — just spawn directly
				return await RunDirectAsync(response, scriptArgs, isConsoleEphemeral);
			}

			// Try the Elevator worker
			var spawnReq = new SpawnRequest(
				response.AssemblyPath!,
				response.DepsJsonPath,
				response.RuntimeConfigPath,
				scriptArgs,
				Environment.CurrentDirectory,
				Array.Empty<string>());

			var spawnResult = await DaemonManager.RequestElevatedSpawnAsync(daemonAddress, spawnReq);
			if (spawnResult.Success)
			{
				try
				{
					using var proc = Process.GetProcessById(spawnResult.ProcessId);
					Console.CancelKeyPress += (_, e) =>
					{
						e.Cancel = true;
						try { proc.Kill(entireProcessTree: false); } catch { }
					};
					await proc.WaitForExitAsync();
					return proc.ExitCode;
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"sea: could not track elevated process: {ex.Message}");
					return 1;
				}
			}

			// Elevator not available — suggest alternatives
			Console.Error.WriteLine("sea: script requires elevation (//sea_elevate)");
			Console.Error.WriteLine($"     elevator: {spawnResult.Error}");
			Console.Error.WriteLine("     fallback: gsudo sea script.cs");
			return 1;
		}
		else if (response.Watch)
		{
			// Watch mode: run, watch for changes, hot-swap
			return await RunWatchAsync(conn, response, scriptArgs);
		}
		else
		{
			// Normal: CLI spawns the process directly
			return await RunDirectAsync(response, scriptArgs, isConsoleEphemeral);
		}
	}

	/// <summary>Spawn a compiled script directly via <c>dotnet exec</c>.</summary>
	public static async Task<int> RunDirectAsync(RunResponse response, string[] scriptArgs, bool isConsoleEphemeral = false)
	{
		var psi = new ProcessStartInfo
		{
			FileName = "dotnet",
			WorkingDirectory = Environment.CurrentDirectory,
		};

		// Pass context to the script process
		if (response.ManifestPath != null)
		{
			psi.Environment["SEASHELL_MANIFEST"] = response.ManifestPath;
			LastManifestPath = response.ManifestPath;
		}
		if (isConsoleEphemeral)
			psi.Environment["SEASHELL_CONSOLE_EPHEMERAL"] = "1";
		if (scriptArgs.Length > 0)
			psi.Environment["SEASHELL_ARGS"] = string.Join("\n", scriptArgs);
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

		using var proc = Process.Start(psi)!;

		Console.CancelKeyPress += (_, e) =>
		{
			e.Cancel = true;
			try { proc.Kill(entireProcessTree: false); } catch { }
		};

		await proc.WaitForExitAsync();
		return proc.ExitCode;
	}

	/// <summary>Watch mode: run, watch for source changes, hot-swap on daemon notification.</summary>
	public static async Task<int> RunWatchAsync(TransportStream daemonConn, RunResponse initial, string[] scriptArgs)
	{
		var reloadCount = 0;
		var currentResponse = initial;
		Process? currentProc = null;
		NamedPipeServerStream? controlPipe = null;
		StreamWriter? controlWriter = null;
		string? statePath = null;

		// Ctrl+C stops the current script and exits watch mode
		var exitRequested = false;
		Console.CancelKeyPress += (_, e) =>
		{
			e.Cancel = true;
			exitRequested = true;
			try { controlWriter?.WriteLine("STOP"); controlWriter?.Flush(); } catch { }
			Thread.Sleep(500);
			try { currentProc?.Kill(entireProcessTree: false); } catch { }
		};

		while (!exitRequested)
		{
			// ── Spawn with control pipe ─────────────────────────────────
			var pipeName = $"seashell-ctrl-{Environment.ProcessId}-{reloadCount}";

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				// Restrict control pipe to current user — prevents other local users
				// from sending RELOAD/STOP commands to hijack running scripts.
				var security = new PipeSecurity();
				security.AddAccessRule(new PipeAccessRule(
					WindowsIdentity.GetCurrent().User!,
					PipeAccessRights.FullControl,
					AccessControlType.Allow));
				controlPipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
					PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
			}
			else
			{
				controlPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
					PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
			}

			var psi = new ProcessStartInfo
			{
				FileName = "dotnet",
				WorkingDirectory = Environment.CurrentDirectory,
			};
			psi.Environment["SEASHELL_MANIFEST"] = currentResponse.ManifestPath ?? "";
			psi.Environment["SEASHELL_CONTROL"] = pipeName;
			psi.Environment["SEASHELL_RELOAD_COUNT"] = reloadCount.ToString();
			if (statePath != null && File.Exists(statePath))
				psi.Environment["SEASHELL_STATE"] = statePath;

			psi.ArgumentList.Add("exec");
			if (currentResponse.RuntimeConfigPath != null)
			{
				psi.ArgumentList.Add("--runtimeconfig");
				psi.ArgumentList.Add(currentResponse.RuntimeConfigPath);
			}
			if (currentResponse.DepsJsonPath != null)
			{
				psi.ArgumentList.Add("--depsfile");
				psi.ArgumentList.Add(currentResponse.DepsJsonPath);
			}
			psi.ArgumentList.Add(currentResponse.AssemblyPath!);
			foreach (var a in scriptArgs)
				psi.ArgumentList.Add(a);

			currentProc = Process.Start(psi)!;

			// Wait for script to connect to control pipe (with timeout)
			var connectTask = controlPipe.WaitForConnectionAsync();
			var timeoutTask = Task.Delay(5000);
			if (await Task.WhenAny(connectTask, timeoutTask) == connectTask)
			{
				controlWriter = new StreamWriter(controlPipe, Encoding.UTF8) { AutoFlush = true };
			}
			else
			{
				controlWriter = null; // script didn't connect — no control pipe
			}

			// ── Wait for either: process exits OR daemon sends HotSwapNotify ──
			var procExitTask = currentProc.WaitForExitAsync();
			var hotSwapTask = daemonConn.ReceiveAsync();

			var completed = await Task.WhenAny(procExitTask, hotSwapTask);

			if (completed == procExitTask)
			{
				// Script exited on its own — check if there's a pending hot-swap
				controlWriter?.Dispose();
				controlPipe.Dispose();

				if (exitRequested) break;

				// Script just ended — exit watch mode with its exit code
				return currentProc.ExitCode;
			}

			// ── Hot-swap: daemon sent new artifacts ─────────────────────
			var swapBytes = await hotSwapTask;
			if (swapBytes == null)
			{
				// Daemon disconnected
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

			// Signal old process to shut down gracefully
			try { controlWriter?.WriteLine("RELOAD"); controlWriter?.Flush(); } catch { }

			// Read state blob back from the script (if any)
			statePath = null;
			try
			{
				if (controlPipe.IsConnected)
				{
					using var cts = new CancellationTokenSource(3000);
					var reader = new BinaryReader(controlPipe, Encoding.UTF8, leaveOpen: true);
					var stateLen = reader.ReadInt32();
					if (stateLen > 0 && stateLen <= 8192)
					{
						var stateBytes = reader.ReadBytes(stateLen);
						statePath = Path.Combine(Path.GetTempPath(), "seashell", $"state-{Environment.ProcessId}.bin");
						Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
						File.WriteAllBytes(statePath, stateBytes);
					}
				}
			}
			catch { }

			// Give it a grace period, then kill
			var graceful = Task.WhenAny(procExitTask, Task.Delay(3000));
			await graceful;
			if (!currentProc.HasExited)
			{
				try { currentProc.Kill(entireProcessTree: false); } catch { }
				await procExitTask;
			}

			controlWriter?.Dispose();
			controlPipe.Dispose();

			// Update response with new artifacts
			currentResponse = new RunResponse(
				true, false, true,
				notify.AssemblyPath,
				notify.DepsJsonPath,
				notify.RuntimeConfigPath,
				notify.ManifestPath,
				0, null);

			// Loop back to spawn new process
		}

		return 0;
	}
}
