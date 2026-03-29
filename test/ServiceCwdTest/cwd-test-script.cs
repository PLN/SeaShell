//sea_inc Mother.cs
//sea_nuget seashell.host
using System;
using System.IO;
using SeaShell.Host;

// CWD resolution test script.
// Verifies:
// 1. Script was found (we're running!) — proves CWD fix works
// 2. Mother.cs include resolved from %ProgramData%\cs-script\inc\ — proves
//    the Engine's IncludeResolver works under SYSTEM/root with no sea CLI
// 3. NuGet package with transitive SeaShell deps resolved and loaded — proves
//    deps.json type:"project" + type:"package" entries, bundled DLL probing,
//    and NuGet cache resolution all work for SYSTEM/root service accounts
// 4. Writes a marker file so the test harness can verify execution

var scriptDir = Path.GetDirectoryName(SeaShell.Sea.ScriptPath) ?? Environment.CurrentDirectory;
var markerDir = Path.Combine(scriptDir, "markers");
var marker = Path.Combine(markerDir, "seashell-cwd-test.marker");

// Exercise SeaShell.Host to prove the NuGet assembly (and its transitive deps:
// SeaShell.Engine, SeaShell.Script, SeaShell.Common, MessagePack) loaded at runtime.
// This is the exact scenario that fails when deps.json has orphaned type:"project"
// entries for bundled DLLs that weren't copied to the output dir.
var nugetOk = "FAIL";
try { nugetOk = typeof(ScriptHost).Assembly.GetName().Name ?? "FAIL"; }
catch (Exception ex) { nugetOk = $"FAIL:{ex.GetType().Name}"; }

Directory.CreateDirectory(markerDir);
File.WriteAllText(marker, string.Join("\n",
	$"PID={Environment.ProcessId}",
	$"CWD={Environment.CurrentDirectory}",
	$"ScriptPath={SeaShell.Sea.ScriptPath}",
	$"Machine={Environment.MachineName}",
	$"Script={Mother.ScriptName}",
	$"NuGet={nugetOk}",
	""));
Console.WriteLine($"[cwd-test] OK: Machine={Environment.MachineName}, NuGet={nugetOk}, marker at {marker}");
