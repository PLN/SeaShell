using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using MessagePack.Resolvers;

namespace SeaShell.Ipc;

/// <summary>
/// Bidirectional binary message channel over a stream, using System.IO.Pipelines
/// for efficient buffered I/O and MessagePack for serialization.
///
/// Wire format: [4-byte LE length][1-byte MessageType tag][MessagePack payload]
///
/// Supports concurrent send + receive (PipeReader and PipeWriter are independent).
/// A write lock prevents interleaved sends from concurrent callers; a read lock lets
/// DisposeAsync wait for an in-flight receive. Awaits use ConfigureAwait(false):
/// script code calls this synchronously (GetAwaiter().GetResult()), possibly under a
/// single-threaded synchronization context.
/// </summary>
public sealed class MessageChannel : IAsyncDisposable
{
	private readonly PipeReader _reader;
	private readonly PipeWriter _writer;
	private readonly Stream? _stream;
	private readonly bool _leaveOpen;
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly SemaphoreSlim _readLock = new(1, 1);
	private readonly CancellationTokenSource _disposeCts = new();
	private volatile bool _sendFaulted;
	private int _disposed;

	private const int HeaderSize = 4; // length prefix
	private const int TagSize = 1;    // MessageType byte
	private const uint MaxMessageSize = 4 * 1024 * 1024; // 4 MB

	private static readonly MessagePackSerializerOptions MsgPackOpts =
		MessagePackSerializerOptions.Standard
			.WithResolver(ContractlessStandardResolver.Instance)
			.WithSecurity(MessagePackSecurity.UntrustedData);

	public MessageChannel(Stream stream, bool leaveOpen = false)
	{
		_stream = stream;
		_leaveOpen = leaveOpen;
		_reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
		_writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
	}

	/// <summary>Send a typed message as a length-prefixed binary frame.</summary>
	public async ValueTask SendAsync<T>(T message, CancellationToken ct = default) where T : notnull
	{
		var tag = MessageTypeMap.GetTag<T>();
		var payload = MessagePackSerializer.Serialize(message, MsgPackOpts, ct);
		await WriteFrameAsync(tag, payload, ct).ConfigureAwait(false);
	}

	/// <summary>Send a message using a runtime type (for forwarding).</summary>
	public async ValueTask SendAsync(MessageType tag, object message, CancellationToken ct = default)
	{
		var clrType = MessageTypeMap.GetClrType(tag);
		var payload = MessagePackSerializer.Serialize(clrType, message, MsgPackOpts, ct);
		await WriteFrameAsync(tag, payload, ct).ConfigureAwait(false);
	}

	private async ValueTask WriteFrameAsync(MessageType tag, byte[] payload, CancellationToken ct)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

