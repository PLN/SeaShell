using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SeaShell.Ipc;
using SeaShell.Engine;
using SeaShell.Protocol;

namespace SeaShell.Cli;

static class DaemonManager
{
	/// <summary>Show daemon and elevator status via a PingRequest.</summary>
	public static async Task<int> StatusAsync(string daemonAddress)
	{
		try
		{
			await using var conn = await TransportClient.ConnectAsync(daemonAddress, timeoutMs: 2000);
			await conn.Channel.SendAsync(new PingRequest());
			var reply = await conn.Channel.ReceiveAsync();
			if (reply == null) { Console.WriteLine("  daemon: not responding"); return 1; }

			var ping = (PingResponse)reply.Value.Message;
			Console.WriteLine($"  daemon:   v{ping.Version}, uptime {ping.UptimeSeconds}s, {ping.ActiveScripts} active");
			Console.WriteLine($"  elevator: {(ping.ElevatorConnected ? "connected" : "not connected")}");
			return 0;
		}
		catch
		{
			Console.WriteLine("  daemon:   not running");
			Console.WriteLine("  elevator: unknown (daemon not running)");
			return 1;
		}
	}

	/// <summary>Send a StopRequest to the daemon.</summary>
	public static async Task<int> DaemonStopAsync(string address)
	{
		try
		{
			await using var conn = await TransportClient.ConnectAsync(address, timeoutMs: 2000);
			await conn.Channel.SendAsync(new StopRequest());
			await conn.Channel.ReceiveAsync();
			Console.WriteLine("daemon: stop requested");
			return 0;
		}
		catch
		{
			Console.WriteLine("daemon: not running");
			return 1;
		}
	}

