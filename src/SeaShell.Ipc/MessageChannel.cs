using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace SeaShell.Ipc;

/// <summary>
/// Bidirectional message channel over a stream, using System.IO.Pipelines for
/// efficient buffered I/O. Wire format: 4-byte LE length prefix + Envelope JSON.
///
/// Supports concurrent send + receive (PipeReader and PipeWriter are independent).
/// A write lock prevents interleaved sends from concurrent callers.
/// </summary>
public sealed class MessageChannel : IAsyncDisposable
{
	private readonly PipeReader _reader;
	private readonly PipeWriter _writer;
	private readonly Stream? _stream;
	private readonly bool _leaveOpen;
	private readonly SemaphoreSlim _writeLock = new(1, 1);

	private const int HeaderSize = 4;
	private const uint MaxMessageSize = 4 * 1024 * 1024; // 4 MB

	public MessageChannel(Stream stream, bool leaveOpen = false)
	{
		_stream = stream;
		_leaveOpen = leaveOpen;
		_reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
		_writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
	}

	/// <summary>Send a typed message as a length-prefixed Envelope.</summary>
	public async ValueTask SendAsync<T>(T message, CancellationToken ct = default) where T : notnull
	{
		var payload = Envelope.Wrap(message).ToBytes();
		await _writeLock.WaitAsync(ct);
		try
		{
			// Write header (4-byte LE length)
			var header = _writer.GetMemory(HeaderSize);
			BinaryPrimitives.WriteUInt32LittleEndian(header.Span, (uint)payload.Length);
			_writer.Advance(HeaderSize);

			// Write payload
			var dest = _writer.GetMemory(payload.Length);
			payload.CopyTo(dest);
			_writer.Advance(payload.Length);

			await _writer.FlushAsync(ct);
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>
	/// Receive the next message. Returns null on clean disconnect.
	/// </summary>
	public async ValueTask<Envelope?> ReceiveAsync(CancellationToken ct = default)
	{
		// Read until we have a complete message (header + payload)
		uint payloadLength = 0;
		bool headerRead = false;

		while (true)
		{
			var result = await _reader.ReadAsync(ct);
			var buffer = result.Buffer;

			if (result.IsCanceled)
				return null;

			if (!headerRead)
			{
				if (buffer.Length < HeaderSize)
				{
					if (result.IsCompleted)
						return null; // peer disconnected before header
					_reader.AdvanceTo(buffer.Start, buffer.End);
					continue;
				}

				// Parse header
				Span<byte> headerBytes = stackalloc byte[HeaderSize];
				buffer.Slice(0, HeaderSize).CopyTo(headerBytes);
				payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes);

				if (payloadLength > MaxMessageSize)
					throw new InvalidOperationException($"Message too large: {payloadLength} bytes");

				headerRead = true;
			}

			var totalNeeded = HeaderSize + payloadLength;
			if (buffer.Length < totalNeeded)
			{
				if (result.IsCompleted)
					return null; // peer disconnected mid-message
				_reader.AdvanceTo(buffer.Start, buffer.End);
				continue;
			}

			// Parse payload
			var payloadSlice = buffer.Slice(HeaderSize, (int)payloadLength);
			Envelope envelope;
			if (payloadSlice.IsSingleSegment)
			{
				envelope = Envelope.FromBytes(payloadSlice.FirstSpan);
			}
			else
			{
				var temp = payloadSlice.ToArray();
				envelope = Envelope.FromBytes(temp);
			}

			_reader.AdvanceTo(buffer.GetPosition(totalNeeded));
			return envelope;
		}
	}

	/// <summary>Receive and unwrap to the expected type. Returns default if disconnected.</summary>
	public async ValueTask<T?> ReceiveAsync<T>(CancellationToken ct = default) where T : class
	{
		var envelope = await ReceiveAsync(ct);
		return envelope?.Unwrap<T>();
	}

	public async ValueTask DisposeAsync()
	{
		await _reader.CompleteAsync();
		await _writer.CompleteAsync();
		if (!_leaveOpen && _stream != null)
			await _stream.DisposeAsync();
		_writeLock.Dispose();
	}
}
