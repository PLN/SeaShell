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
		{
			// Daemon is running at our exact version — check if it matches our staged version.
			var stagedDir = ServiceManifest.GetOrStageDaemon(DaemonManager.Version, verboseLog);
			if (stagedDir != null)
			{
				var expectedHash = DaemonManager.ComputeDirHash(stagedDir);
				if (await DaemonManager.EnsureDaemonMatchesAsync(daemonAddress, expectedHash, verboseLog))
				{
					// Daemon was stopped due to mismatch — start the new one.
					// The new daemon may listen at a different version's address.
					if (StartDaemon(log, verboseLog) != 0)
						return null;

					return await PollForDaemonAsync(daemonAddress, requestedVersion, ct);
				}
			}
			return daemonAddress;
		}

		// 2. Exact-version probe failed — check manifest for compatible running daemon
		var compatibleAddresses = ServiceManifest.GetCompatibleDaemonAddresses(requestedVersion);
		foreach (var candidate in compatibleAddresses)
		{
			if (await TransportClient.ProbeAsync(candidate, ct))
			{
				verboseLog?.Invoke($"found compatible daemon at {candidate}");
				return candidate;
			}
		}

		// 3. No daemon found anywhere — cold start
		if (StartDaemon(log, verboseLog) != 0)
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
		var compatibleAddresses = ServiceManifest.GetCompatibleDaemonAddresses(requestedVersion);

		for (int i = 0; i < 20; i++)
		{
			await Task.Delay(250, ct);

			if (await TransportClient.ProbeAsync(primaryAddress, ct))
				return primaryAddress;

			foreach (var candidate in compatibleAddresses)
			{
				if (await TransportClient.ProbeAsync(candidate, ct))
					return candidate;
			}
		}

		return null;
	}

	/// <summary>
	/// Start the daemon. Delegates to DaemonManager.StartDaemon.
	/// </summary>
	private static int StartDaemon(Action<string>? log, Action<string>? verboseLog = null) =>
		DaemonManager.StartDaemon(log, verboseLog);
}
