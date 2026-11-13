using System;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Invoker;
using SeaShell.Ipc;
using SeaShell.Protocol;
using Xunit;

namespace SeaShell.Engine.Tests;

/// <summary>
/// A cancelled daemon lookup must not be taken for "no daemon running": that started a
/// daemon nobody waits for (a cancelled ScriptHost.CompileAsync, a service stopping during
/// the package update check).
/// </summary>
public class DaemonLauncherTests
{
	private static string UnusedAddress() =>
		TransportEndpoint.GetDaemonAddress($"test{Guid.NewGuid():N}", "0.0.0.1");

	[Fact]
	public async Task EnsureRunning_Cancelled_DoesNotStartDaemon()
	{
		var starts = 0;
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			DaemonLauncher.EnsureRunningAsync(UnusedAddress(), () => { starts++; return 0; }, null, cts.Token));
		Assert.Equal(0, starts);
	}

	[Fact]
	public async Task EnsureRunning_CancelledDuringProbe_DoesNotStartDaemon()
	{
		// A daemon that takes the ping and does not answer it before the caller gives up
		var address = UnusedAddress();
		await using var server = new TransportServer(address);
		server.Start();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

		var starts = 0;
		using var cts = new CancellationTokenSource();
		var lookup = DaemonLauncher.EnsureRunningAsync(address, () => { starts++; return 0; }, null, cts.Token);

		await using var conn = (await server.AcceptAsync(timeout.Token))!;
		var ping = await conn.Channel.ReceiveAsync(timeout.Token);
		Assert.IsType<PingRequest>(ping!.Value.Message);
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
		Assert.Equal(0, starts);
	}
}
