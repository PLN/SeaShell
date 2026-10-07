using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Ipc;
using Xunit;

namespace SeaShell.Engine.Tests;

public class MessageChannelTests
{
	[Fact]
	public async Task DisposeAsync_AfterCancelledSend_DoesNotFlushToStalledPeer()
	{
		// A peer that accepted the connection but never reads: writes block until cancelled.
		var channel = new MessageChannel(new StalledStream());

		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			async () => await channel.SendAsync(new PingRequest(), cts.Token));

		// Before the fix, completing the writer retried the buffered frame with no deadline.
		var dispose = channel.DisposeAsync().AsTask();
		var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10)));
		Assert.Same(dispose, finished);
		await dispose;
	}

	[Fact]
	public async Task DisposeAsync_DuringBlockedSend_UnwindsSendAndCompletes()
	{
		// Dispose races a send that is still blocked (e.g. elevator replaced mid-exchange).
		var channel = new MessageChannel(new StalledStream());
		var send = channel.SendAsync(new PingRequest()).AsTask();
		await Task.Delay(100);
		Assert.False(send.IsCompleted);

		var dispose = channel.DisposeAsync().AsTask();
		var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10)));
		Assert.Same(dispose, finished);
		await dispose;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
		await Assert.ThrowsAsync<ObjectDisposedException>(
			async () => await channel.SendAsync(new PingRequest()));
	}

	[Fact]
	public async Task DisposeAsync_DuringPendingReceive_ReturnsNullWithoutError()
	{
		var channel = new MessageChannel(new StalledStream());
		var receive = channel.ReceiveAsync().AsTask();
		await Task.Delay(100);
		Assert.False(receive.IsCompleted);

		await channel.DisposeAsync();

		Assert.Null(await receive);                     // reads like a disconnect
		Assert.Null(await channel.ReceiveAsync());       // and so does a late receive
	}

	[Fact]
	public async Task CacheTouchRequest_RoundTripsOverTheWire()
	{
		var wire = new MemoryStream();
		await using (var writer = new MessageChannel(wire, leaveOpen: true))
			await writer.SendAsync(new CacheTouchRequest("/cache/job_aaaaaaaa"));

		await using var reader = new MessageChannel(new MemoryStream(wire.ToArray()));
		var msg = await reader.ReceiveAsync();
		Assert.Equal(MessageType.CacheTouchRequest, msg!.Value.Type);
		Assert.Equal("/cache/job_aaaaaaaa", ((CacheTouchRequest)msg.Value.Message).OutputDir);
	}

	private sealed class StalledStream : Stream
	{
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
			await Task.Delay(Timeout.Infinite, ct);

		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
			Task.Delay(Timeout.Infinite, ct);

		public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
		public override void Flush() { }
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
		{
			await Task.Delay(Timeout.Infinite, ct);
			return 0;
		}

		public override int Read(byte[] buffer, int offset, int count) => 0;
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) =>
			Thread.Sleep(Timeout.Infinite);
	}
}