		// DisposeAsync cancels this to unwind a send blocked on a peer that stopped reading.
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
		await _writeLock.WaitAsync(linked.Token).ConfigureAwait(false);
		try
		{
			var frameLength = TagSize + payload.Length;

			// Write header (4-byte LE length of tag + payload)
			var header = _writer.GetMemory(HeaderSize);
			BinaryPrimitives.WriteUInt32LittleEndian(header.Span, (uint)frameLength);
			_writer.Advance(HeaderSize);

			// Write type tag (1 byte)
			var tagMem = _writer.GetMemory(TagSize);
			tagMem.Span[0] = (byte)tag;
			_writer.Advance(TagSize);

			// Write MessagePack payload
			var dest = _writer.GetMemory(payload.Length);
			payload.CopyTo(dest);
			_writer.Advance(payload.Length);

			await _writer.FlushAsync(linked.Token).ConfigureAwait(false);
		}
		catch
		{
			// A cancelled or failed flush can leave a partial frame buffered for a
			// peer that isn't reading. Completing the writer normally would retry
			// that write with no deadline, so DisposeAsync discards it instead.
			_sendFaulted = true;
			throw;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>
	/// Receive the next message. Returns null on clean disconnect.
	/// The returned object is the deserialized message, cast based on MessageType.
	/// </summary>
	public async ValueTask<(MessageType Type, object Message)?> ReceiveAsync(CancellationToken ct = default)
	{
		if (Volatile.Read(ref _disposed) != 0) return null;

		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
		try
		{
			await _readLock.WaitAsync(linked.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested && !ct.IsCancellationRequested)
		{
			return null; // disposed while waiting: same as a disconnect
		}

		try
		{
			// Disposal cancels a pending read via CancelPendingRead (→ null), then
			// waits for this lock before completing the reader under us.
			if (Volatile.Read(ref _disposed) != 0) return null;
			return await ReceiveCoreAsync(ct).ConfigureAwait(false);
		}
		finally
		{
			_readLock.Release();
		}
	}

	private async ValueTask<(MessageType Type, object Message)?> ReceiveCoreAsync(CancellationToken ct)
	{
		uint frameLength = 0;
		bool headerRead = false;

		while (true)
		{
			var result = await _reader.ReadAsync(ct).ConfigureAwait(false);
			var buffer = result.Buffer;

			if (result.IsCanceled)
				return null;

			if (!headerRead)
			{
				if (buffer.Length < HeaderSize)
				{
					if (result.IsCompleted)
						return null;
					_reader.AdvanceTo(buffer.Start, buffer.End);
					continue;
				}

				Span<byte> headerBytes = stackalloc byte[HeaderSize];
				buffer.Slice(0, HeaderSize).CopyTo(headerBytes);
				frameLength = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes);

				if (frameLength > MaxMessageSize)
					throw new InvalidOperationException($"Message too large: {frameLength} bytes");
				if (frameLength < TagSize)
					throw new InvalidOperationException("Message frame too small for type tag");

				headerRead = true;
			}

			var totalNeeded = HeaderSize + frameLength;
			if (buffer.Length < totalNeeded)
			{
				if (result.IsCompleted)
					return null;
				_reader.AdvanceTo(buffer.Start, buffer.End);
				continue;
			}

			// Read type tag
			var tagSlice = buffer.Slice(HeaderSize, TagSize);
			Span<byte> tagByte = stackalloc byte[1];
			tagSlice.CopyTo(tagByte);
			var messageType = (MessageType)tagByte[0];

			// Read MessagePack payload
			var payloadLength = (int)frameLength - TagSize;
			var payloadSlice = buffer.Slice(HeaderSize + TagSize, payloadLength);

			object message;
			if (MessageTypeMap.TryGetClrType(messageType, out var clrType))
			{
				ReadOnlyMemory<byte> payloadMemory;
				if (payloadSlice.IsSingleSegment)
				{
					payloadMemory = payloadSlice.First;
				}
				else
				{
					payloadMemory = payloadSlice.ToArray();
				}

				message = MessagePackSerializer.Deserialize(clrType, payloadMemory, MsgPackOpts, ct)
					?? throw new InvalidOperationException($"MessagePack deserialization returned null for {messageType}");
			}
			else
			{
				// Unknown type — return raw bytes so caller can decide
				message = payloadSlice.ToArray();
			}

			_reader.AdvanceTo(buffer.GetPosition(totalNeeded));
			return (messageType, message);
		}
	}

	/// <summary>Receive and cast to the expected type. Returns default if disconnected.</summary>
	public async ValueTask<T?> ReceiveAsync<T>(CancellationToken ct = default) where T : class
	{
		var result = await ReceiveAsync(ct).ConfigureAwait(false);
		return result?.Message as T;
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

		// Unwind in-flight I/O first: a pending read returns null (disconnect), a
		// blocked send is cancelled and marks the channel faulted.
		_disposeCts.Cancel();
		_reader.CancelPendingRead();

		// Reader and writer must be idle before they are completed — completing under
		// an active read or flush releases buffers that operation still uses. The locks
		// are never released again: late sends throw, late receives return null.
		var readerIdle = await _readLock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		var writerIdle = await _writeLock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		try
		{
			if (readerIdle)
				await _reader.CompleteAsync().ConfigureAwait(false);
			// Completing with an exception skips flushing a partial frame left by a
			// failed send. If a send ignored cancellation, closing the stream below
			// is what unblocks it.
			if (writerIdle)
				await _writer.CompleteAsync(_sendFaulted ? new OperationCanceledException("Send aborted") : null).ConfigureAwait(false);
		}
		finally
		{
			if (!_leaveOpen && _stream != null)
				await _stream.DisposeAsync().ConfigureAwait(false);
		}
	}
}
