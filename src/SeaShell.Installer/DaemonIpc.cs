using System;
using System.Threading.Tasks;
using SeaShell.Ipc;
using SeaShell.Protocol;

namespace SeaShell.Installer;

/// <summary>
/// Minimal Ping/Stop helpers over the daemon transport. Duplicates a small
/// surface of <c>SeaShell.Invoker.DaemonManager</c> so the Installer assembly
/// stays free of the Invoker dependency (Invoker pulls in script-execution
/// code the installer has no business with).
/// </summary>
public static class DaemonIpc
{
	/// <summary>Ping the daemon at <paramref name="address"/> and return its response, or null on failure.</summary>
	public static async Task<PingResponse?> PingAsync(string address, int timeoutMs = 2000)
	{
		try
		{
			await using var conn = await TransportClient.ConnectAsync(address, timeoutMs);
			await conn.Channel.SendAsync(new PingRequest());
			var reply = await conn.Channel.ReceiveAsync();
			if (reply == null) return null;
			return reply.Value.Message as PingResponse;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Send StopRequest to the daemon. Returns true if the daemon acknowledged.
	/// Default timeout is generous: a daemon mid-compilation needs a moment to wind
	/// down gracefully, and escalating to task-end/hard-kill is far more disruptive.</summary>
	public static async Task<bool> StopAsync(string address, int timeoutMs = 10_000)
	{
		try
		{
			await using var conn = await TransportClient.ConnectAsync(address, timeoutMs);
			await conn.Channel.SendAsync(new StopRequest());
			await conn.Channel.ReceiveAsync();
			return true;
		}
		catch
		{
			return false;
		}
	}
}
