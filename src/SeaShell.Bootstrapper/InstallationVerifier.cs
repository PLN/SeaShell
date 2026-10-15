using System;
using System.IO;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// End-to-end verification after an install: marker contents resolve to real
/// files, the junction points where we expect, the sea binary reports our
/// version, and scheduled tasks plus file associations are wired up correctly.
/// For the SYSTEM target this includes waiting for the just-started daemon to
/// come up and answer a ping (the daemon writes its pid/pipe markers under its
/// own identity — self-reporting doubles as verification).
///
/// Each check collects its inputs and reports; the pass/fail decision itself
/// lives in <see cref="VerificationRules"/>.
/// </summary>
internal sealed class InstallationVerifier
{
	private readonly InstallTarget _target;

	public InstallationVerifier(InstallTarget target) => _target = target;

	public async Task<bool> VerifyAsync(string seaPath)
	{
		Console.WriteLine("  Verifying installation...");
		var ok = true;

		ok &= VerifyMarkerPointsToExistingFile(MarkerFile.DaemonPath, "daemon.path");
		ok &= VerifyMarkerPointsToExistingFile(MarkerFile.ElevatorPath, "elevator.path");

		var junctionTarget = JunctionHelper.ReadTarget(_target.CurrentDir);
		if (VerificationRules.JunctionPointsToVersion(junctionTarget, _target.VersionDir))
		{
			Console.WriteLine($"    current junction → OK (→ v{_target.Version})");
		}
		else
		{
			Console.Error.WriteLine($"    current junction → FAIL (target: {junctionTarget ?? "<missing>"})");
			ok = false;
		}

		var verResult = EnvironmentProbe.RunProcess(seaPath, "--version").Trim();
		if (VerificationRules.SeaReportsVersion(verResult, _target.Version))
		{
			Console.WriteLine($"    sea --version → OK ({verResult})");
		}
		else
		{
			Console.Error.WriteLine($"    sea --version → FAIL (got {verResult}, expected v{_target.Version})");
			ok = false;
		}

		if (_target.IsSystemTarget)
		{
			var sysDaemonState = DaemonTasks.QuerySystemDaemonTaskState();
			if (sysDaemonState != null)
				Console.WriteLine($"    daemon task (SYSTEM) → OK ({sysDaemonState})");
			else
				Console.Error.WriteLine("    daemon task (SYSTEM) → FAIL (not registered)");
			ok &= sysDaemonState != null;

			// The daemon was started above; wait for it to come up and answer.
			// It writes daemon.pid/daemon.pipe under its own (SYSTEM) identity at
			// startup, and the pipe ACL admits Administrators — so an elevated
			// installer can confirm the full chain end-to-end.
			ok &= await DaemonLiveAsync("daemon (SYSTEM)");
			// No elevator and no file associations under SYSTEM — nothing else to check.
		}
		else if (_target.IsWindows)
		{
			var daemonState = DaemonTasks.QueryDaemonTaskState();
			if (daemonState != null)
				Console.WriteLine($"    daemon task → OK ({daemonState})");
			else
				Console.Error.WriteLine("    daemon task → FAIL (not registered)");
			ok &= daemonState != null;

			if (Elevation.IsElevated(true))
			{
				var elevState = DaemonTasks.QueryElevatorTaskState();
				if (elevState != null)
					Console.WriteLine($"    elevator task → OK ({elevState})");
				else
					Console.Error.WriteLine("    elevator task → FAIL (not registered)");
				ok &= elevState != null;
			}

			// User-profile associations only — this branch is the user target on Windows.
			ok &= VerifyFileAssociation(".cs", seaPath);
			var seawPath = Path.Combine(_target.BinDir, "seaw.exe");
			if (File.Exists(seawPath))
				ok &= VerifyFileAssociation(".csw", seawPath);
		}

		if (!ok)
		{
			Console.Error.WriteLine();
			Console.Error.WriteLine(_target.IsSystemTarget
				? "  ERROR: Installation verification failed. Run elevated 'seashell status --system' for details."
				: "  ERROR: Installation verification failed. Run 'seashell status' for details.");
		}
		return ok;
	}

	/// <summary>
	/// Wait for the freshly-started daemon of THIS version to write its pipe
	/// marker and answer a ping. ~15s budget: marker polling is cheap; a ping
	/// against a stale pipe (same-version reinstall) fails fast and retries.
	/// </summary>
	public async Task<bool> DaemonLiveAsync(string label)
	{
		for (var i = 0; i < 30; i++)
		{
			var pipe = MarkerFile.Read(_target.VersionDir, MarkerFile.DaemonPipe);
			if (!string.IsNullOrEmpty(pipe))
			{
				var ping = await DaemonIpc.PingAsync(pipe, timeoutMs: 1000);
				if (ping != null && VerificationRules.DaemonReportsVersion(ping.Version, _target.Version))
				{
					Console.WriteLine($"    {label} → OK (pid {ping.Pid}, v{ping.Version})");
					return true;
				}
			}
			await Task.Delay(500);
		}
		Console.Error.WriteLine($"    {label} → FAIL (daemon did not come up within 15s)");
		return false;
	}

	private bool VerifyMarkerPointsToExistingFile(string marker, string label)
	{
		var path = MarkerFile.Read(_target.VersionDir, marker);
		if (!string.IsNullOrEmpty(path) && File.Exists(path))
		{
			Console.WriteLine($"    {label} → OK ({Path.GetFileName(path)})");
			return true;
		}
		Console.Error.WriteLine($"    {label} → FAIL (marker: {path ?? "<missing>"})");
		return false;
	}

	private static bool VerifyFileAssociation(string extension, string expectedExe)
	{
		// HKCU shell associations are a Windows concept; the caller only reaches
		// this for a Windows target. The guard keeps the registry access honest.
		if (!OperatingSystem.IsWindows()) return false;
		var progId = $"SeaShell_{extension.TrimStart('.').Replace(".", "_")}";
		try
		{
			using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
				$@"Software\Classes\{progId}\shell\open\command", writable: false);
			var command = key?.GetValue("")?.ToString() ?? "";
			if (VerificationRules.FileAssociationPointsTo(command, expectedExe))
			{
				Console.WriteLine($"    {extension} association → OK");
				return true;
			}
			Console.Error.WriteLine($"    {extension} association → FAIL (command: {command})");
			return false;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"    {extension} association → FAIL ({ex.Message})");
			return false;
		}
	}
}