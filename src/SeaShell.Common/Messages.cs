using System;
using System.Collections.Generic;

namespace SeaShell.Ipc;

// ── Wire type tags ─────────────────────────────────────────────────────

/// <summary>
/// 1-byte type discriminator on the wire.
/// Format: [4-byte LE length][1-byte MessageType][MessagePack payload]
/// </summary>
public enum MessageType : byte
{
	// Script pipe (launcher ↔ script)
	ScriptInit    = 1,
	ScriptReload  = 2,
	ScriptStop    = 3,
	ScriptExit    = 4,
	ScriptState   = 5,
	HostMessage   = 6,
	ScriptMessage = 7,
	ScriptReloadRequest = 8,

	// Daemon protocol (CLI ↔ daemon)
	PingRequest     = 20,
	PingResponse    = 21,
	StopRequest     = 22,
	RunRequest      = 23,
	RunResponse     = 24,
	HotSwapNotify   = 25,
	ReplStartReq    = 26,
	ReplStartResp   = 27,
	ReplEvalReq     = 28,
	ReplEvalResp    = 29,
	RecompileRequest = 30,

	// Attach protocol (blocked caller ↔ running script)
	AttachHello     = 50,
	AttachMessage   = 51,
	AttachClose     = 52,

	// Elevator protocol (daemon ↔ elevator)
	ElevatorHello   = 40,
	ElevatorAck     = 41,
	SpawnRequest    = 42,
	SpawnResponse   = 43,
}

// ── Type ↔ Tag mapping ─────────────────────────────────────────────────

/// <summary>
/// Bidirectional lookup between CLR types and MessageType wire tags.
/// </summary>
public static class MessageTypeMap
{
	private static readonly Dictionary<Type, MessageType> _typeToTag = new()
	{
		[typeof(ScriptInit)]      = MessageType.ScriptInit,
		[typeof(ScriptReload)]    = MessageType.ScriptReload,
		[typeof(ScriptStop)]      = MessageType.ScriptStop,
		[typeof(ScriptExit)]      = MessageType.ScriptExit,
		[typeof(ScriptState)]     = MessageType.ScriptState,
		[typeof(HostMessage)]     = MessageType.HostMessage,
		[typeof(ScriptMessage)]   = MessageType.ScriptMessage,
		[typeof(ScriptReloadRequest)] = MessageType.ScriptReloadRequest,
		[typeof(PingRequest)]     = MessageType.PingRequest,
		[typeof(PingResponse)]    = MessageType.PingResponse,
		[typeof(StopRequest)]     = MessageType.StopRequest,
		[typeof(RunRequest)]      = MessageType.RunRequest,
		[typeof(RunResponse)]     = MessageType.RunResponse,
		[typeof(HotSwapNotify)]   = MessageType.HotSwapNotify,
		[typeof(ReplStartRequest)]  = MessageType.ReplStartReq,
		[typeof(ReplStartResponse)] = MessageType.ReplStartResp,
		[typeof(ReplEvalRequest)]   = MessageType.ReplEvalReq,
		[typeof(ReplEvalResponse)]  = MessageType.ReplEvalResp,
		[typeof(RecompileRequest)]  = MessageType.RecompileRequest,
		[typeof(AttachHello)]     = MessageType.AttachHello,
		[typeof(AttachMessage)]   = MessageType.AttachMessage,
		[typeof(AttachClose)]     = MessageType.AttachClose,
		[typeof(ElevatorHello)]   = MessageType.ElevatorHello,
		[typeof(ElevatorAck)]     = MessageType.ElevatorAck,
		[typeof(SpawnRequest)]    = MessageType.SpawnRequest,
		[typeof(SpawnResponse)]   = MessageType.SpawnResponse,
	};

	private static readonly Dictionary<MessageType, Type> _tagToType = new();

	static MessageTypeMap()
	{
		foreach (var (type, tag) in _typeToTag)
			_tagToType[tag] = type;
	}

	public static MessageType GetTag<T>() =>
		_typeToTag.TryGetValue(typeof(T), out var tag)
			? tag
			: throw new InvalidOperationException($"No MessageType tag registered for {typeof(T).Name}");

	public static MessageType GetTag(Type type) =>
		_typeToTag.TryGetValue(type, out var tag)
			? tag
			: throw new InvalidOperationException($"No MessageType tag registered for {type.Name}");

	public static Type GetClrType(MessageType tag) =>
		_tagToType.TryGetValue(tag, out var type)
			? type
			: throw new InvalidOperationException($"Unknown MessageType tag: {(byte)tag}");

	public static bool TryGetClrType(MessageType tag, out Type type) =>
		_tagToType.TryGetValue(tag, out type!);
}

// ── Launcher ↔ Script (over the script pipe) ────────────────────────────

/// <summary>Launcher → Script: first message after connection. All context.</summary>
public sealed record ScriptInit(
	string? ScriptPath,
	string? StartDir,
	string[]? Args,
	string[]? Sources,
	Dictionary<string, string>? Packages,
	string[]? Assemblies,
	bool IsConsoleEphemeral,
	int LauncherPid,
	int ReloadCount,
	string? State,
	bool Watch,
	bool Restart = false,
	int RestartCount = 0,
	byte MutexScope = 0,
	bool MutexAttach = false,
	bool WindowMode = false
);

/// <summary>Launcher → Script: hot-swap — save state and shut down.</summary>
public sealed record ScriptReload();

