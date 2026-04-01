using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using SeaShell.Ipc;

namespace SeaShell;

/// <summary>
/// Runtime context for SeaShell scripts. Available in every script via `Sea.*`.
///
/// Provides script identity, environment info, build metadata, and hot-swap
/// lifecycle events.
/// </summary>
public static class Sea
{
	// ── Script identity ─────────────────────────────────────────────────

	/// <summary>Absolute path to the main script file.</summary>
	public static string ScriptPath { get; private set; } = "";

	/// <summary>Directory containing the main script.</summary>
	public static string ScriptDir => Path.GetDirectoryName(ScriptPath) ?? "";

	/// <summary>Script filename without extension.</summary>
	public static string ScriptName => Path.GetFileNameWithoutExtension(ScriptPath);

	/// <summary>Script filename with extension.</summary>
	public static string ScriptFile => Path.GetFileName(ScriptPath);

	// ── Environment ─────────────────────────────────────────────────────

	/// <summary>
	/// The directory where 'sea' was invoked. Always correct — SeaShell never
	/// changes CWD, unlike CS-Script which switches to the script's directory.
	/// </summary>
	public static string StartDir { get; private set; } = Environment.CurrentDirectory;

	/// <summary>Whether the script is running with elevated privileges (admin/root).</summary>
	public static bool IsElevated { get; private set; }

	/// <summary>
	/// True when sea.exe detected the console window will likely close on exit
	/// (e.g., script was double-clicked from Explorer rather than run from a terminal).
	/// Always false when running via SeaShell.Host.
	/// </summary>
	public static bool IsConsoleEphemeral { get; private set; }

	/// <summary>
	/// Seconds to delay before the console window closes when <see cref="IsConsoleEphemeral"/> is true.
	/// The CLI shows an interactive countdown that the user can skip (Enter), cancel (Escape),
	/// or adjust (arrow keys). Set to 0 to exit immediately.
	/// Default: 7 seconds. Ignored when not ephemeral or when running via Host.
	/// </summary>
	public static int ExitDelay { get; set; } = 7;

	/// <summary>Script arguments (same as top-level 'args' but explicitly named).</summary>
	public static string[] Args { get; private set; } = Array.Empty<string>();

	// ── Build metadata ──────────────────────────────────────────────────

	/// <summary>All source files that were compiled (main script + includes), in dependency order.</summary>
	public static IReadOnlyList<string> Sources { get; private set; } = Array.Empty<string>();

	/// <summary>NuGet packages resolved for this script (name → version).</summary>
	public static IReadOnlyDictionary<string, string> Packages { get; private set; }
		= new Dictionary<string, string>();

	/// <summary>Managed assembly paths loaded for this script.</summary>
	public static IReadOnlyList<string> Assemblies { get; private set; } = Array.Empty<string>();

	// ── Hot-swap lifecycle ──────────────────────────────────────────────

	/// <summary>
	/// Fires when the CLI is about to replace this process with a recompiled version.
	/// Use this to save state, close connections, flush logs, etc.
	/// Call <see cref="SetReloadState(byte[])"/> during this event to pass state to the new instance.
	/// After this event completes (or the grace period expires), the process is killed.
	/// </summary>
	public static event Action? Reloading;

	/// <summary>
	/// Fires when the CLI requests a clean stop (e.g., Ctrl+C, `sea --stop`).
	/// </summary>
	public static event Action? Stopping;

	/// <summary>
	/// Fires when the Host sends an application message during execution.
	/// Parameters: payload (raw bytes), topic (optional routing key).
	/// Only fires when running via ScriptHost, not CLI.
	/// </summary>
	public static event Action<byte[], string?>? MessageReceived;

	/// <summary>
	/// Fires when a blocked invocation attaches to this instance via //sea_mutex_attach.
	/// The handler receives an <see cref="AttachContext"/> for bidirectional communication
	/// with the caller. Each client gets its own event invocation on a thread pool thread.
	/// </summary>
	public static event Action<AttachContext>? Attached;

	/// <summary>True when running in watch mode (//sea_watch). Enables file-change hot-swap.</summary>
	public static bool IsWatchMode { get; private set; }

	/// <summary>The mutex scope active for this script (0=None, 1=Session, 2=User, 3=System).</summary>
	public static byte MutexScope { get; private set; }

