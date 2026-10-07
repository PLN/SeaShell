using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SeaShell.Installer;
using SeaShell.Ipc;
using SeaShell.Protocol;

namespace SeaShell.Invoker;

/// <summary>
/// Daemon lifecycle management: start, stop, status.
/// Console-free — returns structured results. Accepts log callbacks for progress.
/// Uses marker files (daemon.path) for binary discovery instead of the old manifest.
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
			var reply = await conn.RequestAsync(new PingRequest(), TimeSpan.FromSeconds(5));
			if (reply == null) return null;

			var ping = (PingResponse)reply.Value.Message;
			return new DaemonStatus(
				ping.Version, ping.UptimeSeconds, ping.ActiveScripts,
				ping.ElevatorConnected, ping.Pid,
				ping.IdleSeconds, ping.IdleTimeoutSeconds, ping.ElevatorVersion,
				ping.ElevatorUptimeSeconds, ping.ElevatorIdleSeconds);
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
			await conn.RequestAsync(new StopRequest(), TimeSpan.FromSeconds(10));
			return true;
		}
		catch
		{
			return false;
		}
	}

	// ── Start ───────────────────────────────────────────────────────────

	/// <summary>Start the daemon process. Finds binary via marker file.</summary>
	public static int StartDaemon(Action<string>? log = null, Action<string>? verboseLog = null)
	{
		// Prefer Task Scheduler if the daemon is registered as a task
		if (DaemonTasks.TryRunDaemonTask())
		{
			verboseLog?.Invoke("daemon starting (task)");
			return 0;
		}

		// Find daemon binary via marker file. Escalate diagnostic to the top-level
		// log when discovery fails so users see *why* without needing verbose mode.
		string? discoveryDetail = null;
		var daemonBinary = BinaryDiscovery.FindDaemon(Version, msg =>
		{
			verboseLog?.Invoke(msg);
			discoveryDetail = msg;
		});
		if (daemonBinary == null)
		{
			log?.Invoke($"daemon not found — {discoveryDetail ?? "no marker in any version directory"} (run 'seashell install')");
			return 1;
		}

		var psi = BuildDaemonStartInfo(daemonBinary);

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

	// ── ProcessStartInfo builder ───────────────────────────────────────

	/// <summary>
	/// Build a ProcessStartInfo for launching a daemon or elevator binary.
	/// Handles both native exe and dotnet exec (.dll) cases.
	/// </summary>
	internal static ProcessStartInfo BuildDaemonStartInfo(string binaryPath)
	{
		ProcessStartInfo psi;

		if (binaryPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
		{
			var binDir = Path.GetDirectoryName(binaryPath)!;
			var baseName = Path.GetFileNameWithoutExtension(binaryPath);
			var depsJson = Path.Combine(binDir, baseName + ".deps.json");
			var nugetCache = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

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
			psi.ArgumentList.Add(binaryPath);
		}
		else
		{
			psi = new ProcessStartInfo
			{
				FileName = binaryPath,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
			};
		}

		return psi;
	}

	/// <summary>Ask the daemon to spawn a process via the Elevator.</summary>
	public static async Task<SpawnResponse> RequestElevatedSpawnAsync(string daemonAddress, SpawnRequest request)
	{
		TransportStream conn;
		try
		{
			conn = await TransportClient.ConnectAsync(daemonAddress, timeoutMs: 5000);
		}
		catch (Exception ex)
		{
			return new SpawnResponse(false, 0, $"Elevator not available: {ex.Message}");
		}

		await using (conn)
		{
			// The daemon needs at most AwaitElevatorMs + 20s queue + 15s reply
			// (DaemonServer.SpawnElevatedAsync). The forward deadline leaves the
			// elevator's 15s reply plus margin inside our own timeout, so the daemon
			// never starts a request after we have given up on it.
			var replyTimeout = TimeSpan.FromMilliseconds(request.AwaitElevatorMs) + TimeSpan.FromSeconds(45);
			request = request with
			{
				ForwardDeadlineTickCount = Environment.TickCount64
					+ (long)(replyTimeout - TimeSpan.FromSeconds(20)).TotalMilliseconds,
			};

			try
			{
				var reply = await conn.RequestAsync(request, replyTimeout);
				if (reply == null)
					return new SpawnResponse(false, 0, "Daemon disconnected", MayHaveStarted: true);
				return (SpawnResponse)reply.Value.Message;
			}
			catch (Exception ex)
			{
				// The request may have been forwarded — the outcome is unknown.
				return new SpawnResponse(false, 0, $"Elevator request failed: {ex.Message}", MayHaveStarted: true);
			}
		}
	}
}

/// <summary>Structured daemon status returned by <see cref="DaemonManager.StatusAsync"/>.</summary>
public sealed record DaemonStatus(
	string Version, int UptimeSeconds, int ActiveScripts,
	bool ElevatorConnected, int Pid,
	int IdleSeconds = 0, int IdleTimeoutSeconds = 0,
	string? ElevatorVersion = null,
	int ElevatorUptimeSeconds = 0, int ElevatorIdleSeconds = 0);

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
