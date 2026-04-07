using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;
using SeaShell.Ipc;
using SeaShell.Protocol;
using SeaShell.Daemon;

// ── Logging ─────────────────────────────────────────────────────────────

// Console logging: opt-in via env var or --console flag.
// NOT based on UserInteractive — that's true even under Task Scheduler.
var consoleLog = Environment.GetEnvironmentVariable("SEASHELL_CONSOLE_LOG") == "1"
	|| args.Contains("--console");

var logConfig = new LoggerConfiguration()
	.MinimumLevel.Is(consoleLog ? LogEventLevel.Debug : LogEventLevel.Information)
	.Enrich.WithProperty("Application", "SeaShell.Daemon");

if (consoleLog)
{
	logConfig.WriteTo.Console(
		outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
}

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
	logConfig.WriteTo.EventLog("SeaShell", restrictedToMinimumLevel: LogEventLevel.Information);
}
else
{
	logConfig.WriteTo.LocalSyslog("SeaShell", restrictedToMinimumLevel: LogEventLevel.Information);
}

Log.Logger = logConfig.CreateLogger();

// ── Single-instance check ───────────────────────────────────────────────

var address = TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity, TransportEndpoint.CurrentVersion);

if (await TransportClient.ProbeAsync(address))
{
	Log.Warning("Another daemon instance is already running");
	return 1;
}

// ── Idle timeout ────────────────────────────────────────────────────────

static TimeSpan ParseIdleTimeout(string value)
{
	value = value.Trim();
	if (value == "0") return TimeSpan.Zero;
	if (value.EndsWith("ms", StringComparison.Ordinal) && double.TryParse(value[..^2], out var ms)) return TimeSpan.FromMilliseconds(ms);
	if (value.EndsWith("s", StringComparison.Ordinal) && double.TryParse(value[..^1], out var s)) return TimeSpan.FromSeconds(s);
	if (value.EndsWith("m", StringComparison.Ordinal) && double.TryParse(value[..^1], out var m)) return TimeSpan.FromMinutes(m);
	if (value.EndsWith("h", StringComparison.Ordinal) && double.TryParse(value[..^1], out var h)) return TimeSpan.FromHours(h);
	if (double.TryParse(value, out var sec)) return TimeSpan.FromSeconds(sec);
	return TimeSpan.Zero;
}

var idleTimeout = TimeSpan.Zero;
var envTimeout = Environment.GetEnvironmentVariable("SEASHELL_IDLE_TIMEOUT");
if (!string.IsNullOrEmpty(envTimeout))
	idleTimeout = ParseIdleTimeout(envTimeout);

for (int i = 0; i < args.Length - 1; i++)
{
	if (args[i] == "--idle-timeout")
	{
		idleTimeout = ParseIdleTimeout(args[i + 1]);
		break;
	}
}

// ── Run ─────────────────────────────────────────────────────────────────

Log.Information("SeaShell daemon starting");

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
	e.Cancel = true;
	cts.Cancel();
};

await using var server = new DaemonServer(address) { IdleTimeout = idleTimeout };

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

try
{
	await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
	// clean shutdown
}

Log.Information("SeaShell daemon stopped");
Log.CloseAndFlush();
return 0;
