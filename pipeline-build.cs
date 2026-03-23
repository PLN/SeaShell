//css_inc Mother.cs
//css_inc System.Diagnostics.Ext.cs
using System;
using System.Diagnostics;
using System.IO;
using Serilog;

// ── SeaShell Pipeline: Build ─────────────────────────────────────────
// Runs on each build host. Compiles and publishes platform binaries.
// Pushes publish output to /pipeline/common/ for cross-platform exchange.

var src = Environment.GetEnvironmentVariable("PIPELINE_SRC")!;
var artifacts = Environment.GetEnvironmentVariable("PIPELINE_ARTIFACTS")!;
var logs = Environment.GetEnvironmentVariable("PIPELINE_LOGS")!;
var rid = Environment.GetEnvironmentVariable("PIPELINE_RID")!;
var commonDir = Environment.GetEnvironmentVariable("PIPELINE_COMMON")!;

Console.WriteLine($"[build] Source:    {src}");
Console.WriteLine($"[build] Artifacts: {artifacts}");
Console.WriteLine($"[build] RID:       {rid}");

Directory.CreateDirectory(artifacts);
Directory.CreateDirectory(logs);

// ── Build ────────────────────────────────────────────────────────────

var buildCode = DiagnosticsExt.RunProcess("dotnet", "build -c Release", src,
	logFile: Path.Combine(logs, "build.log"), prefix: "build");
if (buildCode != 0)
{
	Console.Error.WriteLine("[build] dotnet build FAILED");
	return buildCode;
}

// ── Publish CLI + Daemon + Elevator ──────────────────────────────────

var publishDir = Path.Combine(artifacts, "publish");

foreach (var project in new[] { "SeaShell.Cli", "SeaShell.Daemon", "SeaShell.Elevator" })
{
	var csproj = Path.Combine(src, "src", project, $"{project}.csproj");
	var code = DiagnosticsExt.RunProcess("dotnet",
		$"publish \"{csproj}\" -c Release -r {rid} --self-contained false -o \"{publishDir}\"",
		src, logFile: Path.Combine(logs, $"publish-{project}.log"), prefix: "build");
	if (code != 0)
	{
		Console.Error.WriteLine($"[build] publish {project} FAILED");
		return code;
	}
}

Console.WriteLine($"[build] Publish complete → {publishDir}");

// ── Push artifacts to common ─────────────────────────────────────────

var commonArtifactDir = Path.Combine(commonDir, "artifacts", rid);
Console.WriteLine($"[build] Pushing artifacts → {commonArtifactDir}");
Directory.CreateDirectory(commonArtifactDir);

foreach (var file in Directory.GetFiles(publishDir))
{
	File.Copy(file, Path.Combine(commonArtifactDir, Path.GetFileName(file)), overwrite: true);
}

Console.WriteLine($"[build] Pushed {Directory.GetFiles(commonArtifactDir).Length} files to common");

// ── Copy build log to common ─────────────────────────────────────────

var host = Environment.GetEnvironmentVariable("PIPELINE_HOST") ?? rid;
var commonLogDir = Path.Combine(commonDir, "logs");
Directory.CreateDirectory(commonLogDir);
File.Copy(Path.Combine(logs, "build.log"), Path.Combine(commonLogDir, $"build-{host}.log"), overwrite: true);

return 0;
