//css_inc Mother.cs
//css_inc System.Diagnostics.Ext.cs
//css_inc pipeline-tasks.cs
using System;
using System.Diagnostics;
using System.IO;
using Serilog;

// ── SeaShell Pipeline: Build ─────────────────────────────────────────
// Runs on each build host. Compiles and publishes platform binaries.
// Pushes publish output to shared artifacts directory for cross-platform exchange.

var src = Environment.GetEnvironmentVariable("PIPELINE_SRC")!;
var artifacts = Environment.GetEnvironmentVariable("PIPELINE_ARTIFACTS")!;
var logs = Environment.GetEnvironmentVariable("PIPELINE_LOGS")!;
var rid = Environment.GetEnvironmentVariable("PIPELINE_RID")!;
var commonDir = Environment.GetEnvironmentVariable("PIPELINE_COMMON")!;
var host = Environment.GetEnvironmentVariable("PIPELINE_HOST") ?? rid;
var pipelineRoot = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
	System.Runtime.InteropServices.OSPlatform.Windows) ? @"C:\pipeline" : "/pipeline";

Console.WriteLine($"[build] Source:    {src}");
Console.WriteLine($"[build] Artifacts: {artifacts}");
Console.WriteLine($"[build] RID:       {rid}");

Directory.CreateDirectory(artifacts);
Directory.CreateDirectory(logs);

var tasks = new TaskTracker("build", host, "seashell", pipelineRoot);

// ── Build ────────────────────────────────────────────────────────────

tasks.Run("dotnet-build", () =>
	DiagnosticsExt.RunProcess("dotnet", $"build -c Release ", src,
		logFile: Path.Combine(logs, "build.log"), prefix: "build"));

// ── Publish Daemon + Elevator ────────────────────────────────────────

var publishDir = Path.Combine(artifacts, "publish");

foreach (var project in new[] { "SeaShell.Daemon", "SeaShell.Elevator" })
{
	tasks.Run($"publish-{project}", () =>
	{
		var csproj = Path.Combine(src, "src", project, $"{project}.csproj");
		return DiagnosticsExt.RunProcess("dotnet",
			$"publish \"{csproj}\" -c Release -r {rid} --self-contained false -o \"{publishDir}\" ",
			src, logFile: Path.Combine(logs, $"publish-{project}.log"), prefix: "build");
	});
}

// ── Publish CLI (sea + seaw) ────────────────────────────────────────
// sea and seaw are the same csproj with different OutputType/AssemblyName.
// dotnet publish removes the previous project output, so we publish each
// to a temp dir and merge into the shared publishDir.

tasks.Run("publish-SeaShell.Cli", () =>
{
	var csproj = Path.Combine(src, "src", "SeaShell.Cli", "SeaShell.Cli.csproj");
	var cliTmp = Path.Combine(artifacts, "publish-cli-tmp");
	var rc = DiagnosticsExt.RunProcess("dotnet",
		$"publish \"{csproj}\" -c Release -r {rid} --self-contained false -o \"{cliTmp}\" ",
		src, logFile: Path.Combine(logs, "publish-SeaShell.Cli.log"), prefix: "build");
	if (rc != 0) return rc;

	// Copy sea.* files into shared publish dir
	foreach (var file in Directory.GetFiles(cliTmp))
		File.Copy(file, Path.Combine(publishDir, Path.GetFileName(file)), overwrite: true);
	Directory.Delete(cliTmp, true);
	return 0;
});

if (rid == "win-x64")
{
	tasks.Run("publish-seaw", () =>
	{
		var csproj = Path.Combine(src, "src", "SeaShell.Cli", "SeaShell.Cli.csproj");
		var seawTmp = Path.Combine(artifacts, "publish-seaw-tmp");
		var rc = DiagnosticsExt.RunProcess("dotnet",
			$"publish \"{csproj}\" -c Release -r {rid} --self-contained false -o \"{seawTmp}\" -p:WindowMode=true ",
			src, logFile: Path.Combine(logs, "publish-seaw.log"), prefix: "build");
		if (rc != 0) return rc;

		// Copy seaw.* files into shared publish dir (shared DLLs already present)
		foreach (var file in Directory.GetFiles(seawTmp, "seaw.*"))
			File.Copy(file, Path.Combine(publishDir, Path.GetFileName(file)), overwrite: true);
		Directory.Delete(seawTmp, true);
		return 0;
	});
}

Console.WriteLine($"[build] Publish complete → {publishDir}");

// ── Push artifacts to common ─────────────────────────────────────────

tasks.Run("push-artifacts", () =>
{
	var commonArtifactDir = Path.Combine(commonDir, "artifacts", rid);
	Console.WriteLine($"[build] Pushing artifacts → {commonArtifactDir}");
	Directory.CreateDirectory(commonArtifactDir);

	foreach (var file in Directory.GetFiles(publishDir))
		File.Copy(file, Path.Combine(commonArtifactDir, Path.GetFileName(file)), overwrite: true);

	Console.WriteLine($"[build] Pushed {Directory.GetFiles(commonArtifactDir).Length} files to common");
	return 0;
});

// ── Copy build log to common ─────────────────────────────────────────

var commonLogDir = Path.Combine(commonDir, "logs");
Directory.CreateDirectory(commonLogDir);
File.Copy(Path.Combine(logs, "build.log"), Path.Combine(commonLogDir, $"build-{host}.log"), overwrite: true);

return tasks.Finish(logs);
