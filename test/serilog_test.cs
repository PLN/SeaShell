//sea_nuget Serilog
//sea_nuget Serilog.Sinks.Console
using Serilog;

Log.Logger = new LoggerConfiguration()
	.WriteTo.Console()
	.CreateLogger();

Log.Information("Hello from SeaShell with {Package}!", "Serilog");
Log.CloseAndFlush();

return 0;
