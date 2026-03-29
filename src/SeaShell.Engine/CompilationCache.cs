using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SeaShell.Engine;

/// <summary>
/// Computes cache-key hashes for compiled scripts. Incorporates source content,
/// NuGet package versions, and engine identity so stale artifacts are invalidated.
/// </summary>
public static class CompilationCache
{
	// Engine identity — changes when the daemon/engine is rebuilt.
	// Included in the cache hash so stale compiled scripts are invalidated.
	private static readonly string _engineFingerprint = ComputeEngineFingerprint();

	private static string ComputeEngineFingerprint()
	{
		// Include Engine, Script, and Ipc assembly timestamps.
		// Any one changing should invalidate all cached compilations.
		var ticks = 0L;
		try
		{
			var engineDll = typeof(ScriptCompiler).Assembly.Location;
			var engineDir = !string.IsNullOrEmpty(engineDll)
				? Path.GetDirectoryName(engineDll) ?? AppContext.BaseDirectory
				: AppContext.BaseDirectory;

			if (!string.IsNullOrEmpty(engineDll) && File.Exists(engineDll))
				ticks += File.GetLastWriteTimeUtc(engineDll).Ticks;

			foreach (var name in new[] { "SeaShell.Script.dll", "SeaShell.Common.dll", "MessagePack.dll" })
			{
				var path = Path.Combine(engineDir, name);
				if (File.Exists(path))
					ticks += File.GetLastWriteTimeUtc(path).Ticks;
			}
		}
		catch { }
		return ticks.ToString();
	}

	public static string ComputeHash(List<(string Path, string Source)> sources, List<string>? nugetVersions = null)
	{
		using var sha = SHA256.Create();

		// Engine identity — rebuild invalidates all cached scripts
		var fp = Encoding.UTF8.GetBytes(_engineFingerprint);
		sha.TransformBlock(fp, 0, fp.Length, null, 0);

		// Source files
		foreach (var (path, source) in sources.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
		{
			sha.TransformBlock(Encoding.UTF8.GetBytes(path), 0, Encoding.UTF8.GetByteCount(path), null, 0);
			sha.TransformBlock(Encoding.UTF8.GetBytes(source), 0, Encoding.UTF8.GetByteCount(source), null, 0);
		}

		// Direct NuGet package versions — ensures versionless //nuget directives
		// pick up new package versions when they appear in the NuGet cache.
		// Transitive dependencies are not included (immutable by convention).
		if (nugetVersions != null)
		{
			foreach (var v in nugetVersions)
			{
				var bytes = Encoding.UTF8.GetBytes(v);
				sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
			}
		}

		sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
		return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
	}

	/// <summary>Compute cache hash for a pre-compiled binary (engine fingerprint + file content).</summary>
	public static string ComputeBinaryHash(string binaryPath)
	{
		using var sha = SHA256.Create();

		var fp = Encoding.UTF8.GetBytes(_engineFingerprint);
		sha.TransformBlock(fp, 0, fp.Length, null, 0);

		var bytes = File.ReadAllBytes(binaryPath);
		sha.TransformBlock(bytes, 0, bytes.Length, null, 0);

		sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
		return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
	}

	/// <summary>Delete all cache directories for a script (name_* pattern).</summary>
	public static void ClearScript(string cacheDir, string scriptName)
	{
		try
		{
			foreach (var dir in Directory.GetDirectories(cacheDir, $"{scriptName}_*"))
			{
				try { Directory.Delete(dir, recursive: true); }
				catch { } // locked by running process — will be cleaned up on next run
			}
		}
		catch { }
	}
}
