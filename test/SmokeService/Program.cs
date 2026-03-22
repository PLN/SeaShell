using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SeaShell.ServiceHost;

var managementCommands = new HashSet<string> { "install", "uninstall", "start", "stop", "status" };

// First arg is the SmokeApp path ONLY if it's not a management command or flag
var isManagement = args.Length > 0 && (args[0].StartsWith('-') || managementCommands.Contains(args[0].ToLowerInvariant()));
string smokeAppPath;
if (!isManagement && args.Length > 0)
{
	smokeAppPath = args[0];
}
else
{
	var siblingDir = Path.Combine(
		Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!,
		"SmokeApp");
	// Cross-platform: try .exe first (Windows), then bare name (Linux)
	smokeAppPath = Path.Combine(siblingDir, "SmokeApp.exe");
	if (!File.Exists(smokeAppPath))
		smokeAppPath = Path.Combine(siblingDir, "SmokeApp");
}
var serviceArgs = !isManagement && args.Length > 0 ? args[1..] : args;

// Management commands don't need SmokeApp to exist
if (!managementCommands.Contains(serviceArgs.FirstOrDefault()?.ToLowerInvariant() ?? "") && !File.Exists(smokeAppPath))
{
	Console.Error.WriteLine($"SmokeApp not found at: {smokeAppPath}");
	Console.Error.WriteLine("Publish it first: dotnet publish test/SmokeApp -r win-x64 -p:PublishSingleFile=true");
	return 1;
}

Console.WriteLine($"SmokeService: hosting {smokeAppPath}");

return await new ServiceHostBuilder()
	.ServiceName("seashell-smoke")
	.DisplayName("SeaShell Smoke Test")
	.Description("Smoke test: ServiceHost + single-file .exe + watch + RequestReload")
	.RunAssembly(smokeAppPath)
	.RunAsync(serviceArgs);