	/// <summary>
	/// True when running under seaw.exe (Windows subsystem) without a console.
	/// False when running under sea.exe or when seaw.exe allocated a console
	/// due to //sea_console.
	/// </summary>
	public static bool IsWindowMode { get; private set; }

	/// <summary>
	/// True when running under //sea_restart. The Invoker will restart the script
	/// process on exit unless <see cref="Restart"/> is set to false.
	/// </summary>
	public static bool IsRestartMode { get; private set; }

	/// <summary>
	/// Controls whether the Invoker restarts this script on exit.
	/// Default: true when //sea_restart is active. Set to false to exit cleanly
	/// without restart. Only meaningful when <see cref="IsRestartMode"/> is true.
	/// </summary>
	public static bool Restart { get; set; }

	/// <summary>
	/// How many times this script has been restarted (0 on first run).
	/// Distinct from <see cref="ReloadCount"/> — reload is recompile+swap,
	/// restart is process-level.
	/// </summary>
	public static int RestartCount { get; private set; }

	/// <summary>True if this instance was started as a hot-swap replacement (not the first run).</summary>
	public static bool IsReload { get; private set; }

	/// <summary>How many times this script has been reloaded (0 on first run).</summary>
	public static int ReloadCount { get; private set; }

	/// <summary>True after Reloading or Stopping has fired. Check this in loops.</summary>
	public static bool IsShuttingDown { get; private set; }

	/// <summary>
	/// A cancellation token that is cancelled when Reloading or Stopping fires.
	/// Pass this to async operations for automatic cancellation on reload.
	/// </summary>
	public static CancellationToken ShutdownToken => _shutdownCts.Token;

	private static readonly CancellationTokenSource _shutdownCts = new();
	private static MessageChannel? _channel;
	private static Thread? _receiveThread;
	private static byte[]? _reloadState;
	private const int MaxStateSize = 8192;

	/// <summary>
	/// Pass state to the next instance during a reload. Call this from the Reloading event.
	/// The byte array is passed to the new process and available via GetReloadState().
	/// Maximum 8 KB.
	/// </summary>
	public static void SetReloadState(byte[] state)
	{
		if (state.Length > MaxStateSize)
			throw new ArgumentException($"Reload state too large: {state.Length} bytes (max {MaxStateSize})");
		try { _channel?.SendAsync(new ScriptState(Convert.ToBase64String(state))).AsTask().GetAwaiter().GetResult(); }
		catch { }
	}

	/// <summary>
	/// Pass a string state to the next instance during a reload.
	/// Convenience wrapper around SetReloadState(byte[]).
	/// </summary>
	public static void SetReloadState(string state) =>
		SetReloadState(Encoding.UTF8.GetBytes(state));

	/// <summary>
	/// Retrieve state passed from the previous instance via SetReloadState().
	/// Returns null on first run or if no state was passed.
	/// </summary>
	public static byte[]? GetReloadState() => _reloadState;

	/// <summary>
	/// Retrieve string state passed from the previous instance.
	/// Returns null on first run or if no state was passed.
	/// </summary>
	public static string? GetReloadStateString()
	{
		var bytes = GetReloadState();
		return bytes != null ? Encoding.UTF8.GetString(bytes) : null;
	}

	// ── Application messaging ──────────────────────────────────────────

	/// <summary>Send a binary message to the Host. No-op if not connected.</summary>
	public static async System.Threading.Tasks.Task SendMessageAsync(byte[] payload, string? topic = null)
	{
		var ch = _channel;
		if (ch == null) return;
		await ch.SendAsync(new ScriptMessage(payload, topic));
	}

	/// <summary>Send a string message to the Host (UTF-8 encoded). No-op if not connected.</summary>
	public static async System.Threading.Tasks.Task SendMessageAsync(string payload, string? topic = null) =>
		await SendMessageAsync(Encoding.UTF8.GetBytes(payload), topic);

	/// <summary>Send a binary message to the Host (synchronous). No-op if not connected.</summary>
	public static void SendMessage(byte[] payload, string? topic = null)
	{
		var ch = _channel;
		if (ch == null) return;
		try { ch.SendAsync(new ScriptMessage(payload, topic)).AsTask().GetAwaiter().GetResult(); }
		catch { }
	}

