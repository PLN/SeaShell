using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
///
/// <para>
/// Maintains a pool of pre-created, concurrently-pending accept tasks so that
/// at least one listener is always active. On Windows this eliminates the
/// zero-listener gap between accept-loop iterations that otherwise surfaces
/// as <c>ERROR_FILE_NOT_FOUND</c> (which the OS may present to unprivileged
/// callers as Access Denied) for clients probing during daemon startup bursts.
/// On Linux the persistent Unix domain socket + kernel backlog makes the pool
/// unnecessary, so it degenerates to size 1.
/// </para>
///
/// <para>
/// Not thread-safe: call <see cref="AcceptAsync"/> from a single accept loop.
/// Pool tasks outlive individual <c>AcceptAsync</c> calls — only
/// <see cref="DisposeAsync"/> tears them down.
/// </para>
/// </summary>
public sealed class TransportServer : IAsyncDisposable
{
	// On Windows, 4 concurrent listening pipe instances is enough to cover any
	// realistic probe-shaped traffic — refill happens synchronously the moment
	// one transitions from listening to connected, so the pool invariant is
	// "N-1 listeners always live while a handler runs". On Linux, a single
	// accept task suffices since the kernel backlog already absorbs bursts.
	//
	// Note: this is unrelated to NamedPipeServerStream.MaxAllowedServerInstances,
	// which is the *kernel* cap on instances of a named pipe (255). Our pool
	// size is an application-level concurrency choice.
	private static readonly int s_concurrency =
		RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 4 : 1;

	private readonly string _address;
	private readonly PipeSecurity? _pipeSecurity; // Windows only — built once, reused for every Create
	private readonly List<Task<TransportStream>> _pool = new();
	private readonly CancellationTokenSource _shutdownCts = new();
	private Socket? _unixListener;
	private bool _started;

	public TransportServer(string address)
	{
		_address = address;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			// Build the pipe ACL once in the constructor. Every pool instance
			// of the same pipe name must share the same security descriptor
			// and buffer/option settings — inconsistent security across instances
			// throws UnauthorizedAccessException / IOException from Create.
			_pipeSecurity = BuildPipeSecurity();
		}
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static PipeSecurity BuildPipeSecurity()
	{
		// Explicit ACL: allow the current user full access regardless of integrity level.
		// Without this, an elevated server (Elevator) blocks non-elevated clients (CLI).
		var security = new PipeSecurity();
		security.AddAccessRule(new PipeAccessRule(
			WindowsIdentity.GetCurrent().User!,
			PipeAccessRights.FullControl,
			AccessControlType.Allow));
		return security;
	}

	/// <summary>
	/// Start listening. On Linux, binds the socket file. On both platforms,
	/// fills the listener pool so clients can connect as soon as this returns.
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

