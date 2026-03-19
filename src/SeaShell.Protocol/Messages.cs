using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeaShell.Protocol;

// ── CLI → Daemon ────────────────────────────────────────────────────────

public sealed record RunRequest(
	string ScriptPath,
	string[] Args,
	string WorkingDirectory,
	string[] EnvironmentVars,
	int CliPid
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
	string? Error
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
	string Reason
);

public sealed record PingRequest();

public sealed record PingResponse(
	string Version,
	bool IsElevated,
	bool ElevatorConnected,
	int UptimeSeconds,
	int ActiveScripts
);

public sealed record StopRequest();

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

// ── Elevator → Daemon (worker registration) ─────────────────────────────

/// <summary>
/// Elevator connects to Daemon and sends this as its first message.
/// The Daemon holds this connection and sends SpawnRequests over it.
/// </summary>
public sealed record ElevatorHello(bool IsElevated);

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
	string[] EnvironmentVars
);

/// <summary>Elevator → Daemon: result of the spawn.</summary>
public sealed record SpawnResponse(
	bool Success,
	int ProcessId,
	string? Error
);

// ── Envelope ────────────────────────────────────────────────────────────

/// <summary>
/// Wire envelope: a type discriminator + JSON payload.
/// Keeps the protocol extensible without changing framing.
/// </summary>
public sealed record Envelope(string Type, JsonElement Payload)
{
	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public static Envelope Wrap<T>(T message) where T : notnull
	{
		var element = JsonSerializer.SerializeToElement(message, JsonOpts);
		return new Envelope(typeof(T).Name, element);
	}

	public T Unwrap<T>() => Payload.Deserialize<T>(JsonOpts)
		?? throw new InvalidOperationException($"Failed to deserialize {Type} as {typeof(T).Name}");

	public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOpts);

	public static Envelope FromBytes(byte[] bytes) =>
		JsonSerializer.Deserialize<Envelope>(bytes, JsonOpts)
		?? throw new InvalidOperationException("Failed to deserialize envelope");
}
