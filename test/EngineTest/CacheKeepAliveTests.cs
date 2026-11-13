using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Invoker;
using SeaShell.Ipc;
using SeaShell.Protocol;
using Xunit;

namespace SeaShell.Engine.Tests;

public class CacheKeepAliveTests
{
	private static CompiledScript Compiled(string dir, string? manifestDir = null) => new(
		Path.Combine(dir, "s.dll"), null, null,
		Path.Combine(manifestDir ?? dir, "s.sea.json"), null,
		DirectExe: false, Elevated: false, Watch: false, Restart: false);

	[Fact]
	public async Task KeepAlive_TouchesBeforeStart_ThenPingsTheCurrentEntry()
	{
		var address = TransportEndpoint.GetDaemonAddress($"test{Guid.NewGuid():N}", "0.0.0.1");
		await using var server = new TransportServer(address);
		server.Start();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

		var dirA = Path.Combine(Path.GetTempPath(), "entry_aaaaaaaa");
		var dirB = Path.Combine(Path.GetTempPath(), "entry_bbbbbbbb");
		var exeDir = Path.Combine(Path.GetTempPath(), "bin-elsewhere");

		// Precompiled: the first touch is awaited before StartAsync returns.
		var starting = CacheKeepAlive.StartAsync(address, Compiled(dirA), touchNow: true, TimeSpan.FromMilliseconds(100), timeout.Token);
		Assert.Equal(dirA, await NextTouchAsync(server, known: false, timeout.Token));
		var keepAlive = await starting;
		Assert.False(keepAlive.Known);

		// A binary run in place: the entry is where its staged manifest lives.
		keepAlive.Update(Compiled(exeDir, manifestDir: dirB));
		string touched;
		do touched = await NextTouchAsync(server, known: true, timeout.Token);
		while (touched == dirA);
		Assert.Equal(dirB, touched);

		await keepAlive.DisposeAsync();
	}

	[Fact]
	public async Task KeepAlive_ReloadedThroughAnotherDaemon_PingsThatDaemon()
	{
		// Reload after a daemon update: the new build is the newer daemon's cache entry
		var oldAddress = TransportEndpoint.GetDaemonAddress($"test{Guid.NewGuid():N}", "0.0.0.1");
		var newAddress = TransportEndpoint.GetDaemonAddress($"test{Guid.NewGuid():N}", "0.0.0.2");
		await using var newDaemon = new TransportServer(newAddress);
		newDaemon.Start();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

		var dirB = Path.Combine(Path.GetTempPath(), "entry_bbbbbbbb");
		await using var keepAlive = await CacheKeepAlive.StartAsync(oldAddress,
			Compiled(Path.Combine(Path.GetTempPath(), "entry_aaaaaaaa")), touchNow: false, TimeSpan.FromMilliseconds(100), timeout.Token);
		keepAlive.Update(Compiled(dirB), newAddress);

		Assert.Equal(dirB, await NextTouchAsync(newDaemon, known: true, timeout.Token));
	}

		private static async Task<string> NextTouchAsync(TransportServer server, bool known, CancellationToken ct)
	{
		await using var conn = (await server.AcceptAsync(ct))!;
		var msg = await conn.Channel.ReceiveAsync(ct);
		var touch = Assert.IsType<CacheTouchRequest>(msg!.Value.Message);
		await conn.Channel.SendAsync(new CacheTouchResponse(known), ct);
		return touch.OutputDir;
	}
}