	/// <summary>Start the daemon process. Stages binary to data dir to avoid DLL locks.</summary>
	public static int StartDaemon()
	{
		// Prefer Task Scheduler if the daemon is registered as a task
		if (ScheduledTasks.TryRunDaemonTask())
		{
			Console.Error.WriteLine("sea: daemon starting (task)");
			return 0;
		}

		// Find daemon source directory
		var (sourceDir, mode) = FindDaemonSource();
		if (sourceDir == null)
		{
			Console.Error.WriteLine("sea: daemon not found");
			return 1;
		}

		// Dev mode: build first (dotnet run would have built, but we need the output dir)
		if (mode == LaunchMode.Dev)
		{
			// sourceDir = .../SeaShell.Daemon/bin/Debug/net10.0 → walk up 3 to project dir
			var daemonProj = Path.GetFullPath(
				Path.Combine(sourceDir, "..", "..", "..", "SeaShell.Daemon.csproj"));
			var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{daemonProj}\" -v q")
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			});
			build?.WaitForExit();
		}

		// Stage to data dir
		var (stagedDir, hash) = StageBinary(sourceDir, "daemon");
		var stagedDll = Path.Combine(stagedDir, "seashell-daemon.dll");
		var stagedExe = Path.Combine(stagedDir,
			RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "seashell-daemon.exe" : "seashell-daemon");

		// NuGet cache path for --additionalprobingpath (resolves package DLLs)
		var nugetCache = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

		// Build ProcessStartInfo from staged copy
		ProcessStartInfo psi;
		if (File.Exists(stagedExe) && mode == LaunchMode.Published)
		{
			psi = new ProcessStartInfo
			{
				FileName = stagedExe,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
		}
		else if (File.Exists(stagedDll))
		{
			var depsJson = Path.Combine(stagedDir, "seashell-daemon.deps.json");
			psi = new ProcessStartInfo
			{
				FileName = "dotnet",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			psi.ArgumentList.Add("exec");
			if (File.Exists(depsJson))
			{
				psi.ArgumentList.Add("--depsfile");
				psi.ArgumentList.Add(depsJson);
			}
			psi.ArgumentList.Add("--additionalprobingpath");
			psi.ArgumentList.Add(nugetCache);
			psi.ArgumentList.Add(stagedDll);
		}
		else
		{
			Console.Error.WriteLine($"sea: staged daemon not found in {stagedDir}");
			return 1;
		}

		// (Daemon computes its own hash from AppContext.BaseDirectory)

		try
		{
			var proc = Process.Start(psi);
			if (proc == null || proc.HasExited)
			{
				Console.Error.WriteLine("sea: failed to start daemon");
				return 1;
			}
			Console.Error.WriteLine($"sea: daemon started (pid {proc.Id})");
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"sea: failed to start daemon: {ex.Message}");
			return 1;
		}
	}

	// ── Daemon source discovery ────────────────────────────────────────

	private enum LaunchMode { Dev, Published, Tool }

	/// <summary>Find the daemon source directory. Returns (sourceDir, null) or (null, null) if not found.</summary>
	internal static (string? sourceDir, string? mode) FindDaemonSourcePublic()
	{
		var (dir, m) = FindDaemonSource();
		return (dir, dir != null ? m.ToString() : null);
	}

	private static (string? sourceDir, LaunchMode mode) FindDaemonSource()
	{
		var cliDir = AppContext.BaseDirectory;

		// Priority 1: dev mode — sibling project build output
		var daemonProj = Path.GetFullPath(
			Path.Combine(cliDir, "..", "..", "..", "..", "SeaShell.Daemon", "SeaShell.Daemon.csproj"));
		if (File.Exists(daemonProj))
		{
			var buildOutput = Path.GetFullPath(
				Path.Combine(cliDir, "..", "..", "..", "..", "SeaShell.Daemon", "bin", "Debug", "net10.0"));
			if (Directory.Exists(buildOutput))
				return (buildOutput, LaunchMode.Dev);
		}

		// Priority 2: published mode — native apphost next to CLI
		var daemonExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
			? "seashell-daemon.exe" : "seashell-daemon";
		if (File.Exists(Path.Combine(cliDir, daemonExe)))
			return (cliDir, LaunchMode.Published);

		// Priority 3: dotnet tool mode — DLL next to CLI
		if (File.Exists(Path.Combine(cliDir, "seashell-daemon.dll")))
			return (cliDir, LaunchMode.Tool);

		return (null, LaunchMode.Dev);
	}

	// ── Binary staging ─────────────────────────────────────────────────

	/// <summary>
	/// Copy a daemon or elevator binary and its dependencies to a stable data directory.
	/// Returns (stagedDir, hash). Reuses existing staging if hash matches.
	/// </summary>
	internal static (string stagedDir, string hash) StageBinary(string sourceDir, string binaryName)
	{
		var hash = ComputeDirHash(sourceDir);
		var stageDir = Path.Combine(SeaShellPaths.DataDir, binaryName, hash);

		if (Directory.Exists(stageDir))
			return (stageDir, hash);

		Directory.CreateDirectory(stageDir);
		foreach (var file in Directory.GetFiles(sourceDir))
		{
			var dest = Path.Combine(stageDir, Path.GetFileName(file));
			try { File.Copy(file, dest); } catch { }
		}

		// Generate .runtimeconfig.dev.json with NuGet probing path so the staged
		// binary can resolve package dependencies (EventLog, Serilog sinks, etc.)
		var nugetCache = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
		foreach (var rc in Directory.GetFiles(stageDir, "*.runtimeconfig.json"))
		{
			var devJson = Path.ChangeExtension(rc, null); // strip .json
			devJson = devJson + ".dev.json"; // name.runtimeconfig.dev.json
			if (!File.Exists(devJson))
			{
				var content = $$"""
				{
				  "runtimeOptions": {
				    "additionalProbingPaths": [
				      "{{nugetCache.Replace("\\", "\\\\")}}"
				    ]
				  }
				}
				""";
				File.WriteAllText(devJson, content);
			}
		}

		return (stageDir, hash);
	}

	private static string ComputeDirHash(string dir)
	{
		var ticks = 0L;
		try
		{
			foreach (var f in Directory.GetFiles(dir, "*.dll").OrderBy(f => f))
				ticks += File.GetLastWriteTimeUtc(f).Ticks;
		}
		catch { }
		return ticks.ToString("x");
	}

	// ── Version-check running daemon ───────────────────────────────────

	/// <summary>
	/// Check if a running daemon matches the expected hash. If not, stop it.
	/// Returns true if the daemon was stopped (or wasn't running).
	/// </summary>
	internal static async Task<bool> EnsureDaemonMatchesAsync(string daemonAddress, string expectedHash)
	{
		try
		{
			await using var conn = await TransportClient.ConnectAsync(daemonAddress, timeoutMs: 2000);
			await conn.Channel.SendAsync(new PingRequest());
			var reply = await conn.Channel.ReceiveAsync();
			if (reply == null) return true; // not responding

			var ping = (PingResponse)reply.Value.Message;
			if (ping.DaemonHash == expectedHash)
				return false; // same version, don't restart

			// Different version — stop the old daemon
			Console.Error.WriteLine($"sea: daemon version mismatch (running={ping.DaemonHash?[..8] ?? "?"}, staged={expectedHash[..Math.Min(8, expectedHash.Length)]}), restarting...");

			try
			{
				await using var stopConn = await TransportClient.ConnectAsync(daemonAddress, timeoutMs: 2000);
				await stopConn.Channel.SendAsync(new StopRequest());
				await stopConn.Channel.ReceiveAsync();
			}
			catch { }

			// Wait for daemon to exit
			for (int i = 0; i < 6; i++)
			{
				await Task.Delay(500);
				if (!await TransportClient.ProbeAsync(daemonAddress))
					return true; // stopped
			}

			// Last resort: kill by PID
			if (ping.Pid > 0)
			{
				try
				{
					var proc = Process.GetProcessById(ping.Pid);
					proc.Kill();
					Console.Error.WriteLine($"sea: killed stale daemon (pid {ping.Pid})");
				}
				catch { }
			}

			return true;
		}
		catch
		{
			return true; // not running
		}
	}

	/// <summary>Ask the daemon to spawn a process via the Elevator.</summary>
	public static async Task<SpawnResponse> RequestElevatedSpawnAsync(string daemonAddress, SpawnRequest request)
	{
		try
		{
			await using var conn = await TransportClient.ConnectAsync(daemonAddress, timeoutMs: 5000);
			await conn.Channel.SendAsync(request);
			var reply = await conn.Channel.ReceiveAsync();
			if (reply == null)
				return new SpawnResponse(false, 0, "Daemon disconnected");
			return (SpawnResponse)reply.Value.Message;
		}
		catch (Exception ex)
		{
			return new SpawnResponse(false, 0, $"Elevator not available: {ex.Message}");
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
