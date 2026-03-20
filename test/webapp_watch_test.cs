//sea_webapp
//sea_watch
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

var version = "v1";

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

app.MapGet("/", () => $"Hello from {version}! (reload #{Sea.ReloadCount})");

// Graceful Kestrel shutdown on hot-swap — releases the port cleanly
Sea.Reloading += () =>
{
	Console.WriteLine($"[{version}] Reloading — stopping Kestrel...");
	app.StopAsync().Wait(TimeSpan.FromSeconds(2));
	Console.WriteLine($"[{version}] Kestrel stopped, port released");
};

Console.WriteLine($"[{version}] Starting on http://localhost:5199 (reload #{Sea.ReloadCount})");
app.Run("http://localhost:5199");
// test
// blah
