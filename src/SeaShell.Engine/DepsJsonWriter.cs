using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

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
		var ipcVer = GetBundledVersion(engineDir, "SeaShell.Common.dll");
		var scriptVer = GetBundledVersion(engineDir, "SeaShell.Script.dll");

		// Build targets entries
		var targets = new Dictionary<string, object>();

		// SeaShell runtime libraries — always listed as project deps so the .NET host
		// includes them in the TPA (Trusted Platform Assemblies) list. Without
		// these entries, the startup hook (SeaShell.Script) can't load and the
		// script pipe connection fails.
		// The DLLs are always physically copied to the output dir by ScriptCompiler.
		// Even when NuGet transitively provides the same packages, we keep the
		// type:"project" entries because they resolve from the app base dir (guaranteed)
		// rather than the NuGet cache (which may not be accessible under service accounts).
		targets[$"MessagePack/{msgpackVer}"] = new
		{
			runtime = new Dictionary<string, object>
			{
				["MessagePack.dll"] = new { },
				["MessagePack.Annotations.dll"] = new { }
			}
		};
		targets[$"SeaShell.Common/{ipcVer}"] = new
		{
			dependencies = new Dictionary<string, string>
			{
				["MessagePack"] = msgpackVer
			},
			runtime = new Dictionary<string, object>
			{
				["SeaShell.Common.dll"] = new { }
			}
		};
		targets[$"SeaShell.Script/{scriptVer}"] = new
		{
			dependencies = new Dictionary<string, string>
			{
				["SeaShell.Common"] = ipcVer
			},
			runtime = new Dictionary<string, object>
			{
				["SeaShell.Script.dll"] = new { }
			}
		};

		// Each NuGet package — listed as type:"project" because all DLLs are copied
		// to the output dir by ScriptCompiler. No NuGet cache probing at runtime.
		foreach (var pkg in packages)
		{
			var entry = new Dictionary<string, object>();

			// Runtime managed assets — bare filenames (copied flat to output dir)
			if (pkg.RuntimeAssets.Count > 0)
			{
				entry["runtime"] = pkg.RuntimeAssets.ToDictionary(
					a => Path.GetFileName(a.FullPath),
					a => (object)new { });
			}

			// Native assets — keep relative path (runtimes/{rid}/native/ structure
			// is preserved in the output dir by ScriptCompiler)
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

		libraries[$"SeaShell.Common/{ipcVer}"] = new
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
				type = "project",
				serviceable = false,
				sha512 = ""
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

	/// <summary>
	/// Merge SeaShell's bundled library entries into an existing (companion) deps.json.
	/// Used by CompileBinary when running a pre-compiled binary that has its own deps.json —
	/// the companion entries are preserved, and SeaShell's bundled DLLs are added so the
	/// startup hook (SeaShell.Script) and its transitive deps can load.
	/// </summary>
	public static void Merge(string companionPath, string outputPath, string engineDir)
	{
		var node = JsonNode.Parse(File.ReadAllText(companionPath))!;

		var msgpackVer = GetBundledVersion(engineDir, "MessagePack.dll");
		var ipcVer = GetBundledVersion(engineDir, "SeaShell.Common.dll");
		var scriptVer = GetBundledVersion(engineDir, "SeaShell.Script.dll");

		// Find the first (and usually only) TFM target
		var targetsNode = node["targets"]?.AsObject();
		JsonNode? tfmTargets = null;
		if (targetsNode != null)
		{
			foreach (var kv in targetsNode)
			{
				tfmTargets = kv.Value;
				break;
			}
		}

		if (tfmTargets is JsonObject tfmObj)
		{
			// Add bundled entries only if they don't already exist
			if (!HasEntry(tfmObj, "MessagePack"))
			{
				tfmObj[$"MessagePack/{msgpackVer}"] = new JsonObject
				{
					["runtime"] = new JsonObject
					{
						["MessagePack.dll"] = new JsonObject(),
						["MessagePack.Annotations.dll"] = new JsonObject()
					}
				};
			}
			if (!HasEntry(tfmObj, "SeaShell.Common"))
			{
				tfmObj[$"SeaShell.Common/{ipcVer}"] = new JsonObject
				{
					["dependencies"] = new JsonObject { ["MessagePack"] = msgpackVer },
					["runtime"] = new JsonObject { ["SeaShell.Common.dll"] = new JsonObject() }
				};
			}
			if (!HasEntry(tfmObj, "SeaShell.Script"))
			{
				tfmObj[$"SeaShell.Script/{scriptVer}"] = new JsonObject
				{
					["dependencies"] = new JsonObject { ["SeaShell.Common"] = ipcVer },
					["runtime"] = new JsonObject { ["SeaShell.Script.dll"] = new JsonObject() }
				};
			}
		}

		// Add to libraries
		var libs = node["libraries"]?.AsObject();
		if (libs != null)
		{
			if (!HasEntry(libs, "MessagePack"))
				libs[$"MessagePack/{msgpackVer}"] = new JsonObject
				{
					["type"] = "project", ["serviceable"] = false, ["sha512"] = ""
				};
			if (!HasEntry(libs, "SeaShell.Common"))
				libs[$"SeaShell.Common/{ipcVer}"] = new JsonObject
				{
					["type"] = "project", ["serviceable"] = false, ["sha512"] = ""
				};
			if (!HasEntry(libs, "SeaShell.Script"))
				libs[$"SeaShell.Script/{scriptVer}"] = new JsonObject
				{
					["type"] = "project", ["serviceable"] = false, ["sha512"] = ""
				};
		}

		var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(outputPath, json);
	}

	/// <summary>Check if a JsonObject has any key starting with "name/".</summary>
	static bool HasEntry(JsonObject obj, string name)
	{
		var prefix = name + "/";
		foreach (var kv in obj)
			if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	static string GetBundledVersion(string engineDir, string dllName)
	{
		var path = Path.Combine(engineDir, dllName);
		if (!File.Exists(path)) return "0.0.0";
		return AssemblyName.GetAssemblyName(path).Version?.ToString(3) ?? "0.0.0";
	}
}
