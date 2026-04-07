using System;
using System.Text;
using SeaShell.Ipc;

namespace SeaShell;

/// <summary>
/// Wraps a single attach client connection on the server side.
/// Provided to the <see cref="Sea.Attached"/> event handler.
/// Thread-safe — each client gets its own context on a thread pool thread.
/// </summary>
public sealed class AttachContext : IDisposable
{
	private readonly MessageChannel _channel;
	private bool _closed;

	/// <summary>The caller's command-line arguments.</summary>
	public string[] Args { get; }

	/// <summary>The caller's working directory.</summary>
	public string WorkingDirectory { get; }

	internal AttachContext(MessageChannel channel, string[] args, string workingDirectory)
	{
		_channel = channel;
		Args = args;
		WorkingDirectory = workingDirectory;
	}

	/// <summary>Send a binary message to the attach client.</summary>
	public void Send(byte[] payload)
	{
		if (_closed) return;
		try { _channel.SendAsync(new AttachMessage(payload)).AsTask().GetAwaiter().GetResult(); }
		catch { }
	}

	/// <summary>Send a string message to the attach client (UTF-8).</summary>
	public void SendString(string text) => Send(Encoding.UTF8.GetBytes(text));

	/// <summary>
	/// Receive a binary message from the attach client. Blocks until a message
	/// arrives or the connection is closed. Returns null if disconnected.
	/// </summary>
	public byte[]? Receive()
	{
		if (_closed) return null;
		try
		{
			var result = _channel.ReceiveAsync().AsTask().GetAwaiter().GetResult();
			if (result == null) return null;

			return result.Value.Type switch
			{
				MessageType.AttachMessage => ((AttachMessage)result.Value.Message).Payload,
				MessageType.AttachClose => null,
				_ => null,
			};
		}
		catch { return null; }
	}

	/// <summary>Close the attach connection. The client's process will exit.</summary>
	public void Close(int exitCode = 0)
	{
		if (_closed) return;
		_closed = true;
		try { _channel.SendAsync(new AttachClose(exitCode)).AsTask().GetAwaiter().GetResult(); }
		catch { }
	}

	/// <summary>Close the connection and dispose the underlying channel.</summary>
	public void Dispose()
	{
		Close();
		_channel.DisposeAsync().AsTask().GetAwaiter().GetResult();
	}
}
