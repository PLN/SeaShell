using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Composite install behind a single UAC prompt. A non-elevated invocation
/// relaunches itself elevated and streams the child's output back into the
/// original console via a tailed log file; an elevated one installs the
/// invoking user and then LocalSystem, retargeting between the passes.
/// Which passes run, and in which order, is decided by
/// <see cref="InstallAllPlanner"/>.
/// </summary>
internal sealed class InstallAllRunner
{
	private readonly InstallTarget _target;
	private readonly BootstrapperOptions _options;

	public InstallAllRunner(InstallTarget target, BootstrapperOptions options)
	{
		_target = target;
		_options = options;
	}

	public async Task<int> RunAsync(string commandName)
	{
		var plan = InstallAllPlanner.Plan(
			Elevation.IsElevated(_target.IsWindows),
			_options.InvokerSid,
			SeaShellIdentity.Current.Token);

		if (plan.RelaunchElevated)
			return await RelaunchElevatedAsync(commandName);

		// Over-the-shoulder UAC check: if the elevated account differs from the
		// invoker, the user-profile parts would land in the wrong profile.
		if (plan.SkipUserParts)
		{
			Console.WriteLine($"  NOTE: elevated as a different account than the invoker ({_options.InvokerSid}).");
			Console.WriteLine("        Installing SYSTEM only. For the user-profile install, run");
			Console.WriteLine("        'seashell install' as that user (elevated, for the elevator task).");
		}

		foreach (var pass in plan.Passes)
		{
			// The SYSTEM pass is always announced by a blank separator line.
			if (pass == InstallPassScope.System) Console.WriteLine();

			var passTarget = pass == InstallPassScope.User
				? _target.WithIdentity(SeaShellIdentity.Current)
				: _target.WithIdentity(SeaShellIdentity.LocalSystem);

			var outcome = await new InstallRunner(passTarget, _options).RunAsync();
			if (outcome.ExitCode != 0) return outcome.ExitCode;

			if (pass != InstallPassScope.User) continue;

			// We're elevated and both tasks are registered — bring the whole chain
			// up now: daemon first, then the elevator (it connects TO the daemon,
			// retrying every 3s, so order only affects latency).
			if (!InstallAllPlanner.ShouldStartUserServiceChain(outcome.SkippedNewerVersion)) continue;

			Console.WriteLine();
			Console.WriteLine("  Starting daemon...");
			if (!DaemonTasks.TryRunDaemonTask())
				Console.WriteLine("  WARNING: could not start the daemon task");
			else if (await new InstallationVerifier(passTarget).DaemonLiveAsync("daemon"))
			{
				Console.WriteLine("  Starting elevator...");
				if (!DaemonTasks.TryRunElevatorTask())
					Console.WriteLine("  WARNING: could not start the elevator task");
				else
					await VerifyElevatorRegisteredAsync(passTarget);
			}
		}

		return 0;
	}

	private static async Task<int> RelaunchElevatedAsync(string commandName)
	{
		var exe = Environment.ProcessPath;
		if (exe == null)
		{
			Console.Error.WriteLine("seashell: cannot determine own executable path for elevation.");
			return 1;
		}

		var childLog = Path.Combine(Path.GetTempPath(), $"seashell-install-{Guid.NewGuid():N}.log");
		var psi = new ProcessStartInfo
		{
			FileName = exe,
			UseShellExecute = true,        // required for the UAC verb
			Verb = "runas",
			WindowStyle = ProcessWindowStyle.Hidden,
		};
		psi.ArgumentList.Add(commandName);
		psi.ArgumentList.Add("--all");
		psi.ArgumentList.Add("--log-file");
		psi.ArgumentList.Add(childLog);
		psi.ArgumentList.Add("--invoker-sid");
		psi.ArgumentList.Add(SeaShellIdentity.Current.Token);

		Console.WriteLine("  Requesting elevation (UAC prompt)...");
		Process child;
		try
		{
			child = Process.Start(psi)!;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			Console.Error.WriteLine("  ERROR: elevation was declined.");
			return 1;
		}

		await TailLogUntilExitAsync(childLog, child);
		return child.ExitCode;
	}

	// Stream a log file the elevated child is appending to into this console,
	// until the child exits; then drain the remainder and clean up.
	private static async Task TailLogUntilExitAsync(string logPath, Process child)
	{
		long position = 0;

		async Task DrainAsync()
		{
			if (!File.Exists(logPath)) return;
			try
			{
				using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				if (fs.Length <= position) return;
				fs.Position = position;
				using var reader = new StreamReader(fs);
				var chunk = await reader.ReadToEndAsync();
				position = fs.Length;
				Console.Write(chunk);
			}
			catch (IOException) { } // file mid-create; retry next tick
		}

		while (!child.HasExited)
		{
			await DrainAsync();
			await Task.Delay(200);
		}
		await child.WaitForExitAsync(); // settle ExitCode
		await DrainAsync();
		try { File.Delete(logPath); } catch { }
	}

	// After starting the elevator task: the elevator connects to the daemon and
	// registers; the daemon then reports its version in PingResponse — one check
	// proving daemon up + elevator up + handshake complete.
	private static async Task<bool> VerifyElevatorRegisteredAsync(InstallTarget target)
	{
		for (var i = 0; i < 30; i++)
		{
			var pipe = MarkerFile.Read(target.VersionDir, MarkerFile.DaemonPipe);
			if (!string.IsNullOrEmpty(pipe))
			{
				var ping = await DaemonIpc.PingAsync(pipe, timeoutMs: 1000);
				if (ping?.ElevatorVersion != null)
				{
					Console.WriteLine($"    elevator → OK (v{ping.ElevatorVersion}, registered with daemon)");
					return true;
				}
			}
			await Task.Delay(500);
		}
		Console.WriteLine("    elevator → WARNING (did not register with the daemon within 15s)");
		return false;
	}
}