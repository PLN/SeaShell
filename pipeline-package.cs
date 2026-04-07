//css_inc Mother.cs
//css_inc System.Diagnostics.Ext.cs
//css_inc pipeline-tasks.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Serilog;

// ── SeaShell Pipeline: Package ───────────────────────────────────────
// Runs on Windows host only. Creates distribution archives and the
// SeaShell bootstrapper NuGet tool package with embedded archives.
// Version comes from Directory.Build.props (set by pipeline orchestrator).

var src = Environment.GetEnvironmentVariable("PIPELINE_SRC") ?? "";
var artifacts = Environment.GetEnvironmentVariable("PIPELINE_ARTIFACTS") ?? "";
var logs = Environment.GetEnvironmentVariable("PIPELINE_LOGS") ?? "";
var commonDir = Environment.GetEnvironmentVariable("PIPELINE_COMMON") ?? "";
var host = Environment.GetEnvironmentVariable("PIPELINE_HOST") ?? "win-x64";

Console.WriteLine($"[package] ENV PIPELINE_SRC={src}");
Console.WriteLine($"[package] ENV PIPELINE_ARTIFACTS={artifacts}");
Console.WriteLine($"[package] ENV PIPELINE_COMMON={commonDir}");

if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(artifacts) || string.IsNullOrEmpty(commonDir))
{
	Console.Error.WriteLine("[package] Missing environment variables. Must be run via host pipeline.cs");
	return 1;
}

var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
	System.Runtime.InteropServices.OSPlatform.Windows);
var pipelineRoot = isWindows ? @"C:\pipeline" : "/pipeline";
var nupkgDir = Path.Combine(artifacts, "nupkg");
var archiveDir = Path.Combine(artifacts, "dist");

Directory.CreateDirectory(nupkgDir);
Directory.CreateDirectory(archiveDir);
Directory.CreateDirectory(logs);

var tasks = new TaskTracker("package", host, "seashell", pipelineRoot);
var failed = false;

// Read version from Directory.Build.props
var propsPath = Path.Combine(src, "Directory.Build.props");
var propsContent = File.ReadAllText(propsPath);
var versionMatch = System.Text.RegularExpressions.Regex.Match(propsContent, @"<Version>([^<]+)</Version>");
var version = versionMatch.Success ? versionMatch.Groups[1].Value : "0.0.0";

Console.WriteLine($"[package] Source:   {src}");
Console.WriteLine($"[package] Version:  {version}");
Console.WriteLine($"[package] NuPkg:    {nupkgDir}");

// ── 1. Create distribution archives ─────────────────────────────────

