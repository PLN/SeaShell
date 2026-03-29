using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Ipc;

namespace SeaShell.Invoker;

/// <summary>
/// Bidirectional application messaging between a host process and a running script.
/// Create an instance, subscribe to <see cref="MessageReceived"/>, and pass it to
/// <see cref="ScriptInvoker.RunAsync"/> or <see cref="ScriptInvoker.ExecuteAsync"/>.
/// </summary>
public sealed class ScriptConnection
{
	/// <summary>
	/// Fires when the script sends an application message.
	/// Parameters: payload (raw bytes), topic (optional routing key).
	/// </summary>
	public event Action<byte[], string?>? MessageReceived;

	internal MessageChannel? Channel;

	/// <summary>Send a binary message to the running script.</summary>
	public async Task SendAsync(byte[] payload, string? topic = null, CancellationToken ct = default)
	{
		var ch = Channel ?? throw new InvalidOperationException("Script is not connected");
		await ch.SendAsync(new HostMessage(payload, topic), ct);
	}

	/// <summary>Send a string message (UTF-8) to the running script.</summary>
	public async Task SendAsync(string payload, string? topic = null, CancellationToken ct = default) =>
		await SendAsync(Encoding.UTF8.GetBytes(payload), topic, ct);

	/// <summary>Send a ScriptStop message to gracefully shut down the script.</summary>
	public async Task StopAsync(CancellationToken ct = default)
	{
		var ch = Channel ?? throw new InvalidOperationException("Script is not connected");
		await ch.SendAsync(new ScriptStop(), ct);
	}

	internal void RaiseMessageReceived(byte[] payload, string? topic) =>
		MessageReceived?.Invoke(payload, topic);
}
