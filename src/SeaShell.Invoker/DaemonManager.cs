using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SeaShell.Ipc;
using SeaShell.Protocol;

namespace SeaShell.Invoker;

/// <summary>
/// Daemon lifecycle management: start, stop, status, binary staging, version checking.
/// Console-free — returns structured results. Accepts log callbacks for progress.
/// </summary>
public static class DaemonManager
{
	/// <summary>True when running on a musl-based Linux (Alpine, Void, etc.).</summary>
	internal static bool IsMuslRuntime { get; } =
		!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
		&& (RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
			|| File.Exists("/etc/alpine-release"));

	internal static readonly string Version =
		typeof(DaemonManager).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";

	// ── Status ─────────────────────────────────────────────────────────

	/// <summary>Ping the daemon and return its status.</summary>
	public static async Task<DaemonStatus?> StatusAsync(string daemonAddress)
	{
		try
		{
			await using var conn = await TransportClient.ConnectAsync(daemonAddress, timeoutMs: 2000);
			await conn.Channel.SendAsync(new PingRequest());
			var reply = await conn.Channel.ReceiveAsync();
			if (reply == null) return null;

			var ping = (PingResponse)reply.Value.Message;
			return new DaemonStatus(
				ping.Version, ping.UptimeSeconds, ping.ActiveScripts,
				ping.ElevatorConnected, ping.Pid, ping.DaemonHash,
				ping.IdleSeconds, ping.IdleTimeoutSeconds, ping.ElevatorVersion);
		}
		catch
		{
			return null;
		}
	}

	// ── Stop ────────────────────────────────────────────────────────────

	/// <summary>Send a StopRequest to the daemon. Returns true if daemon was running and accepted.</summary>
	public static async Task<bool> DaemonStopAsync(string address)
	{
		try
		{
			await using var conn = await TransportClient.ConnectAsync(address, timeoutMs: 2000);
			await conn.Channel.SendAsync(new StopRequest());
			await conn.Channel.ReceiveAsync();
			return true;
		}
		catch
		{
			return false;
		}
	}

	// ── Start ───────────────────────────────────────────────────────────

