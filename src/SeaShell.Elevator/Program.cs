using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;
using SeaShell.Elevator;

// ── Logging (same pattern as Daemon) ────────────────────────────────────

var consoleLog = Environment.GetEnvironmentVariable("SEASHELL_CONSOLE_LOG") == "1"
	|| args.Contains("--console");

var logConfig = new LoggerConfiguration()
	.MinimumLevel.Is(consoleLog ? LogEventLevel.Debug : LogEventLevel.Information)
	.Enrich.WithProperty("Application", "SeaShell.Elevator");

if (consoleLog)
{
	logConfig.WriteTo.Console(
		outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
}

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
	logConfig.WriteTo.EventLog("SeaShell", restrictedToMinimumLevel: LogEventLevel.Information);
else
	logConfig.WriteTo.LocalSyslog("SeaShell", restrictedToMinimumLevel: LogEventLevel.Information);

Log.Logger = logConfig.CreateLogger();

// ── Run ─────────────────────────────────────────────────────────────────

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
	e.Cancel = true;
	cts.Cancel();
};

_ = Task.Run(async () =>
{
	while (!cts.Token.IsCancellationRequested)
	{
		if (Environment.GetEnvironmentVariable("SEASHELL_STOP") == "1")
		{
			cts.Cancel();
			break;
		}
		await Task.Delay(500, cts.Token).ConfigureAwait(false);
	}
}, cts.Token);

var worker = new ElevatorWorker();

try
{
	await worker.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
	// clean shutdown
}

Log.Information("SeaShell elevator stopped");
Log.CloseAndFlush();
return 0;
