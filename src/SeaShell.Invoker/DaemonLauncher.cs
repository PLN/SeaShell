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
	/// Returns true if a daemon is running.
	/// </summary>
	public static async Task<bool> EnsureRunningAsync(
		string? daemonAddress = null,
		Action<string>? log = null,
		Action<string>? verboseLog = null,
		CancellationToken ct = default)
	{
		daemonAddress ??= TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity, TransportEndpoint.CurrentVersion);

		if (await TransportClient.ProbeAsync(daemonAddress, ct))
		{
			// Daemon is running — check if it matches our staged version.
			var stagedDir = ServiceManifest.GetOrStageDaemon(DaemonManager.Version, verboseLog);
			if (stagedDir != null)
			{
				var expectedHash = DaemonManager.ComputeDirHash(stagedDir);
				if (await DaemonManager.EnsureDaemonMatchesAsync(daemonAddress, expectedHash, verboseLog))
				{
					// Daemon was stopped due to mismatch — start the new one
					if (StartDaemon(log, verboseLog) != 0)
						return false;

					for (int i = 0; i < 20; i++)
					{
						await Task.Delay(250, ct);
						if (await TransportClient.ProbeAsync(daemonAddress, ct))
							return true;
					}
					return false;
				}
			}
			return true;
		}

		// Start daemon via DaemonManager
		if (StartDaemon(log, verboseLog) != 0)
			return false;

		// Wait for daemon to become available
		for (int i = 0; i < 20; i++)
		{
			await Task.Delay(250, ct);
			if (await TransportClient.ProbeAsync(daemonAddress, ct))
				return true;
		}

		return false;
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
	/// Start the daemon. Delegates to DaemonManager.StartDaemon.
	/// </summary>
	private static int StartDaemon(Action<string>? log, Action<string>? verboseLog = null) =>
		DaemonManager.StartDaemon(log, verboseLog);
}