	/// <summary>Send a string message to the Host (synchronous, UTF-8). No-op if not connected.</summary>
	public static void SendMessage(string payload, string? topic = null) =>
		SendMessage(Encoding.UTF8.GetBytes(payload), topic);

	// ── Script-initiated reload ─────────────────────────────────────────

	/// <summary>
	/// Request this script to be recompiled and reloaded. Triggers the same hot-swap
	/// path as a file change. Works in both direct and watch mode.
	/// </summary>
	/// <param name="clearCache">If true, forces a full recompile by clearing the compilation cache.</param>
	/// <param name="reason">Optional reason string for logging.</param>
	public static void RequestReload(bool clearCache = false, string? reason = null)
	{
		var ch = _channel ?? throw new InvalidOperationException("No IPC channel — script was not launched by SeaShell");
		try { ch.SendAsync(new ScriptReloadRequest(reason, clearCache)).AsTask().GetAwaiter().GetResult(); }
		catch { }
	}

	/// <summary>Async version of <see cref="RequestReload"/>.</summary>
	public static async System.Threading.Tasks.Task RequestReloadAsync(bool clearCache = false, string? reason = null)
	{
		var ch = _channel ?? throw new InvalidOperationException("No IPC channel — script was not launched by SeaShell");
		await ch.SendAsync(new ScriptReloadRequest(reason, clearCache));
	}

	// ── Initialization ──────────────────────────────────────────────────

	private static bool _initialized;

	/// <summary>
	/// Called by the script assembly's module initializer (_SeaShellBoot) to guarantee
	/// initialization runs before any script code, regardless of whether the script
	/// references Sea.* types directly.
	/// </summary>
	public static void EnsureInitialized()
	{
		if (_initialized) return;
		Initialize();
	}

	[ModuleInitializer]
	internal static void Initialize()
	{
		if (_initialized) return;
		_initialized = true;

		IsElevated = CheckElevation();

		// Elevated scripts spawned by the Elevator: attach to the CLI's console
		// so Console.Title, stdout, stderr all target the correct window.
		var cliPidStr = Environment.GetEnvironmentVariable("SEASHELL_CLI_PID");
		if (int.TryParse(cliPidStr, out var cliPid) && cliPid > 0
			&& RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			FreeConsole();
			if (!AttachConsole((uint)cliPid))
				AllocConsole(); // fallback: at least get a console
		}

		// Create pipe server — the launcher (CLI or Host) connects as client.
		var pipeName = Environment.GetEnvironmentVariable("SEASHELL_PIPE");
		if (!string.IsNullOrEmpty(pipeName))
		{
			try
			{
				var pipe = CreateServerPipe(pipeName);
				pipe.WaitForConnection();
				_channel = new MessageChannel(pipe);

				// Receive ScriptInit (first message, blocks until CLI sends it)
				var init = _channel.ReceiveAsync<ScriptInit>().AsTask().GetAwaiter().GetResult();
				if (init != null)
					ApplyInit(init);

				// Send ScriptExit when process exits
				AppDomain.CurrentDomain.ProcessExit += (_, _) =>
				{
					try { _channel.SendAsync(new ScriptExit(Environment.ExitCode, ExitDelay, Restart)).AsTask().GetAwaiter().GetResult(); }
					catch { }
				};

				// Background receive loop for ScriptReload/ScriptStop
				_receiveThread = new Thread(ReceiveLoop)
				{
					IsBackground = true,
					Name = "SeaShell.Channel",
				};
				_receiveThread.Start();
			}
			catch
			{
				// Pipe connection failed — script runs without launcher communication.
			}
		}
	}

	// ── Receive loop ────────────────────────────────────────────────────

	private static void ReceiveLoop()
	{
		try
		{
			while (!IsShuttingDown)
			{
				var result = _channel!.ReceiveAsync().AsTask().GetAwaiter().GetResult();
				if (result == null) break; // disconnected

				switch (result.Value.Type)
				{
					case MessageType.ScriptReload:
						IsShuttingDown = true;
						_shutdownCts.Cancel();
						AttachServer.Stop();
						try { Reloading?.Invoke(); } catch { }
						break;

					case MessageType.ScriptStop:
						IsShuttingDown = true;
						_shutdownCts.Cancel();
						AttachServer.Stop();
						try { Stopping?.Invoke(); } catch { }
						break;

					case MessageType.HostMessage:
						var hm = (HostMessage)result.Value.Message;
						try { MessageReceived?.Invoke(hm.Payload, hm.Topic); } catch { }
						break;
				}
			}
		}
		catch
		{
			// Channel error — script continues without signal support.
		}
	}

