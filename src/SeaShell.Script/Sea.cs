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
					try { _channel.SendAsync(new ScriptExit(Environment.ExitCode, ExitDelay)).AsTask().GetAwaiter().GetResult(); }
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
				var envelope = _channel!.ReceiveAsync().AsTask().GetAwaiter().GetResult();
				if (envelope == null) break; // disconnected

				switch (envelope.Type)
				{
					case nameof(ScriptReload):
						IsShuttingDown = true;
						_shutdownCts.Cancel();
						try { Reloading?.Invoke(); } catch { }
						break;

					case nameof(ScriptStop):
						IsShuttingDown = true;
						_shutdownCts.Cancel();
						try { Stopping?.Invoke(); } catch { }
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

		var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
			PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

		// Restrict socket to owner only (matches daemon security model)
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
