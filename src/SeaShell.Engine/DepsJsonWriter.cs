using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
		List<NuGetResolver.ResolvedPackage> packages,
		string engineDir)
	{
		var tfmName = $".NETCoreApp,Version=v{Environment.Version.Major}.{Environment.Version.Minor}";

		// Read actual versions from bundled DLLs (avoids hardcoding)
		var msgpackVer = GetBundledVersion(engineDir, "MessagePack.dll");
		var ipcVer = GetBundledVersion(engineDir, "SeaShell.Ipc.dll");
		var scriptVer = GetBundledVersion(engineDir, "SeaShell.Script.dll");

		// Build targets entries
		var targets = new Dictionary<string, object>();

		// SeaShell runtime libraries — listed as project deps so the .NET host
		// includes them in the TPA (Trusted Platform Assemblies) list. Without
		// these entries, the startup hook (SeaShell.Script) can't load and the
		// script pipe connection fails.
		// The DLLs are physically copied to the output dir by ScriptCompiler;
		// the engine dir is also in additionalProbingPaths as a fallback.
		targets[$"MessagePack/{msgpackVer}"] = new
		{
			runtime = new Dictionary<string, object>
			{
				["MessagePack.dll"] = new { },
				["MessagePack.Annotations.dll"] = new { }
			}
		};
		targets[$"SeaShell.Ipc/{ipcVer}"] = new
		{
			dependencies = new Dictionary<string, string>
			{
				["MessagePack"] = msgpackVer
			},
			runtime = new Dictionary<string, object>
			{
				["SeaShell.Ipc.dll"] = new { }
			}
		};
		targets[$"SeaShell.Script/{scriptVer}"] = new
		{
			dependencies = new Dictionary<string, string>
			{
				["SeaShell.Ipc"] = ipcVer
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

		libraries[$"MessagePack/{msgpackVer}"] = new
		{
			type = "project",
			serviceable = false,
			sha512 = ""
		};

		libraries[$"SeaShell.Ipc/{ipcVer}"] = new
		{
			type = "project",
			serviceable = false,
			sha512 = ""
		};

		libraries[$"SeaShell.Script/{scriptVer}"] = new
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

	static string GetBundledVersion(string engineDir, string dllName)
	{
		var path = Path.Combine(engineDir, dllName);
		if (!File.Exists(path)) return "0.0.0";
		return AssemblyName.GetAssemblyName(path).Version?.ToString(3) ?? "0.0.0";
	}
}
