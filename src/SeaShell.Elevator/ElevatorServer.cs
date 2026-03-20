using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SeaShell.Ipc;
using SeaShell.Protocol;

namespace SeaShell.Elevator;

/// <summary>
/// Thin elevated worker. Connects TO the Daemon (not the other way around),
/// registers itself, then sits in a loop receiving SpawnRequests.
///
/// No public pipe — the only way to reach the Elevator is through the Daemon.
/// </summary>
public sealed class ElevatorWorker
{
	private static readonly ILogger _log = Log.ForContext<ElevatorWorker>();
	private readonly bool _isElevated;

	public ElevatorWorker()
	{
		_isElevated = CheckElevation();
	}

	public async Task RunAsync(CancellationToken ct)
	{
		_log.Information("Starting (elevated={IsElevated})", _isElevated);
		if (!_isElevated)
			_log.Warning("Running without elevation — scripts will NOT be elevated");

		var daemonAddress = TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity);

		while (!ct.IsCancellationRequested)
		{
			try
			{
				await ConnectAndServeAsync(daemonAddress, ct);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_log.Warning("Connection lost: {Message}", ex.Message);
			}

			if (ct.IsCancellationRequested) break;

			_log.Information("Reconnecting in 3s...");
			await Task.Delay(3000, ct);
		}
	}

	private async Task ConnectAndServeAsync(string daemonAddress, CancellationToken ct)
	{
		_log.Debug("Connecting to daemon...");
		await using var conn = await TransportClient.ConnectAsync(daemonAddress, timeoutMs: 5000, ct);

		var hello = Envelope.Wrap(new ElevatorHello(_isElevated));
		await conn.SendAsync(hello.ToBytes(), ct);

		var ackBytes = await conn.ReceiveAsync(ct);
		if (ackBytes == null) throw new InvalidOperationException("Daemon disconnected during handshake");

		var ack = Envelope.FromBytes(ackBytes).Unwrap<ElevatorAck>();
		if (!ack.Accepted) throw new InvalidOperationException($"Daemon rejected registration: {ack.Reason}");

		_log.Information("Registered with daemon, waiting for spawn requests");

		while (!ct.IsCancellationRequested)
		{
			var bytes = await conn.ReceiveAsync(ct);
			if (bytes == null)
			{
				_log.Information("Daemon closed connection");
				break;
			}

			var envelope = Envelope.FromBytes(bytes);
			Envelope response;

			if (envelope.Type == nameof(SpawnRequest))
				response = Envelope.Wrap(await HandleSpawnAsync(envelope.Unwrap<SpawnRequest>()));
			else if (envelope.Type == nameof(StopRequest))
			{
				_log.Information("Stop requested via daemon");
				Environment.SetEnvironmentVariable("SEASHELL_STOP", "1");
				response = Envelope.Wrap(new SpawnResponse(true, 0, null));
				await conn.SendAsync(response.ToBytes(), ct);
				break;
			}
			else
			{
				response = Envelope.Wrap(new SpawnResponse(false, 0, $"Unknown message: {envelope.Type}"));
			}

			await conn.SendAsync(response.ToBytes(), ct);
		}
	}

	private async Task<SpawnResponse> HandleSpawnAsync(SpawnRequest request)
	{
		_log.Information("Spawn: {AssemblyPath}", request.AssemblyPath);

		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "dotnet",
				WorkingDirectory = request.WorkingDirectory,
				UseShellExecute = false,
				// Hidden window — the process needs a console handle to initialize,
				// but the window is invisible. Sea.Initialize immediately calls
				// FreeConsole + AttachConsole to switch to the CLI's console.
				WindowStyle = ProcessWindowStyle.Hidden,
			};
			if (request.CliPid > 0)
				psi.Environment["SEASHELL_CLI_PID"] = request.CliPid.ToString();
			psi.ArgumentList.Add("exec");
			if (request.RuntimeConfigPath != null)
			{
				psi.ArgumentList.Add("--runtimeconfig");
				psi.ArgumentList.Add(request.RuntimeConfigPath);
			}
			if (request.DepsJsonPath != null)
			{
				psi.ArgumentList.Add("--depsfile");
				psi.ArgumentList.Add(request.DepsJsonPath);
			}
			psi.ArgumentList.Add(request.AssemblyPath);
			foreach (var a in request.Args)
				psi.ArgumentList.Add(a);

			foreach (var envVar in request.EnvironmentVars)
			{
				var eq = envVar.IndexOf('=');
				if (eq > 0)
					psi.Environment[envVar[..eq]] = envVar[(eq + 1)..];
			}

			using var proc = Process.Start(psi);
			if (proc == null || proc.HasExited)
				return new SpawnResponse(false, 0, "Failed to start process");

			var pid = proc.Id;
			_log.Information("Spawned pid {ProcessId}", pid);
			return new SpawnResponse(true, pid, null);
		}
		catch (Exception ex)
		{
			_log.Error(ex, "Spawn failed");
			return new SpawnResponse(false, 0, ex.Message);
		}
	}

	private static bool CheckElevation()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			using var identity = WindowsIdentity.GetCurrent();
			var principal = new WindowsPrincipal(identity);
			return principal.IsInRole(WindowsBuiltInRole.Administrator);
		}
		return Environment.UserName == "root" || Geteuid() == 0;
	}

	[DllImport("libc", SetLastError = true)]
	private static extern uint Geteuid();
}