	// ── Pipe creation ───────────────────────────────────────────────────

	private static NamedPipeServerStream CreateServerPipe(string pipeName)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			// Explicit ACL: allow the current user full access regardless of
			// integrity level. Elevated scripts run as the same user as the
			// non-elevated CLI, so current-user ACL works across elevation.
			var security = new PipeSecurity();
			security.AddAccessRule(new PipeAccessRule(
				WindowsIdentity.GetCurrent().User!,
				PipeAccessRights.FullControl,
				System.Security.AccessControl.AccessControlType.Allow));
			return NamedPipeServerStreamAcl.Create(
				pipeName, PipeDirection.InOut, 1,
				PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
		}

		// Set restrictive umask BEFORE pipe creation to close the TOCTOU gap —
		// the underlying Unix socket is created with owner-only permissions from the start.
		NamedPipeServerStream pipe;
		using (PosixUmask.RestrictiveUmask())
		{
			pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
				PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		}

		// Defense-in-depth: explicitly set permissions in case umask was bypassed
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			try
			{
				var socketPath = Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{pipeName}");
				if (File.Exists(socketPath))
					File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}
			catch { }
		}

		return pipe;
	}

	// ── Init ────────────────────────────────────────────────────────────

	private static void ApplyInit(ScriptInit init)
	{
		if (!string.IsNullOrEmpty(init.ScriptPath))
			ScriptPath = init.ScriptPath;
		if (!string.IsNullOrEmpty(init.StartDir))
			StartDir = init.StartDir;
		if (init.Args != null)
			Args = init.Args;
		if (init.Sources != null)
			Sources = init.Sources;
		if (init.Packages != null)
			Packages = init.Packages;
		if (init.Assemblies != null)
			Assemblies = init.Assemblies;

		IsConsoleEphemeral = init.IsConsoleEphemeral;
		IsWatchMode = init.Watch;
		IsRestartMode = init.Restart;
		Restart = init.Restart; // default to true when //sea_restart is active
		RestartCount = init.RestartCount;
		MutexScope = init.MutexScope;
		IsWindowMode = init.WindowMode;

		// Start attach server if //sea_mutex_attach is active
		if (init.MutexAttach && !string.IsNullOrEmpty(init.ScriptPath))
		{
			var identity = ComputeIdentity(init.ScriptPath);
			AttachServer.Start($"seashell-attach-{identity}");
		}

		if (init.ReloadCount > 0)
		{
			IsReload = true;
			ReloadCount = init.ReloadCount;
		}

		if (!string.IsNullOrEmpty(init.State))
		{
			try { _reloadState = Convert.FromBase64String(init.State); }
			catch { }
		}
	}

	/// <summary>FNV-1a hash of the normalized script path — same identity as DirectiveScanner.</summary>
	private static string ComputeIdentity(string scriptPath)
	{
		var normalized = Path.GetFullPath(scriptPath).ToLowerInvariant();
		const ulong fnvOffsetBasis = 14695981039346656037;
		const ulong fnvPrime = 1099511628211;
		var hash = fnvOffsetBasis;
		foreach (var c in normalized) { hash ^= c; hash *= fnvPrime; }
		return hash.ToString("x16");
	}

	/// <summary>Raise the Attached event. Called by AttachServer.</summary>
	internal static void RaiseAttached(AttachContext ctx)
	{
		try { Attached?.Invoke(ctx); } catch { }
	}

	private static bool CheckElevation()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			using var identity = WindowsIdentity.GetCurrent();
			var principal = new WindowsPrincipal(identity);
			return principal.IsInRole(WindowsBuiltInRole.Administrator);
		}
		return Environment.UserName == "root" || GetEuid() == 0;
	}

	// ── P/Invoke ────────────────────────────────────────────────────────

	[DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
	private static extern uint GetEuid();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool FreeConsole();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AttachConsole(uint dwProcessId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AllocConsole();
}
