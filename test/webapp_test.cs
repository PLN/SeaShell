//sea_webapp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder();
var app = builder.Build();

app.MapGet("/", () => $"Hello from SeaShell webapp! Sea.ScriptName={Sea.ScriptName}");
app.MapGet("/info", () => new
{
	Sea.ScriptName,
	Sea.StartDir,
	Sea.IsElevated,
	Packages = Sea.Packages.Count,
});

Console.WriteLine("Starting on http://localhost:5123");
app.Run("http://localhost:5123");
