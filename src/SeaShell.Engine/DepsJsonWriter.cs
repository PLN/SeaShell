using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SeaShell.Engine;

/// <summary>
/// Generates a .deps.json file that tells the dotnet host where to find
/// NuGet package DLLs (both managed and native, including runtime-specific assets).
///
/// This is the key innovation that replaces NugetRuntime.cs — the dotnet host
/// reads this file and sets up correct assembly probing and native library loading
/// automatically.
/// </summary>
public static class DepsJsonWriter
{
	public static void Write(
		string outputPath,
		string assemblyName,
		List<NuGetResolver.ResolvedPackage> packages)
	{
		var tfmName = $".NETCoreApp,Version=v{Environment.Version.Major}.{Environment.Version.Minor}";

		// Build targets entries
		var targets = new Dictionary<string, object>();

		// SeaShell runtime libraries — listed as project deps so dotnet exec
		// finds them in the app directory (next to the compiled script DLL)
		targets["SeaShell.Ipc/1.0.0"] = new
		{
			runtime = new Dictionary<string, object>
			{
				["SeaShell.Ipc.dll"] = new { }
			}
		};
		targets["SeaShell.Script/1.0.0"] = new
		{
			dependencies = new Dictionary<string, string>
			{
				["SeaShell.Ipc"] = "1.0.0"
			},
			runtime = new Dictionary<string, object>
			{
				["SeaShell.Script.dll"] = new { }
			}
		};

		// Each NuGet package
		foreach (var pkg in packages)
		{
			var entry = new Dictionary<string, object>();

			// Runtime managed assets
			if (pkg.RuntimeAssets.Count > 0)
			{
				entry["runtime"] = pkg.RuntimeAssets.ToDictionary(
					a => a.RelativePath,
					a => (object)new { });
			}

			// Native assets
			if (pkg.NativeAssets.Count > 0)
			{
				entry["native"] = pkg.NativeAssets.ToDictionary(
					a => a.RelativePath,
					a => (object)new { });
			}

			targets[$"{pkg.Name}/{pkg.Version}"] = entry;
		}

		// Build libraries entries
		var libraries = new Dictionary<string, object>();

		libraries["SeaShell.Ipc/1.0.0"] = new
		{
			type = "project",
			serviceable = false,
			sha512 = ""
		};

		libraries["SeaShell.Script/1.0.0"] = new
		{
			type = "project",
			serviceable = false,
			sha512 = ""
		};

		foreach (var pkg in packages)
		{
			libraries[$"{pkg.Name}/{pkg.Version}"] = new
			{
				type = "package",
				serviceable = true,
				sha512 = "",
				path = pkg.PackagePath,
				hashPath = ""
			};
		}

		var depsJson = new
		{
			runtimeTarget = new { name = tfmName, signature = "" },
			compilationOptions = new { },
			targets = new Dictionary<string, object>
			{
				[tfmName] = targets
			},
			libraries
		};

		var json = JsonSerializer.Serialize(depsJson, new JsonSerializerOptions
		{
			WriteIndented = true,
		});

		File.WriteAllText(outputPath, json);
	}
}
