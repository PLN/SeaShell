using System;
using System.Text;
using System.Threading;
using System.Threading.Channels;
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

	// Set when a script instance starts: what it runs from, and the queue its own reload
	// requests (Sea.RequestReload) go to. Left in place when the instance ends: a request
	// written between instances is lost, and the next instance runs a fresh compile anyway.
	// Not set for an elevated run through the elevator, which has no reload path.
	private volatile CompiledScript? _running;
	private volatile ChannelWriter<(string?, bool)>? _reloadRequests;

	/// <summary>The compiled script the current (or last) instance runs from; null if none started yet.</summary>
	internal CompiledScript? Running => _running;

	internal void SetRunning(CompiledScript compiled, ChannelWriter<(string?, bool)> reloadRequests)
	{
		_reloadRequests = reloadRequests;
		_running = compiled;
	}

	/// <summary>
	/// Reload the running instance as if the script had called <c>Sea.RequestReload()</c>:
	/// it is told to save its state and exit, the script is recompiled, and the new build
	/// starts with that state. False when no instance has started that could be reloaded.
	/// </summary>
	internal bool RequestReload(string reason) =>
		_reloadRequests?.TryWrite((reason, false)) ?? false;

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
