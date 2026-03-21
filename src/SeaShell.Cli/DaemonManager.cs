using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SeaShell.Ipc;
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

	/// <summary>Start the daemon process. Prefers Task Scheduler if registered, else Process.Start.</summary>
	public static int StartDaemon()
	{
		// Prefer Task Scheduler if the daemon is registered as a task
		if (ScheduledTasks.TryRunDaemonTask())
		{
			Console.Error.WriteLine("sea: daemon starting (task)");
			return 0;
		}

		var cliDir = AppContext.BaseDirectory;

		// Priority 1: dev mode — dotnet run --project
		var daemonProj = Path.GetFullPath(
			Path.Combine(cliDir, "..", "..", "..", "..", "SeaShell.Daemon", "SeaShell.Daemon.csproj"));

		// Priority 2: published mode — native apphost next to CLI
		var daemonExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
			? "seashell-daemon.exe" : "seashell-daemon";
		var daemonExePath = Path.Combine(cliDir, daemonExe);

		// Priority 3: dotnet tool mode — DLL next to CLI, launch via dotnet exec
		var daemonDll = Path.Combine(cliDir, "seashell-daemon.dll");

		ProcessStartInfo psi;
		if (File.Exists(daemonProj))
		{
			psi = new ProcessStartInfo
			{
				FileName = "dotnet",
				ArgumentList = { "run", "--project", daemonProj },
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
		}
		else if (File.Exists(daemonExePath))
		{
			psi = new ProcessStartInfo
			{
				FileName = daemonExePath,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
		}
		else if (File.Exists(daemonDll))
		{
			// dotnet tool: no apphost, launch DLL via dotnet exec
			psi = new ProcessStartInfo
			{
				FileName = "dotnet",
				ArgumentList = { "exec", daemonDll },
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
		}
		else
		{
			Console.Error.WriteLine("sea: daemon not found");
			return 1;
		}

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
