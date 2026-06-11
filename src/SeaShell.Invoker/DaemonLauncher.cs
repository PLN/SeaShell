using System;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Protocol;

namespace SeaShell.Invoker;

/// <summary>
/// Ensures a SeaShell daemon is running for the current user.
/// Used by ScriptInvoker before compilation requests.
/// </summary>
public static class DaemonLauncher
{
	/// <summary>
	/// Check if a daemon is running. If not, start one and wait for it to become available.
	/// Returns the resolved daemon address on success, or null on failure.
	/// The returned address may differ from the input if a compatible higher-version
	/// daemon is already running.
	/// </summary>
	public static async Task<string?> EnsureRunningAsync(
		string? daemonAddress = null,
		Action<string>? log = null,
		Action<string>? verboseLog = null,
		CancellationToken ct = default)
	{
		var requestedVersion = TransportEndpoint.CurrentVersion;
		daemonAddress ??= TransportEndpoint.GetDaemonAddress(
			TransportEndpoint.CurrentUserIdentity, requestedVersion);

		// 1. Probe our exact version's address
		if (await TransportClient.ProbeAsync(daemonAddress, ct))
			return daemonAddress;

		// 2. Exact-version probe failed — check for compatible running daemon
		//    (enumerate live pipes/sockets, not manifest)
		var identity = TransportEndpoint.CurrentUserIdentity;
		var requested = Version.Parse(requestedVersion);
		var endpoints = TransportEndpoint.EnumerateDaemonEndpoints(identity);
		foreach (var (version, address) in endpoints)
		{
			if (!Version.TryParse(version, out var v) || v <= requested)
				continue; // only accept strictly newer

			if (await TransportClient.ProbeAsync(address, ct))
			{
				verboseLog?.Invoke($"found compatible daemon at {address}");
				return address;
			}
		}

		// 3. No daemon found anywhere — cold start
		if (DaemonManager.StartDaemon(log, verboseLog) != 0)
			return null;

		return await PollForDaemonAsync(daemonAddress, requestedVersion, ct);
	}

	/// <summary>Check if a daemon is currently running.</summary>
	public static async Task<bool> IsRunningAsync(
		string? daemonAddress = null,
		CancellationToken ct = default)
	{
		daemonAddress ??= TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity, TransportEndpoint.CurrentVersion);
		return await TransportClient.ProbeAsync(daemonAddress, ct);
	}

	/// <summary>
	/// Poll for a daemon to become available after StartDaemon.
	/// Checks the primary address and all compatible higher-version addresses
	/// on each iteration (ProbeAsync on non-existent pipes fails fast).
	/// </summary>
	private static async Task<string?> PollForDaemonAsync(
		string primaryAddress, string requestedVersion, CancellationToken ct)
	{
		var identity = TransportEndpoint.CurrentUserIdentity;
		var requested = Version.Parse(requestedVersion);

		for (int i = 0; i < 20; i++)
		{
			await Task.Delay(250, ct);

			if (await TransportClient.ProbeAsync(primaryAddress, ct))
				return primaryAddress;

			// Also check compatible higher-version daemons
			foreach (var (version, address) in TransportEndpoint.EnumerateDaemonEndpoints(identity))
			{
				if (!Version.TryParse(version, out var v) || v <= requested)
					continue;
				if (await TransportClient.ProbeAsync(address, ct))
					return address;
			}
		}

		return null;
	}
}
