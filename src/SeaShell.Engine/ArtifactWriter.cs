using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SeaShell.Engine;

/// <summary>
/// Writes compilation artifacts: .runtimeconfig.json, .sea.json manifest,
/// and helpers for locating them on disk.
/// </summary>
static class ArtifactWriter
{
	public static void WriteRuntimeConfig(string path, bool webApp)
	{
		var tfm = GetCurrentTfm();
		var version = Environment.Version.ToString(3); // e.g., "10.0.0"

		var frameworks = new List<object>
		{
			new { name = "Microsoft.NETCore.App", version }
		};
		if (webApp)
			frameworks.Add(new { name = "Microsoft.AspNetCore.App", version });

		// Point the host at the NuGet cache so it can find package DLLs
		var nugetCache = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".nuget", "packages");

		var config = new
		{
			runtimeOptions = new
			{
				tfm,
				frameworks,
				additionalProbingPaths = new[] { nugetCache },
				configProperties = new Dictionary<string, object>
				{
					["System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization"] = false,
				}
			}
		};

		var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(path, json);
	}

	public static void WriteManifest(
		string path,
		string scriptPath,
		IncludeResolver.ResolvedScript resolved,
		List<NuGetResolver.ResolvedPackage> packages)
	{
		var manifest = new
		{
			scriptPath,
			sources = resolved.Sources.Select(s => s.Path).ToArray(),
			packages = packages.ToDictionary(p => p.Name, p => p.Version),
			assemblies = packages
				.SelectMany(p => p.CompileDlls)
				.ToArray(),
		};

		var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		});

		File.WriteAllText(path, json);
	}

	public static string? FindDepsJson(string outputDir, string scriptName)
	{
		var path = Path.Combine(outputDir, $"{scriptName}.deps.json");
		return File.Exists(path) ? path : null;
	}

	public static string? FindManifest(string outputDir, string scriptName)
	{
		var path = Path.Combine(outputDir, $"{scriptName}.sea.json");
		return File.Exists(path) ? path : null;
	}

	/// <summary>
	/// Find reference assemblies in dotnet/packs/{packName}/{version}/ref/net{x.y}/
	/// These are the compilation-only assemblies (no native DLLs).
	/// </summary>
	public static string? FindRefAssemblyDir(string packName)
	{
		var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
		var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
		var packsDir = Path.Combine(dotnetRoot, "packs", packName);
		if (!Directory.Exists(packsDir)) return null;

		// Pick the latest version (sort as Version, not string — "9.0.14" < "10.0.5")
		var versionDir = Directory.GetDirectories(packsDir)
			.Where(d => Version.TryParse(Path.GetFileName(d), out _))
			.OrderByDescending(d => Version.Parse(Path.GetFileName(d)!))
			.FirstOrDefault();
		if (versionDir == null) return null;

		// Find the ref/net{major}.{minor}/ folder
		var tfm = GetCurrentTfm();
		var refDir = Path.Combine(versionDir, "ref", tfm);
		return Directory.Exists(refDir) ? refDir : null;
	}

	public static string GetCurrentTfm()
	{
		var ver = Environment.Version;
		return $"net{ver.Major}.{ver.Minor}";
	}

	public static bool IsKnownNative(string name) => name switch
	{
		"coreclr" or "clrjit" or "clrgc" or "clrgcexp" or "clretwrc" => true,
		"hostpolicy" or "mscorrc" or "mscordbi" or "mscordaccore" => true,
		"msquic" => true,
		_ => name.StartsWith("mscordaccore_") ||
		     name.StartsWith("Microsoft.DiaSymReader.Native") ||
		     name.Contains("Compression.Native")
	};
}
