using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;

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
	private static Thread? _controlThread;
	private static BinaryWriter? _controlWriter;
	private static bool _stateSent;
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
		_stateSent = true;
		try { _controlWriter?.Write(state.Length); _controlWriter?.Write(state); _controlWriter?.Flush(); }
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
	public static byte[]? GetReloadState()
	{
		var path = Environment.GetEnvironmentVariable("SEASHELL_STATE");
		if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
		try { return File.ReadAllBytes(path); } catch { return null; }
	}

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

	[ModuleInitializer]
	internal static void Initialize()
	{
		IsElevated = CheckElevation();
		// The CLI is the single source of truth — it checks GetConsoleProcessList
		// before spawning us and passes the result. We can't check ourselves
		// because as a child process we inflate the console process count.
		IsConsoleEphemeral = Environment.GetEnvironmentVariable("SEASHELL_CONSOLE_EPHEMERAL") == "1";

		// Script args passed by CLI via env var (manifest is compile-time, doesn't know runtime args)
		var argsEnv = Environment.GetEnvironmentVariable("SEASHELL_ARGS");
		if (!string.IsNullOrEmpty(argsEnv))
			Args = argsEnv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		// Write ExitDelay for the CLI to read after we exit
		AppDomain.CurrentDomain.ProcessExit += (_, _) =>
		{
			try
			{
				var mp = Environment.GetEnvironmentVariable("SEASHELL_MANIFEST");
				if (!string.IsNullOrEmpty(mp))
					File.WriteAllText(mp + ".exitdelay", ExitDelay.ToString());
			}
			catch { }
		};

		// Load manifest
		var manifestPath = Environment.GetEnvironmentVariable("SEASHELL_MANIFEST");
		if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
		{
			try
			{
				var json = File.ReadAllText(manifestPath);
				var manifest = JsonSerializer.Deserialize<ScriptManifest>(json, ManifestJsonOpts);
				if (manifest != null)
					ApplyManifest(manifest);
			}
			catch { }
		}

		// Hot-swap metadata from env vars (set by CLI on reload)
		var reloadStr = Environment.GetEnvironmentVariable("SEASHELL_RELOAD_COUNT");
		if (int.TryParse(reloadStr, out var count) && count > 0)
		{
			IsReload = true;
			ReloadCount = count;
		}

		// Connect to CLI's control pipe for reload/stop signals
		var controlPipe = Environment.GetEnvironmentVariable("SEASHELL_CONTROL");
		if (!string.IsNullOrEmpty(controlPipe))
		{
			_controlThread = new Thread(() => ControlPipeLoop(controlPipe))
			{
				IsBackground = true,
				Name = "SeaShell.Control",
			};
			_controlThread.Start();
		}
	}

	// ── Control pipe ────────────────────────────────────────────────────

	private static void ControlPipeLoop(string pipeName)
	{
		try
		{
			using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
			pipe.Connect(5000);

			var reader = new StreamReader(pipe, Encoding.UTF8);
			_controlWriter = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);

			while (!IsShuttingDown)
			{
				var line = reader.ReadLine();
				if (line == null) break; // pipe closed

				switch (line.Trim().ToUpperInvariant())
				{
					case "RELOAD":
						IsShuttingDown = true;
						_shutdownCts.Cancel();
						_stateSent = false;
						try { Reloading?.Invoke(); } catch { }
						// If the handler didn't call SetReloadState, send zero-length to unblock CLI
						if (!_stateSent)
							try { _controlWriter.Write(0); _controlWriter.Flush(); } catch { }
						break;

					case "STOP":
						IsShuttingDown = true;
						_shutdownCts.Cancel();
						try { Stopping?.Invoke(); } catch { }
						break;
				}
			}
		}
		catch
		{
			// Control pipe failed — script runs without hot-swap support.
			// This is fine for scripts started directly (not via watch mode).
		}
	}

	// ── Manifest ────────────────────────────────────────────────────────

	private static void ApplyManifest(ScriptManifest m)
	{
		if (!string.IsNullOrEmpty(m.ScriptPath))
			ScriptPath = m.ScriptPath;
		if (!string.IsNullOrEmpty(m.StartDir))
			StartDir = m.StartDir;
		if (m.Args != null)
			Args = m.Args;
		if (m.Sources != null)
			Sources = m.Sources;
		if (m.Packages != null)
			Packages = m.Packages;
		if (m.Assemblies != null)
			Assemblies = m.Assemblies;
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

	[DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
	private static extern uint GetEuid();

	private static readonly JsonSerializerOptions ManifestJsonOpts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	internal sealed class ScriptManifest
	{
		public string? ScriptPath { get; set; }
		public string? StartDir { get; set; }
		public string[]? Args { get; set; }
		public string[]? Sources { get; set; }
		public Dictionary<string, string>? Packages { get; set; }
		public string[]? Assemblies { get; set; }
	}
}
