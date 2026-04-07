//css_inc Mother.cs
//css_inc System.Diagnostics.Ext.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Serilog;

// ── SeaShell Pipeline: Package ───────────────────────────────────────
// Runs on Windows host only. Creates .nupkg files, injects the
// SeaShell.Service dependency, and validates package contents.
// Pushes packages to shared artifacts directory (PIPELINE_COMMON).

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

Directory.CreateDirectory(nupkgDir);
Directory.CreateDirectory(logs);

Console.WriteLine($"[package] Source:   {src}");
Console.WriteLine($"[package] NuPkg:    {nupkgDir}");

// ── Build Daemon (for shared IL DLLs used by Service package) ────────
// The Daemon is no longer a CLI dependency, so dotnet pack won't build it.
// We need its build output (pure IL, no R2R) for the shared DLLs.

var daemonCsproj = Path.Combine(src, "src", "SeaShell.Daemon", "SeaShell.Daemon.csproj");
var daemonBuildCode = DiagnosticsExt.RunProcess("dotnet",
	$"build \"{daemonCsproj}\" -c Release {versionArg}",
	src, logFile: Path.Combine(logs, "build-daemon.log"), prefix: "package");
if (daemonBuildCode != 0)
{
	Console.Error.WriteLine("[package] Daemon build FAILED");
	return daemonBuildCode;
}

// ── Pack CLI + Host ──────────────────────────────────────────────────

var packCode = DiagnosticsExt.RunProcess("dotnet", $"pack -c Release -o \"{nupkgDir}\" {versionArg}", src,
	logFile: Path.Combine(logs, "pack.log"), prefix: "package");
if (packCode != 0)
{
	Console.Error.WriteLine("[package] dotnet pack FAILED");
	return packCode;
}

// ── Pack SeaShell.Service ───────────────────────────────────────────
// Built from pkg/SeaShell.Binaries.csproj using published binaries from
// all build hosts (already in commonDir/artifacts/{rid}/).

var publishBase = Path.Combine(commonDir, "artifacts").Replace('\\', '/') + "/";
var binariesCsproj = Path.Combine(src, "pkg", "SeaShell.Binaries.csproj");

// BuildDir = plain IL build output (no R2R), used for shared cross-platform DLLs.
// The first 'dotnet pack' above built the Daemon, so its bin/Release/net10.0/ exists.
var buildDir = Path.Combine(src, "src", "SeaShell.Daemon", "bin", "Release", "net10.0").Replace('\\', '/') + "/";

Console.WriteLine($"[package] Packing SeaShell.Service from {publishBase}");
Console.WriteLine($"[package] Shared DLLs from {buildDir}");

var servicePackCode = DiagnosticsExt.RunProcess("dotnet",
	$"pack \"{binariesCsproj}\" -c Release -o \"{nupkgDir}\" {versionArg} -p:PublishDir={publishBase} -p:BuildDir={buildDir}",
	src, logFile: Path.Combine(logs, "pack-service.log"), prefix: "package");
if (servicePackCode != 0)
{
	Console.Error.WriteLine("[package] SeaShell.Service pack FAILED");
	return servicePackCode;
}

// ── Inject SeaShell.Service dependency ──────────────────────────────
// The CLI and Host packages depend on SeaShell.Service at the nuspec level
// (not in .csproj — avoids the chicken-and-egg problem since Service is
// built from the same repo). We inject it post-pack into each nupkg.

var servicePkg = Directory.GetFiles(nupkgDir, "SeaShell.Service.*.nupkg").FirstOrDefault();
if (servicePkg == null)
{
	Console.Error.WriteLine("[package] SeaShell.Service package not found — pack pkg/SeaShell.Binaries.csproj first");
	return 1;
}

// Extract service version from filename: SeaShell.Service.0.3.17.nupkg → 0.3.17
var serviceVersion = Path.GetFileNameWithoutExtension(servicePkg);
serviceVersion = serviceVersion.Substring("SeaShell.Service.".Length);

Console.WriteLine($"[package] Service version: {serviceVersion}");