/// <summary>Launcher → Script: clean shutdown.</summary>
public sealed record ScriptStop();

/// <summary>Script → Launcher: process is exiting.</summary>
public sealed record ScriptExit(int ExitCode, int ExitDelay, bool Restart = true);

/// <summary>Script → Launcher: reload state (response to ScriptReload).</summary>
public sealed record ScriptState(string? Data);

/// <summary>Launcher → Script: application message during execution.</summary>
public sealed record HostMessage(byte[] Payload, string? Topic);

/// <summary>Script → Launcher: application message during execution.</summary>
public sealed record ScriptMessage(byte[] Payload, string? Topic);

/// <summary>Script → Launcher: script requests to be recompiled and reloaded.</summary>
public sealed record ScriptReloadRequest(string? Reason, bool ClearCache = false);

// ── CLI → Daemon ────────────────────────────────────────────────────────

public sealed record RunRequest(
	string ScriptPath,
	string[] Args,
	string WorkingDirectory,
	string[] EnvironmentVars,
	int CliPid,
	bool ClearCache = false
);

/// <summary>
/// Daemon → CLI response. Two modes:
///   Normal:   Elevated=false, AssemblyPath set → CLI spawns the process.
///   Elevated: Elevated=true,  ProcessId set    → Elevator already spawned it, CLI just waits.
/// Watch: if true, CLI should keep the connection open for HotSwapNotify messages.
/// </summary>
public sealed record RunResponse(
	bool Success,
	bool Elevated,
	bool Watch,
	string? AssemblyPath,
	string? DepsJsonPath,
	string? RuntimeConfigPath,
	string? ManifestPath,
	int ProcessId,
	string? Error,
	string? StartupHookPath = null,
	bool DirectExe = false,
	bool Restart = false,
	byte MutexScope = 0,
	bool MutexAttach = false
);

/// <summary>
/// Daemon → CLI push: source file changed, here are new artifacts.
/// CLI should swap the running process.
/// </summary>
public sealed record HotSwapNotify(
	string AssemblyPath,
	string? DepsJsonPath,
	string? RuntimeConfigPath,
	string? ManifestPath,
	string Reason,
	string? StartupHookPath = null,
	bool DirectExe = false,
	bool Restart = false
);

public sealed record PingRequest();

public sealed record PingResponse(
	string Version,
	bool IsElevated,
	bool ElevatorConnected,
	int UptimeSeconds,
	int ActiveScripts,
	int Pid = 0,
	string? DaemonHash = null,
	int IdleSeconds = 0,
	int IdleTimeoutSeconds = 0,
	string? ElevatorVersion = null,
	int ElevatorUptimeSeconds = 0,
	int ElevatorIdleSeconds = 0
);

public sealed record StopRequest();

/// <summary>CLI → Daemon: request immediate recompilation (triggered by script-initiated reload).</summary>
public sealed record RecompileRequest(bool ClearCache = false);

// ── REPL protocol ───────────────────────────────────────────────────

/// <summary>CLI → Daemon: start an interactive REPL session over this connection.</summary>
public sealed record ReplStartRequest(string[] NuGetPackages);

/// <summary>Daemon → CLI: REPL session is ready.</summary>
public sealed record ReplStartResponse(bool Success, string? Error);

/// <summary>CLI → Daemon: evaluate code in the REPL session.</summary>
public sealed record ReplEvalRequest(string Code);

/// <summary>
/// Daemon → CLI: evaluation result.
/// Result is the display string of the return value (null if void/no value).
/// Output is any Console.Write output captured during evaluation.
/// IsComplete indicates whether the code was a complete statement
/// (false = needs more input, e.g., unclosed brace).
/// </summary>
public sealed record ReplEvalResponse(
	bool Success,
	string? Result,
	string? Output,
	string? Error,
	bool IsComplete
);

// ── Attach protocol (blocked caller ↔ running script) ──────────────────

/// <summary>Client → Server: initial handshake with caller's args and CWD.</summary>
public sealed record AttachHello(string[] Args, string WorkingDirectory);

/// <summary>Bidirectional: application-level message between attach client and server.</summary>
public sealed record AttachMessage(byte[] Payload);

/// <summary>Either side: close the attach connection.</summary>
public sealed record AttachClose(int ExitCode = 0);

// ── Elevator → Daemon (worker registration) ─────────────────────────────

/// <summary>
/// Elevator connects to Daemon and sends this as its first message.
/// The Daemon holds this connection and sends SpawnRequests over it.
/// </summary>
public sealed record ElevatorHello(bool IsElevated, string? Version = null);

/// <summary>Daemon acknowledges the Elevator registration.</summary>
public sealed record ElevatorAck(bool Accepted, string? Reason);

// ── Daemon → Elevator (over the held connection) ────────────────────────

/// <summary>
/// Daemon asks the Elevator to spawn an already-compiled script as an elevated process.
/// The Elevator does no compilation — it just launches dotnet exec.
/// </summary>
public sealed record SpawnRequest(
	string AssemblyPath,
	string? DepsJsonPath,
	string? RuntimeConfigPath,
	string[] Args,
	string WorkingDirectory,
	string[] EnvironmentVars,
	int CliPid,
	int AwaitElevatorMs = 0
);

/// <summary>Elevator → Daemon: result of the spawn.</summary>
public sealed record SpawnResponse(
	bool Success,
	int ProcessId,
	string? Error
);
