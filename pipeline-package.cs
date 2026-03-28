//css_inc Mother.cs
//css_inc System.Diagnostics.Ext.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Serilog;

// ── SeaShell Pipeline: Package ───────────────────────────────────────
// Runs on Windows host only. Creates .nupkg files, injects Linux
// apphosts, and validates package contents.
// Pushes packages to /pipeline/common/<project>/<id>/artifacts/nupkg/

var src = Environment.GetEnvironmentVariable("PIPELINE_SRC") ?? "";
var artifacts = Environment.GetEnvironmentVariable("PIPELINE_ARTIFACTS") ?? "";
var logs = Environment.GetEnvironmentVariable("PIPELINE_LOGS") ?? "";
var commonDir = Environment.GetEnvironmentVariable("PIPELINE_COMMON") ?? "";
var host = Environment.GetEnvironmentVariable("PIPELINE_HOST") ?? "win-x64";
var runCounter = Environment.GetEnvironmentVariable("PIPELINE_RUN") ?? "0";
var versionArg = $"-p:BuildNumber={runCounter}";

Console.WriteLine($"[package] ENV PIPELINE_SRC={src}");
Console.WriteLine($"[package] ENV PIPELINE_ARTIFACTS={artifacts}");
Console.WriteLine($"[package] ENV PIPELINE_COMMON={commonDir}");

if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(artifacts) || string.IsNullOrEmpty(commonDir))
{
	Console.Error.WriteLine("[package] Missing environment variables. Must be run via host pipeline.cs");
	return 1;
}

var nupkgDir = Path.Combine(artifacts, "nupkg");
var linuxArtifacts = Path.Combine(artifacts, "linux-x64");
var muslArtifacts = Path.Combine(artifacts, "linux-musl-x64");

Directory.CreateDirectory(nupkgDir);
Directory.CreateDirectory(logs);

Console.WriteLine($"[package] Source:   {src}");
Console.WriteLine($"[package] NuPkg:    {nupkgDir}");
Console.WriteLine($"[package] Linux:    {linuxArtifacts}");
Console.WriteLine($"[package] Musl:     {muslArtifacts}");

// ── Check Linux artifacts (host pipeline.cs already awaited them) ─────

if (!Directory.Exists(linuxArtifacts) || Directory.GetFiles(linuxArtifacts).Length == 0)
{
	Console.Error.WriteLine($"[package] Linux artifacts not found at {linuxArtifacts}");
	return 1;
}

Console.WriteLine($"[package] Linux artifacts: {Directory.GetFiles(linuxArtifacts).Length} files");

var hasMuslArtifacts = Directory.Exists(muslArtifacts) && Directory.GetFiles(muslArtifacts).Length > 0;
if (hasMuslArtifacts)
	Console.WriteLine($"[package] Musl artifacts:  {Directory.GetFiles(muslArtifacts).Length} files");
else
	Console.WriteLine("[package] Musl artifacts:  not available (skipping musl injection)");

// ── Pack ──────────────────────────────────────────────────────────────

var packCode = DiagnosticsExt.RunProcess("dotnet", $"pack -c Release -o \"{nupkgDir}\" {versionArg}", src,
	logFile: Path.Combine(logs, "pack.log"), prefix: "package");
if (packCode != 0)
{
	Console.Error.WriteLine("[package] dotnet pack FAILED");
	return packCode;
}

// ── Inject Linux apphosts ────────────────────────────────────────────

var toolPkg = Directory.GetFiles(nupkgDir, "SeaShell.0.*.nupkg").FirstOrDefault();
if (toolPkg == null)
{
	Console.Error.WriteLine("[package] SeaShell tool package not found in nupkg dir");
	return 1;
}

Console.WriteLine($"[package] Injecting Linux apphosts into {Path.GetFileName(toolPkg)}");

using (var zip = ZipFile.Open(toolPkg, ZipArchiveMode.Update))
{
	foreach (var name in new[] { "seashell-daemon", "seashell-elevator" })
	{
		var linuxBin = Path.Combine(linuxArtifacts, name);
		if (!File.Exists(linuxBin))
		{
			Console.Error.WriteLine($"[package] Linux apphost not found: {linuxBin}");
			return 1;
		}

		var entryName = $"tools/net10.0/any/{name}";
		zip.GetEntry(entryName)?.Delete();
		zip.CreateEntryFromFile(linuxBin, entryName);
		Console.WriteLine($"[package]   + {entryName}");
	}

	// ── Inject musl apphosts into runtimes/linux-musl-x64/ ──────────
	if (hasMuslArtifacts)
	{
		foreach (var name in new[] { "seashell-daemon", "seashell-elevator" })
		{
			var muslBin = Path.Combine(muslArtifacts, name);
			if (!File.Exists(muslBin))
			{
				Console.Error.WriteLine($"[package] Musl apphost not found: {muslBin}");
				return 1;
			}

			var entryName = $"runtimes/linux-musl-x64/{name}";
			zip.GetEntry(entryName)?.Delete();
			zip.CreateEntryFromFile(muslBin, entryName);
			Console.WriteLine($"[package]   + {entryName}");
		}
	}

	// Patch [Content_Types].xml for extensionless files
	var ctEntry = zip.GetEntry("[Content_Types].xml")!;
	string ct;
	using (var reader = new StreamReader(ctEntry.Open()))
		ct = reader.ReadToEnd();

	foreach (var name in new[] { "seashell-daemon", "seashell-elevator" })
	{
		var overrideTag = $"<Override PartName=\"/tools/net10.0/any/{name}\" ContentType=\"application/octet\" />";
		if (!ct.Contains($"/tools/net10.0/any/{name}\""))
			ct = ct.Replace("</Types>", $"  {overrideTag}\n</Types>");

		if (hasMuslArtifacts)
		{
			var muslOverride = $"<Override PartName=\"/runtimes/linux-musl-x64/{name}\" ContentType=\"application/octet\" />";
			if (!ct.Contains($"/runtimes/linux-musl-x64/{name}\""))
				ct = ct.Replace("</Types>", $"  {muslOverride}\n</Types>");
		}
	}

	ctEntry.Delete();
	var newEntry = zip.CreateEntry("[Content_Types].xml");
	using (var writer = new StreamWriter(newEntry.Open()))
		writer.Write(ct);
}

