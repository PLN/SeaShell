using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

using SeaShell.Ipc;

namespace SeaShell.Protocol;

// ── Abstract transport ──────────────────────────────────────────────────

/// <summary>
/// A connected bidirectional stream between CLI and daemon.
/// Wraps either a NamedPipeStream (Windows) or a UnixDomainSocket (Linux).
/// Exposes a <see cref="MessageChannel"/> for binary message framing.
/// </summary>
public sealed class TransportStream : IAsyncDisposable
{
	private readonly IDisposable? _owner; // socket or pipe that owns the stream

	/// <summary>Binary message channel over this transport.</summary>
	public MessageChannel Channel { get; }

	internal TransportStream(Stream stream, IDisposable? owner = null)
	{
		_owner = owner;
		Channel = new MessageChannel(stream, leaveOpen: false);
	}

	public async ValueTask DisposeAsync()
	{
		await Channel.DisposeAsync();
		_owner?.Dispose();
	}
}

// ── Platform endpoint ───────────────────────────────────────────────────

/// <summary>
/// Resolves the platform-appropriate IPC endpoint for a given identity.
/// Windows: named pipe.  Linux: Unix domain socket.
/// </summary>
public static class TransportEndpoint
{
	/// <summary>
	/// Pipe name (Windows) or socket file path (Linux) for the daemon.
	/// Version in the address enables side-by-side: each version gets its own daemon.
	/// </summary>
	public static string GetDaemonAddress(string identity, string version) =>
		GetAddress($"seashell-{version}-{identity}");

	/// <summary>
	/// Pipe name (Windows) or socket file path (Linux) for the elevator.
	/// </summary>
	public static string GetElevatorAddress(string identity, string version) =>
		GetAddress($"seashell-elevated-{version}-{identity}");

	private static string GetAddress(string name)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return name;

		// Linux/macOS: prefer XDG_RUNTIME_DIR (per-user, tmpfs, correct permissions)
		// Fallback to /tmp/ if unavailable.
		var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
		if (string.IsNullOrEmpty(runtimeDir) || !Directory.Exists(runtimeDir))
			runtimeDir = Path.GetTempPath();

		return Path.Combine(runtimeDir, $"{name}.sock");
	}

	/// <summary>The identity string for the current user.</summary>
	public static string CurrentUserIdentity =>
		Environment.UserName.ToLowerInvariant();

	/// <summary>4-part version of the Protocol assembly, used as the side-by-side key.
	/// Includes the build number so each pipeline build gets its own daemon instance —
	/// prevents silent version mismatches between CLI and daemon.</summary>
	public static string CurrentVersion =>
		typeof(TransportEndpoint).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";
}

// ── Server (daemon side) ────────────────────────────────────────────────

/// <summary>
/// Listens for incoming connections. Uses NamedPipeServerStream on Windows,
/// Unix domain socket on Linux.
/// </summary>
public sealed class TransportServer : IAsyncDisposable
{
	private readonly string _address;
	private Socket? _unixListener;

	public TransportServer(string address)
	{
		_address = address;
	}

	/// <summary>
	/// Start listening (Linux only — creates the socket file).
	/// On Windows, each AcceptAsync creates its own pipe instance.
	/// </summary>
	public void Start()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			// Clean up stale socket file
			if (File.Exists(_address))
				File.Delete(_address);

			// Set restrictive umask BEFORE bind to close the TOCTOU gap —
			// the socket is created with owner-only permissions from the start.
			using (PosixUmask.RestrictiveUmask())
			{
				_unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
				_unixListener.Bind(new UnixDomainSocketEndPoint(_address));
			}
			_unixListener.Listen(8);

			// Defense-in-depth: explicitly set permissions in case umask was bypassed
			File.SetUnixFileMode(_address,
				UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}

	/// <summary>Accept the next client connection.</summary>
	public async Task<TransportStream> AcceptAsync(CancellationToken ct = default)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return await AcceptWindowsAsync(ct);
		else
			return await AcceptUnixAsync(ct);
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private async Task<TransportStream> AcceptWindowsAsync(CancellationToken ct)
	{
		// Explicit ACL: allow the current user full access regardless of integrity level.
		// Without this, an elevated server (Elevator) blocks non-elevated clients (CLI).
		var security = new PipeSecurity();
		security.AddAccessRule(new PipeAccessRule(
			WindowsIdentity.GetCurrent().User!,
			PipeAccessRights.FullControl,
			AccessControlType.Allow));

		var pipe = NamedPipeServerStreamAcl.Create(
			_address,
			PipeDirection.InOut,
			NamedPipeServerStream.MaxAllowedServerInstances,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous,
			inBufferSize: 0,
			outBufferSize: 0,
			security);

		try
		{
			await pipe.WaitForConnectionAsync(ct);
			return new TransportStream(pipe, pipe);
		}
		catch
		{
			await pipe.DisposeAsync();
			throw;
		}
	}

	private async Task<TransportStream> AcceptUnixAsync(CancellationToken ct)
	{
		var socket = await _unixListener!.AcceptAsync(ct);
		var stream = new NetworkStream(socket, ownsSocket: true);
		return new TransportStream(stream, socket);
	}

	public ValueTask DisposeAsync()
	{
		if (_unixListener != null)
		{
			_unixListener.Dispose();
			// Clean up socket file
			try { File.Delete(_address); } catch { }
		}
		return ValueTask.CompletedTask;
	}
}

// ── Client (CLI side) ───────────────────────────────────────────────────

/// <summary>
/// Connects to the daemon. Uses NamedPipeClientStream on Windows,
/// Unix domain socket on Linux.
/// </summary>
public static class TransportClient
{
	public static async Task<TransportStream> ConnectAsync(
		string address, int timeoutMs = 5000, CancellationToken ct = default)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return await ConnectWindowsAsync(address, timeoutMs, ct);
		else
			return await ConnectUnixAsync(address, timeoutMs, ct);
	}

	private static async Task<TransportStream> ConnectWindowsAsync(
		string address, int timeoutMs, CancellationToken ct)
	{
		var pipe = new NamedPipeClientStream(".", address, PipeDirection.InOut, PipeOptions.Asynchronous);
		await pipe.ConnectAsync(timeoutMs, ct);
		return new TransportStream(pipe, pipe);
	}

	private static async Task<TransportStream> ConnectUnixAsync(
		string address, int timeoutMs, CancellationToken ct)
	{
		var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(timeoutMs);

		await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), cts.Token);
		var stream = new NetworkStream(socket, ownsSocket: true);
		return new TransportStream(stream, socket);
	}

	/// <summary>Check if the daemon is reachable.</summary>
	public static async Task<bool> ProbeAsync(string address, CancellationToken ct = default)
	{
		try
		{
			await using var conn = await ConnectAsync(address, timeoutMs: 1000, ct);
			await conn.Channel.SendAsync(new PingRequest(), ct);
			var reply = await conn.Channel.ReceiveAsync(ct);
			return reply != null;
		}
		catch
		{
			return false;
		}
	}
}
