//sea_inc Mother.cs
using System;
using System.IO;

// CWD resolution test script.
// Verifies:
// 1. Script was found (we're running!) — proves CWD fix works
// 2. Mother.cs include resolved from %ProgramData%\cs-script\inc\ — proves
//    the Engine's IncludeResolver works under SYSTEM/root with no sea CLI
// 3. Writes a marker file so the test harness can verify execution

var scriptDir = Path.GetDirectoryName(SeaShell.Sea.ScriptPath) ?? Environment.CurrentDirectory;
var markerDir = Path.Combine(scriptDir, "markers");
var marker = Path.Combine(markerDir, "seashell-cwd-test.marker");

Directory.CreateDirectory(markerDir);
File.WriteAllText(marker, string.Join("\n",
	$"PID={Environment.ProcessId}",
	$"CWD={Environment.CurrentDirectory}",
	$"ScriptPath={SeaShell.Sea.ScriptPath}",
	$"Machine={Environment.MachineName}",
	$"Script={Mother.ScriptName}",
	""));
Console.WriteLine($"[cwd-test] OK: Machine={Environment.MachineName}, marker at {marker}");