foreach (var pattern in new[] { "SeaShell.0.*.nupkg", "SeaShell.Host.*.nupkg" })
{
	var pkg = Directory.GetFiles(nupkgDir, pattern).FirstOrDefault();
	if (pkg == null) continue;

	Console.WriteLine($"[package] Injecting Service dependency into {Path.GetFileName(pkg)}");
	InjectServiceDependency(pkg, serviceVersion);
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

void AssertNotContains(string nupkg, string[] unexpected)
{
	using var zip = ZipFile.OpenRead(nupkg);
	var entries = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
	foreach (var e in unexpected)
	{
		if (entries.Any(x => x.Equals(e, StringComparison.OrdinalIgnoreCase)))
		{
			Console.Error.WriteLine($"[package]   UNEXPECTED: {e} (should be in SeaShell.Service)");
			failed = true;
		}
	}
}

var toolPkg = Directory.GetFiles(nupkgDir, "SeaShell.0.*.nupkg").FirstOrDefault();
if (toolPkg == null)
{
	Console.Error.WriteLine("[package] SeaShell tool package not found in nupkg dir");
	return 1;
}

Console.WriteLine("\n[package] SeaShell (tool):");
AssertContains(toolPkg, new[] {
	"tools/net10.0/any/sea.dll",
	"tools/net10.0/any/SeaShell.Invoker.dll",
	"tools/net10.0/any/SeaShell.Protocol.dll",
	"tools/net10.0/any/SeaShell.Common.dll",
	"tools/net10.0/any/MessagePack.dll",
});

// Daemon/Elevator must NOT be in the tool package (they're in SeaShell.Service)
AssertNotContains(toolPkg, new[] {
	"tools/net10.0/any/seashell-daemon.dll",
	"tools/net10.0/any/seashell-daemon.exe",
	"tools/net10.0/any/seashell-elevator.dll",
	"tools/net10.0/any/seashell-elevator.exe",
});

// Verify Service dependency was injected
AssertNuspecHasDependency(toolPkg, "SeaShell.Service");

var hostPkg = Directory.GetFiles(nupkgDir, "SeaShell.Host.*.nupkg").FirstOrDefault();
if (hostPkg != null)
{
	Console.WriteLine("\n[package] SeaShell.Host:");
	AssertContains(hostPkg, new[] {
		"lib/net10.0/SeaShell.Host.dll",
		"build/net10.0/SeaShell.Host.targets",
		"build/net10.0/SeaShell.Script.dll",
		"build/net10.0/SeaShell.Common.dll",
		"build/net10.0/SeaShell.Protocol.dll",
		"build/net10.0/SeaShell.Invoker.dll",
		"build/net10.0/MessagePack.dll",
		"build/net10.0/MessagePack.Annotations.dll",
		"buildTransitive/net10.0/SeaShell.Host.targets",
		"buildTransitive/net10.0/SeaShell.Script.dll",
		"buildTransitive/net10.0/SeaShell.Common.dll",
		"buildTransitive/net10.0/SeaShell.Protocol.dll",
		"buildTransitive/net10.0/SeaShell.Invoker.dll",
		"buildTransitive/net10.0/MessagePack.dll",
		"buildTransitive/net10.0/MessagePack.Annotations.dll",
	});

	AssertNuspecHasDependency(hostPkg, "SeaShell.Service");
}

Console.WriteLine("\n[package] SeaShell.Service:");
AssertContains(servicePkg, new[] {
	"runtimes/win-x64/seashell-daemon.exe",
	"runtimes/win-x64/seashell-elevator.exe",
	"runtimes/linux-x64/seashell-daemon",
	"runtimes/linux-x64/seashell-elevator",
});

// Internal packages (Engine, Ipc, Protocol, Invoker, ServiceHost) are no longer published.
// Verify they were NOT produced.
foreach (var (pattern, name) in new[] {
	("SeaShell.Engine.*.nupkg", "SeaShell.Engine"),
	("SeaShell.Common.*.nupkg", "SeaShell.Common"),
	("SeaShell.Protocol.*.nupkg", "SeaShell.Protocol"),
	("SeaShell.Invoker.*.nupkg", "SeaShell.Invoker"),
	("SeaShell.ServiceHost.*.nupkg", "SeaShell.ServiceHost"),
})
{
	var pkg = Directory.GetFiles(nupkgDir, pattern).FirstOrDefault();
	if (pkg != null)
	{
		Console.Error.WriteLine($"[package] UNEXPECTED: {name} should not be packable (IsPackable=false)");
		failed = true;
	}
}

// Expected package count: SeaShell (tool) + SeaShell.Host + SeaShell.Service = 3
var expectedCount = 3;
var packageCount = Directory.GetFiles(nupkgDir, "*.nupkg").Length;
if (packageCount != expectedCount)
{
	Console.Error.WriteLine($"[package] Expected {expectedCount} packages, found {packageCount}");
	foreach (var f in Directory.GetFiles(nupkgDir, "*.nupkg"))
		Console.Error.WriteLine($"[package]   {Path.GetFileName(f)}");
	failed = true;
}

if (failed)
{
	Console.Error.WriteLine("\n[package] VALIDATION FAILED");
	return 1;
}

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

// ── Helpers ──────────────────────────────────────────────────────────

void InjectServiceDependency(string nupkgPath, string version)
{
	using var zip = ZipFile.Open(nupkgPath, ZipArchiveMode.Update);

	// Find the .nuspec entry
	var nuspecEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
	if (nuspecEntry == null)
	{
		Console.Error.WriteLine($"[package]   No .nuspec found in {Path.GetFileName(nupkgPath)}");
		failed = true;
		return;
	}

	// Parse nuspec XML
	XDocument doc;
	using (var stream = nuspecEntry.Open())
		doc = XDocument.Load(stream);

	var ns = doc.Root!.Name.Namespace;
	var metadata = doc.Root.Element(ns + "metadata");
	if (metadata == null) return;

	// Find or create <dependencies>
	var dependencies = metadata.Element(ns + "dependencies");
	if (dependencies == null)
	{
		dependencies = new XElement(ns + "dependencies");
		metadata.Add(dependencies);
	}

	// Find or create the target framework group for net10.0
	var group = dependencies.Elements(ns + "group")
		.FirstOrDefault(g => g.Attribute("targetFramework")?.Value
			?.Contains("net10.0", StringComparison.OrdinalIgnoreCase) == true);

	if (group == null)
	{
		// If no groups exist, add dependency directly (flat style)
		// If groups exist, add to the matching one or create new
		if (dependencies.Elements(ns + "group").Any())
		{
			group = new XElement(ns + "group",
				new XAttribute("targetFramework", "net10.0"));
			dependencies.Add(group);
		}
	}

	var depElement = new XElement(ns + "dependency",
		new XAttribute("id", "SeaShell.Service"),
		new XAttribute("version", $"[{version}]"),
		new XAttribute("exclude", "Build,Analyzers"));

	if (group != null)
		group.Add(depElement);
	else
		dependencies.Add(depElement);

	// Write back
	nuspecEntry.Delete();
	var newEntry = zip.CreateEntry(nuspecEntry.FullName);
	using (var stream = newEntry.Open())
		doc.Save(stream);

	Console.WriteLine($"[package]   Injected SeaShell.Service [{version}] dependency");
}

void AssertNuspecHasDependency(string nupkgPath, string dependencyId)
{
	using var zip = ZipFile.OpenRead(nupkgPath);
	var nuspecEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
	if (nuspecEntry == null)
	{
		Console.Error.WriteLine($"[package]   No .nuspec in {Path.GetFileName(nupkgPath)}");
		failed = true;
		return;
	}

	string content;
	using (var reader = new StreamReader(nuspecEntry.Open()))
		content = reader.ReadToEnd();

	if (content.Contains(dependencyId, StringComparison.OrdinalIgnoreCase))
		Console.WriteLine($"[package]   OK: {dependencyId} dependency present");
	else
	{
		Console.Error.WriteLine($"[package]   MISSING: {dependencyId} dependency not found in nuspec");
		failed = true;
	}
}
