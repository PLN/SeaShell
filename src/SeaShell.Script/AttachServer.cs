using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using SeaShell.Ipc;

namespace SeaShell;

/// <summary>
/// Named pipe listener for the //sea_mutex_attach protocol.
/// Accepts incoming connections from blocked callers and raises
/// <see cref="Sea.Attached"/> for each one.
/// </summary>
internal static class AttachServer
{
	private static Thread? _listenerThread;
	private static string? _pipeName;
	private static volatile bool _stopping;

	/// <summary>
	/// Start listening for attach connections on the well-known pipe name.
	/// Call this once from Sea.Initialize() when //sea_mutex_attach is active.
	/// </summary>
	public static void Start(string pipeName)
	{
		_pipeName = pipeName;
		_stopping = false;
		_listenerThread = new Thread(ListenLoop)
		{
			IsBackground = true,
			Name = "SeaShell.Attach",
		};
		_listenerThread.Start();
	}

	/// <summary>Stop accepting new connections.</summary>
	public static void Stop()
	{
		_stopping = true;
	}

	private static void ListenLoop()
	{
		while (!_stopping)
		{
			try
			{
				var pipe = CreateServerPipe(_pipeName!);
				pipe.WaitForConnection();

				// Handle each client on a thread pool thread
				var channel = new MessageChannel(pipe);
				ThreadPool.QueueUserWorkItem(_ => HandleClient(channel));
			}
			catch
			{
				if (!_stopping)
					Thread.Sleep(100); // brief pause before retry on transient errors
			}
		}
	}

	private static void HandleClient(MessageChannel channel)
	{
		try
		{
			// First message must be AttachHello
			var result = channel.ReceiveAsync().AsTask().GetAwaiter().GetResult();
			if (result == null) return;
			if (result.Value.Type != MessageType.AttachHello) return;

			var hello = (AttachHello)result.Value.Message;
			using var ctx = new AttachContext(channel, hello.Args, hello.WorkingDirectory);

			// Raise the event — script handles the conversation
			Sea.RaiseAttached(ctx);

			// If script didn't explicitly close, close now
			ctx.Close();
		}
		catch { }
		finally
		{
			try { channel.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
		}
	}

	private static NamedPipeServerStream CreateServerPipe(string pipeName)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			var security = new PipeSecurity();
			security.AddAccessRule(new PipeAccessRule(
				WindowsIdentity.GetCurrent().User!,
				PipeAccessRights.FullControl,
				System.Security.AccessControl.AccessControlType.Allow));
			return NamedPipeServerStreamAcl.Create(
				pipeName, PipeDirection.InOut, 1,
				PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
		}

		// Linux: restrictive umask BEFORE pipe creation — no TOCTOU gap
		NamedPipeServerStream pipe;
		using (PosixUmask.RestrictiveUmask())
		{
			pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
				PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		}

		// Defense-in-depth: explicitly set permissions
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
}