	/// <summary>Start the daemon process. Stages binary via manifest to avoid DLL locks.</summary>
	public static int StartDaemon(Action<string>? log = null, Action<string>? verboseLog = null)
	{
		// Prefer Task Scheduler if the daemon is registered as a task
		if (ScheduledTasks.TryRunDaemonTask())
		{
			verboseLog?.Invoke("daemon starting (task)");
			return 0;
		}

		// Find daemon via manifest (stages if needed)
		var stagedDir = ServiceManifest.GetOrStageDaemon(Version, verboseLog);
		if (stagedDir == null)
		{
			log?.Invoke("daemon not found");
			return 1;
		}

		var stagedDll = Path.Combine(stagedDir, "seashell-daemon.dll");
		var stagedExe = Path.Combine(stagedDir,
			RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "seashell-daemon.exe" : "seashell-daemon");

		var nugetCache = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

		ProcessStartInfo psi;
		if (File.Exists(stagedExe))
		{
			psi = new ProcessStartInfo
			{
				FileName = stagedExe,
				UseShellExecute = false,
				CreateNoWindow = true,
				// Redirect stdin/stdout/stderr so the daemon gets new pipes instead
				// of inheriting the caller's handles. Streams are closed immediately
				// after Start (see below). This prevents the daemon from holding open
				// the caller's stdout — which would block SSH sessions, pipe readers,
				// and test harnesses from detecting EOF.
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
			};
		}
		else if (File.Exists(stagedDll))
		{
			var depsJson = Path.Combine(stagedDir, "seashell-daemon.deps.json");
			psi = new ProcessStartInfo
			{
				FileName = "dotnet",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
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
			log?.Invoke($"staged daemon not found in {stagedDir}");
			return 1;
		}

		try
		{
			// Two layers of protection against handle inheritance:
			//
			// 1. Redirect+Close (cross-platform): The daemon's stdin/stdout/stderr are
			//    redirected to new pipes which we close immediately. The daemon gets
			//    broken pipes instead of the caller's handles. Works on both Windows
			//    and Linux.
			//
			// 2. StdHandleInheritGuard (Windows-only): Temporarily clears the
			//    HANDLE_FLAG_INHERIT bit on the caller's std handles before CreateProcess.
			//    This prevents the daemon from inheriting ANY of the caller's handles
			//    (not just stdio). Without this, CreateProcess(bInheritHandles=TRUE)
			//    leaks every inheritable handle to the daemon — including pipes from
			//    SSH sessions, test harnesses, and redirected output.
			using var guard = StdHandleInheritGuard.Suppress();
			var proc = Process.Start(psi);
			if (proc == null || proc.HasExited)
			{
				log?.Invoke("failed to start daemon");
				return 1;
			}
			proc.StandardOutput.Close();
			proc.StandardError.Close();
			proc.StandardInput.Close();
			verboseLog?.Invoke($"daemon started (pid {proc.Id})");
			return 0;
		}
		catch (Exception ex)
		{
			log?.Invoke($"failed to start daemon: {ex.Message}");
			return 1;
		}
	}

	// ── Binary staging ─────────────────────────────────────────────────

	/// <summary>
	/// Copy a daemon or elevator binary and its dependencies to a stable data directory.
	/// Returns (stagedDir, hash). Reuses existing staging if hash matches.
	/// </summary>
	public static (string stagedDir, string hash) StageBinary(string sourceDir, string binaryName)
	{
		var hash = ComputeDirHash(sourceDir);
		var stageDir = Path.Combine(SeaShellPaths.DataDir, binaryName, hash);

		if (Directory.Exists(stageDir))
			return (stageDir, hash);

		Directory.CreateDirectory(stageDir);
		CopyDirectory(sourceDir, stageDir);

		// Generate .runtimeconfig.dev.json with NuGet probing path
		var nugetCache = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
		foreach (var rc in Directory.GetFiles(stageDir, "*.runtimeconfig.json"))
		{
			var devJson = Path.ChangeExtension(rc, null);
			devJson = devJson + ".dev.json";
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

	private static void CopyDirectory(string source, string destination)
	{
		foreach (var file in Directory.GetFiles(source))
		{
			var dest = Path.Combine(destination, Path.GetFileName(file));
			try { File.Copy(file, dest); } catch { } // TODO: log when log callback available
		}
		foreach (var dir in Directory.GetDirectories(source))
		{
			var destDir = Path.Combine(destination, Path.GetFileName(dir));
			Directory.CreateDirectory(destDir);
			CopyDirectory(dir, destDir);
		}
	}

	public static string ComputeDirHash(string dir)
	{
		var sb = new System.Text.StringBuilder();
		try
		{
			foreach (var f in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories).OrderBy(f => f))
			{
				var name = System.Reflection.AssemblyName.GetAssemblyName(f);
				sb.Append(name.FullName);
			}
		}
		catch { }

		var bytes = System.Security.Cryptography.SHA256.HashData(
			System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
		return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
	}

	// ── Version-check running daemon ───────────────────────────────────

	/// <summary>
	/// Check if a running daemon matches the expected hash. If not, stop it.
	/// Returns true if the daemon was stopped (or wasn't running).
	/// </summary>
	public static async Task<bool> EnsureDaemonMatchesAsync(
		string daemonAddress, string expectedHash, Action<string>? log = null)
	{
		try
		{
			await using var conn = await TransportClient.ConnectAsync(daemonAddress, timeoutMs: 2000);
			await conn.Channel.SendAsync(new PingRequest());
			var reply = await conn.Channel.ReceiveAsync();
			if (reply == null) return true;

			var ping = (PingResponse)reply.Value.Message;
			if (ping.DaemonHash == expectedHash)
				return false; // same version, don't restart

			log?.Invoke($"daemon version mismatch (running={ping.DaemonHash?[..8] ?? "?"}, staged={expectedHash[..Math.Min(8, expectedHash.Length)]}), restarting...");

			try
			{
				await using var stopConn = await TransportClient.ConnectAsync(daemonAddress, timeoutMs: 2000);
				await stopConn.Channel.SendAsync(new StopRequest());
				await stopConn.Channel.ReceiveAsync();
			}
			catch { }

			for (int i = 0; i < 6; i++)
			{
				await Task.Delay(500);
				if (!await TransportClient.ProbeAsync(daemonAddress))
					return true;
			}

			if (ping.Pid > 0)
			{
				try
				{
					var proc = Process.GetProcessById(ping.Pid);
					proc.Kill();
					log?.Invoke($"killed stale daemon (pid {ping.Pid})");
				}
				catch { }
			}

			return true;
		}
		catch
		{
			return true;
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
}

/// <summary>Structured daemon status returned by <see cref="DaemonManager.StatusAsync"/>.</summary>
public sealed record DaemonStatus(
	string Version, int UptimeSeconds, int ActiveScripts,
	bool ElevatorConnected, int Pid, string? DaemonHash,
	int IdleSeconds = 0, int IdleTimeoutSeconds = 0,
	string? ElevatorVersion = null);

/// <summary>
/// Temporarily clears the HANDLE_FLAG_INHERIT bit on the process's standard handles
/// (stdin, stdout, stderr) so that a child process created via CreateProcess does not
/// inherit them. Restores the original flags on Dispose.
///
/// This prevents long-lived children (daemon) from holding open the caller's stdio
/// handles — which would block SSH sessions, pipe readers, and test harnesses from
/// detecting EOF.
///
/// No-op on Linux (fork+exec handles fd inheritance differently).
/// </summary>
internal sealed class StdHandleInheritGuard : IDisposable
{
	private readonly (IntPtr handle, bool wasInheritable)[]? _saved;

	private StdHandleInheritGuard((IntPtr, bool)[]? saved) => _saved = saved;

	public static StdHandleInheritGuard Suppress()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return new StdHandleInheritGuard(null);

		var handles = new[]
		{
			GetStdHandle(STD_INPUT_HANDLE),
			GetStdHandle(STD_OUTPUT_HANDLE),
			GetStdHandle(STD_ERROR_HANDLE),
		};

		var saved = new (IntPtr handle, bool wasInheritable)[handles.Length];
		for (int i = 0; i < handles.Length; i++)
		{
			var h = handles[i];
			saved[i] = (h, false);
			if (h == IntPtr.Zero || h == INVALID_HANDLE_VALUE) continue;

			if (GetHandleInformation(h, out var flags))
			{
				saved[i] = (h, (flags & HANDLE_FLAG_INHERIT) != 0);
				if ((flags & HANDLE_FLAG_INHERIT) != 0)
					SetHandleInformation(h, HANDLE_FLAG_INHERIT, 0);
			}
		}

		return new StdHandleInheritGuard(saved);
	}

	public void Dispose()
	{
		if (_saved == null) return;
		foreach (var (handle, wasInheritable) in _saved)
		{
			if (wasInheritable && handle != IntPtr.Zero && handle != INVALID_HANDLE_VALUE)
				SetHandleInformation(handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
		}
	}

	private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
	private const uint HANDLE_FLAG_INHERIT = 0x00000001;
	private const int STD_INPUT_HANDLE = -10;
	private const int STD_OUTPUT_HANDLE = -11;
	private const int STD_ERROR_HANDLE = -12;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GetStdHandle(int nStdHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);
}