// ── Validate package contents ────────────────────────────────────────

Console.WriteLine("\n[package] === Validating packages ===");
var failed = false;

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

Console.WriteLine("\n[package] SeaShell (tool):");
var toolExpected = new List<string> {
	"tools/net10.0/any/sea.dll",
	"tools/net10.0/any/seashell-daemon.dll",
	"tools/net10.0/any/seashell-daemon.exe",
	"tools/net10.0/any/seashell-daemon",
	"tools/net10.0/any/seashell-elevator.exe",
	"tools/net10.0/any/seashell-elevator",
	"tools/net10.0/any/SeaShell.Engine.dll",
	"tools/net10.0/any/SeaShell.Script.dll",
	"tools/net10.0/any/SeaShell.Ipc.dll",
	"tools/net10.0/any/MessagePack.dll",
};
if (hasMuslArtifacts)
{
	toolExpected.Add("runtimes/linux-musl-x64/seashell-daemon");
	toolExpected.Add("runtimes/linux-musl-x64/seashell-elevator");
}
AssertContains(toolPkg, toolExpected.ToArray());

var hostPkg = Directory.GetFiles(nupkgDir, "SeaShell.Host.*.nupkg").FirstOrDefault();
if (hostPkg != null)
{
	Console.WriteLine("\n[package] SeaShell.Host:");
	AssertContains(hostPkg, new[] {
		"lib/net10.0/SeaShell.Host.dll",
		"build/net10.0/SeaShell.Host.targets",
		"build/net10.0/SeaShell.Script.dll",
		"build/net10.0/SeaShell.Ipc.dll",
		"build/net10.0/MessagePack.dll",
		"build/net10.0/MessagePack.Annotations.dll",
		"buildTransitive/net10.0/SeaShell.Host.targets",
		"buildTransitive/net10.0/SeaShell.Script.dll",
		"buildTransitive/net10.0/SeaShell.Ipc.dll",
		"buildTransitive/net10.0/MessagePack.dll",
		"buildTransitive/net10.0/MessagePack.Annotations.dll",
	});
}

foreach (var (pattern, name, expected) in new[] {
	("SeaShell.Engine.*.nupkg", "SeaShell.Engine", new[] {
		"lib/net10.0/SeaShell.Engine.dll",
		"lib/net10.0/SeaShell.Script.dll",
		"lib/net10.0/SeaShell.Ipc.dll",
		"lib/net10.0/MessagePack.dll",
		"lib/net10.0/MessagePack.Annotations.dll",
	}),
	("SeaShell.Ipc.*.nupkg", "SeaShell.Ipc", new[] { "lib/net10.0/SeaShell.Ipc.dll" }),
	("SeaShell.ServiceHost.*.nupkg", "SeaShell.ServiceHost", new[] { "lib/net10.0/SeaShell.ServiceHost.dll" }),
})
{
	var pkg = Directory.GetFiles(nupkgDir, pattern).FirstOrDefault();
	if (pkg != null)
	{
		Console.WriteLine($"\n[package] {name}:");
		AssertContains(pkg, expected);
	}
}

if (failed)
{
	Console.Error.WriteLine("\n[package] VALIDATION FAILED");
	return 1;
}

var packageCount = Directory.GetFiles(nupkgDir, "*.nupkg").Length;
Console.WriteLine($"\n[package] All {packageCount} packages validated OK");

// ── Push to common ───────────────────────────────────────────────────

var commonNupkg = Path.Combine(commonDir, "artifacts", "nupkg");
Console.WriteLine($"[package] Pushing packages → {commonNupkg}");
Directory.CreateDirectory(commonNupkg);

foreach (var file in Directory.GetFiles(nupkgDir, "*.nupkg"))
{
	File.Copy(file, Path.Combine(commonNupkg, Path.GetFileName(file)), overwrite: true);
}

// Copy log
var commonLogDir = Path.Combine(commonDir, "logs");
Directory.CreateDirectory(commonLogDir);
var packLog = Path.Combine(logs, "pack.log");
if (File.Exists(packLog))
	File.Copy(packLog, Path.Combine(commonLogDir, $"package-{host}.log"), overwrite: true);

Console.WriteLine($"[package] Pushed {packageCount} packages to common");
return 0;
