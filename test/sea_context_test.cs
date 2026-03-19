//sea_nuget Serilog
//sea_nuget Microsoft.Data.SqlClient

Console.WriteLine("=== SeaShell Runtime Context ===");
Console.WriteLine();
Console.WriteLine($"  Sea.ScriptPath:  {Sea.ScriptPath}");
Console.WriteLine($"  Sea.ScriptName:  {Sea.ScriptName}");
Console.WriteLine($"  Sea.ScriptDir:   {Sea.ScriptDir}");
Console.WriteLine($"  Sea.StartDir:    {Sea.StartDir}");
Console.WriteLine($"  Sea.IsElevated:  {Sea.IsElevated}");
Console.WriteLine();
Console.WriteLine($"  Sources ({Sea.Sources.Count}):");
foreach (var src in Sea.Sources)
	Console.WriteLine($"    {src}");
Console.WriteLine();
Console.WriteLine($"  Packages ({Sea.Packages.Count}):");
foreach (var (name, ver) in Sea.Packages)
	Console.WriteLine($"    {name} {ver}");
Console.WriteLine();
Console.WriteLine($"  Assemblies ({Sea.Assemblies.Count}):");
foreach (var asm in Sea.Assemblies.Take(5))
	Console.WriteLine($"    {Path.GetFileName(asm)}");
if (Sea.Assemblies.Count > 5)
	Console.WriteLine($"    ... and {Sea.Assemblies.Count - 5} more");

return 0;