		_started = true;
		FillPool();
	}

	/// <summary>
	/// Top up the pool to <see cref="s_concurrency"/> pending accept tasks.
	/// On Windows, <c>NamedPipeServerStreamAcl.Create</c> runs synchronously
	/// and will throw from this method on ACL failure or resource exhaustion —
	/// callers should catch and retry (the pool degrades gracefully and the
	/// next successful <see cref="FillPool"/> restores it).
	/// </summary>
	private void FillPool()
	{
		while (_pool.Count < s_concurrency)
			_pool.Add(AcceptOneAsync(_shutdownCts.Token));
	}

	private Task<TransportStream> AcceptOneAsync(CancellationToken ct)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return AcceptOneWindowsAsync(ct);
		return AcceptOneUnixAsync(ct);
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private Task<TransportStream> AcceptOneWindowsAsync(CancellationToken ct)
	{
		// Synchronous Create happens outside the async state machine so its
		// failures propagate directly out of FillPool instead of being buried
		// inside a faulted pool task.
		var pipe = NamedPipeServerStreamAcl.Create(
			_address,
			PipeDirection.InOut,
			NamedPipeServerStream.MaxAllowedServerInstances,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous,
			inBufferSize: 0,
			outBufferSize: 0,
			_pipeSecurity!);

		return WaitForConnectionAsync(pipe, ct);

		static async Task<TransportStream> WaitForConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
		{
			try
			{
				await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
				return new TransportStream(pipe, pipe);
			}
			catch
			{
				await pipe.DisposeAsync().ConfigureAwait(false);
				throw;
			}
		}
	}

	private async Task<TransportStream> AcceptOneUnixAsync(CancellationToken ct)
	{
		var socket = await _unixListener!.AcceptAsync(ct).ConfigureAwait(false);
		var stream = new NetworkStream(socket, ownsSocket: true);
		return new TransportStream(stream, socket);
	}

	/// <summary>
	/// Accept the next client connection. Never cancels pool tasks — only
	/// the current wait. Use <see cref="DisposeAsync"/> to tear down the pool.
	/// </summary>
	/// <param name="ct">Cancels the current wait with <see cref="OperationCanceledException"/>.
	/// Pool listeners keep running.</param>
	/// <param name="maxWait">Optional upper bound on the wait. If it elapses
	/// before a connection arrives, returns <c>null</c> — used by idle-timeout
	/// pollers that need to wake and recheck state without disturbing listeners.</param>
	public async ValueTask<TransportStream?> AcceptAsync(CancellationToken ct = default, TimeSpan? maxWait = null)
	{
		if (!_started)
			throw new InvalidOperationException("TransportServer.Start() must be called before AcceptAsync.");

		FillPool();

		// One linked CTS covers both user-cancel and the optional maxWait timer.
		// Its token feeds a TCS-backed sentinel task that races against the pool
		// in Task.WhenAny. We never cancel the pool tasks from here — they're
		// wired to _shutdownCts, which only fires on DisposeAsync.
		using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		if (maxWait.HasValue) waitCts.CancelAfter(maxWait.Value);

		var waitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var reg = waitCts.Token.UnsafeRegister(
			static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true),
			waitTcs);

		var waits = new List<Task>(_pool.Count + 1);
		waits.AddRange(_pool);
		waits.Add(waitTcs.Task);

		var finished = await Task.WhenAny(waits).ConfigureAwait(false);

		if (finished == waitTcs.Task)
		{
			if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
			return null; // maxWait elapsed — caller polls and retries
		}

		var winner = (Task<TransportStream>)finished;
		_pool.Remove(winner);

		// Await first so a faulted pool task surfaces to the caller's accept-loop
		// catch instead of getting tangled with refill logic.
		var conn = await winner.ConfigureAwait(false);

		try
		{
			FillPool();
		}
		catch
		{
			// Refill failed — don't hand back a connection whose follow-up
			// would leak a pool slot. Let the caller see the error.
			await conn.DisposeAsync().ConfigureAwait(false);
			throw;
		}

		return conn;
	}

	public async ValueTask DisposeAsync()
	{
		_shutdownCts.Cancel();

		// Wait for pool tasks to settle. ContinueWith swallows faults so
		// Task.WhenAll doesn't re-throw during shutdown; time-box the drain
		// so a stuck listener can't block disposal.
		if (_pool.Count > 0)
		{
			var drain = Task.WhenAll(_pool.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
			await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

			// Any pool tasks that managed to complete with a connection before
			// shutdown need explicit disposal — otherwise those pipe instances
			// linger and can block the pipe name on the next daemon start.
			foreach (var task in _pool)
			{
				if (task.IsCompletedSuccessfully)
				{
					try { await task.Result.DisposeAsync().ConfigureAwait(false); }
					catch { }
				}
			}
			_pool.Clear();
		}

		_shutdownCts.Dispose();

		if (_unixListener != null)
		{
			_unixListener.Dispose();
			// Clean up socket file
			try { File.Delete(_address); } catch { }
		}
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