tasks.Run("create-archives", () =>
{
	var rids = new[] { "win-x64", "linux-x64", "linux-musl-x64" };
	foreach (var archiveRid in rids)
	{
		var ridArtifacts = Path.Combine(commonDir, "artifacts", archiveRid);
		if (!Directory.Exists(ridArtifacts))
		{
			Console.WriteLine($"[package] Skipping {archiveRid} (no artifacts)");
			continue;
		}

		// RID-only name for embedded resources
		var archivePath = Path.Combine(archiveDir, $"{archiveRid}.zip");
		if (File.Exists(archivePath)) File.Delete(archivePath);

		using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
		{
			foreach (var file in Directory.GetFiles(ridArtifacts))
			{
				if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
				if (file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
				zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
			}
		}

		var sizeKb = new FileInfo(archivePath).Length / 1024;
		var fileCount = 0;
		using (var zip = ZipFile.OpenRead(archivePath))
			fileCount = zip.Entries.Count;
		Console.WriteLine($"[package] Archive: {archiveRid}.zip ({fileCount} files, {sizeKb} KB)");

		// Validate: sea + daemon must be present
		using (var zip = ZipFile.OpenRead(archivePath))
		{
			var entries = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var seaBin = archiveRid.StartsWith("win") ? "sea.exe" : "sea.dll";
			if (!entries.Contains(seaBin) && !entries.Contains("sea.dll"))
			{
				Console.Error.WriteLine($"[package] ARCHIVE MISSING: sea in {archiveRid}.zip");
				failed = true;
			}
			var daemonBin = archiveRid.StartsWith("win") ? "seashell-daemon.exe" : "seashell-daemon";
			if (!entries.Contains(daemonBin) && !entries.Contains("seashell-daemon.dll"))
			{
				Console.Error.WriteLine($"[package] ARCHIVE MISSING: daemon in {archiveRid}.zip");
				failed = true;
			}
		}
	}
	return failed ? 1 : 0;
});

if (failed)
{
	Console.Error.WriteLine("[package] ARCHIVE CREATION FAILED");
	return tasks.Finish(logs);
}

// ── 2. Copy archives into Bootstrapper for embedding ────────────────

var bootstrapperArchives = Path.Combine(src, "src", "SeaShell.Bootstrapper", "archives");
Directory.CreateDirectory(bootstrapperArchives);

foreach (var zip in Directory.GetFiles(archiveDir, "*.zip"))
	File.Copy(zip, Path.Combine(bootstrapperArchives, Path.GetFileName(zip)), overwrite: true);

Console.WriteLine($"[package] Copied {Directory.GetFiles(bootstrapperArchives, "*.zip").Length} archives to Bootstrapper");

// ── 3. Pack Bootstrapper (dotnet tool) + Host (library) ────────────

tasks.Run("pack-tool-host", () =>
	DiagnosticsExt.RunProcess("dotnet", $"pack -c Release -o \"{nupkgDir}\"", src,
		logFile: Path.Combine(logs, "pack.log"), prefix: "package"));

// ── 4. Validate packages ────────────────────────────────────────────

Console.WriteLine("\n[package] === Validating packages ===");

var toolPkg = Directory.GetFiles(nupkgDir, "SeaShell.0.*.nupkg").FirstOrDefault();
if (toolPkg != null)
{
	Console.WriteLine("\n[package] SeaShell (bootstrapper tool):");
	AssertContains(toolPkg, new[] {
		"tools/net10.0/any/seashell.dll",
	});

	// Verify archives are embedded
	using (var zip = ZipFile.OpenRead(toolPkg))
	{
		var entries = zip.Entries.Select(e => e.FullName).ToList();
		var archiveCount = entries.Count(e => e.Contains("archives/") && e.EndsWith(".zip"));
		Console.WriteLine($"[package]   Embedded archives: {archiveCount}");
		if (archiveCount < 3)
		{
			Console.Error.WriteLine($"[package]   Expected 3 embedded archives, found {archiveCount}");
			failed = true;
		}
	}
}
else
{
	Console.Error.WriteLine("[package] SeaShell bootstrapper package not found");
	failed = true;
}

var hostPkg = Directory.GetFiles(nupkgDir, "SeaShell.Host.*.nupkg").FirstOrDefault();
if (hostPkg != null)
{
	Console.WriteLine("\n[package] SeaShell.Host:");
	AssertContains(hostPkg, new[] {
		"lib/net10.0/SeaShell.Host.dll",
		"build/net10.0/SeaShell.Host.targets",
	});
}

// Verify no unexpected packages
foreach (var (pattern, name) in new[] {
	("SeaShell.Engine.*.nupkg", "SeaShell.Engine"),
	("SeaShell.Common.*.nupkg", "SeaShell.Common"),
	("SeaShell.Protocol.*.nupkg", "SeaShell.Protocol"),
	("SeaShell.Invoker.*.nupkg", "SeaShell.Invoker"),
	("SeaShell.Cli.*.nupkg", "SeaShell.Cli"),
	("SeaShell.Service.*.nupkg", "SeaShell.Service"),
})
{
	var pkg = Directory.GetFiles(nupkgDir, pattern).FirstOrDefault();
	if (pkg != null)
	{
		Console.Error.WriteLine($"[package] UNEXPECTED: {name} should not be packable");
		failed = true;
	}
}

var packageCount = Directory.GetFiles(nupkgDir, "*.nupkg").Length;
Console.WriteLine($"\n[package] Total packages: {packageCount}");

if (failed)
{
	Console.Error.WriteLine("\n[package] VALIDATION FAILED");
	return tasks.Finish(logs);
}

Console.WriteLine($"[package] All packages validated OK");

// ── 5. Copy versioned archives to dist ──────────────────────────────

foreach (var zip in Directory.GetFiles(archiveDir, "*.zip"))
{
	var rid = Path.GetFileNameWithoutExtension(zip);
	if (rid.StartsWith("seashell-")) continue; // skip already-versioned copies
	var versionedName = $"seashell-{version}-{rid}.zip";
	File.Copy(zip, Path.Combine(archiveDir, versionedName), overwrite: true);
}

// ── 6. Push to common ───────────────────────────────────────────────

tasks.Run("push-packages", () =>
{
	var commonNupkg = Path.Combine(commonDir, "artifacts", "nupkg");
	var commonDist = Path.Combine(commonDir, "artifacts", "dist");
	Console.WriteLine($"[package] Pushing packages → {commonNupkg}");
	Console.WriteLine($"[package] Pushing archives → {commonDist}");
	Directory.CreateDirectory(commonNupkg);
	Directory.CreateDirectory(commonDist);

	foreach (var file in Directory.GetFiles(nupkgDir, "*.nupkg"))
		File.Copy(file, Path.Combine(commonNupkg, Path.GetFileName(file)), overwrite: true);

	foreach (var file in Directory.GetFiles(archiveDir, "*.zip"))
		File.Copy(file, Path.Combine(commonDist, Path.GetFileName(file)), overwrite: true);

	return 0;
});

// Copy log
var commonLogDir = Path.Combine(commonDir, "logs");
Directory.CreateDirectory(commonLogDir);
var packLog = Path.Combine(logs, "pack.log");
if (File.Exists(packLog))
	File.Copy(packLog, Path.Combine(commonLogDir, $"package-{host}.log"), overwrite: true);

// Cleanup: remove the bootstrapper archives/ dir (don't leave it in source)
try { Directory.Delete(bootstrapperArchives, true); } catch { }

Console.WriteLine($"[package] Pushed {packageCount} packages + archives to common");
return tasks.Finish(logs);

// ── Helpers ──────────────────────────────────────────────────────────

void AssertContains(string nupkg, string[] expected)
{
	using var zip = ZipFile.OpenRead(nupkg);
	var entries = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
	foreach (var e in expected)
	{
		if (entries.Any(x => x.Equals(e, StringComparison.OrdinalIgnoreCase)))
			Console.WriteLine($"[package]   OK: {e}");
		else
		{
			Console.Error.WriteLine($"[package]   MISSING: {e}");
			failed = true;
		}
	}
}
