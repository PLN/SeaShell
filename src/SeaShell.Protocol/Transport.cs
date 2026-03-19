using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace SeaShell.Protocol;

// ── Abstract transport ──────────────────────────────────────────────────

/// <summary>
/// A connected bidirectional stream between CLI and daemon.
/// Wraps either a NamedPipeStream (Windows) or a UnixDomainSocket (Linux).
/// </summary>
public sealed class TransportStream : IAsyncDisposable
{
	private readonly Stream _stream;
	private readonly IDisposable? _owner; // socket or pipe that owns the stream

	internal TransportStream(Stream stream, IDisposable? owner = null)
	{
		_stream = stream;
		_owner = owner;
	}

	/// <summary>Send a length-prefixed message.</summary>
	public async Task SendAsync(byte[] data, CancellationToken ct = default)
	{
		var header = new byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)data.Length);
		await _stream.WriteAsync(header, ct);
		await _stream.WriteAsync(data, ct);
		await _stream.FlushAsync(ct);
	}

	/// <summary>Receive a length-prefixed message. Returns null on clean disconnect.</summary>
	public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
	{
		var header = new byte[4];
		var read = await ReadExactAsync(header, ct);
		if (read == 0) return null; // peer disconnected

		var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
		if (length == 0) return Array.Empty<byte>();
		if (length > 4 * 1024 * 1024) // 4 MB sanity limit
			throw new InvalidOperationException($"Message too large: {length} bytes");

		var body = new byte[length];
		await ReadExactAsync(body, ct);
		return body;
	}

	private async Task<int> ReadExactAsync(byte[] buffer, CancellationToken ct)
	{
		int offset = 0;
		while (offset < buffer.Length)
		{
			var n = await _stream.ReadAsync(buffer.AsMemory(offset), ct);
			if (n == 0) return offset; // EOF
			offset += n;
		}
		return offset;
	}

	public async ValueTask DisposeAsync()
	{
		await _stream.DisposeAsync();
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
	/// </summary>
	public static string GetDaemonAddress(string identity) =>
		GetAddress($"seashell-{identity}");

	/// <summary>
	/// Pipe name (Windows) or socket file path (Linux) for the elevator.
	/// </summary>
	public static string GetElevatorAddress(string identity) =>
		GetAddress($"seashell-elevated-{identity}");

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

			_unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			_unixListener.Bind(new UnixDomainSocketEndPoint(_address));
			_unixListener.Listen(8);

			// Restrict socket to owner only — prevents other local users from
			// connecting and executing arbitrary code via the daemon.
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

		await pipe.WaitForConnectionAsync(ct);
		return new TransportStream(pipe, pipe);
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
			var ping = Envelope.Wrap(new PingRequest()).ToBytes();
			await conn.SendAsync(ping, ct);
			var reply = await conn.ReceiveAsync(ct);
			return reply != null;
		}
		catch
		{
			return false;
		}
	}
}
