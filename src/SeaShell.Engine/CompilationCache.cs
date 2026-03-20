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
static class CompilationCache
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
			if (!string.IsNullOrEmpty(engineDll) && File.Exists(engineDll))
				ticks += File.GetLastWriteTimeUtc(engineDll).Ticks;

			foreach (var name in new[] { "SeaShell.Script.dll", "SeaShell.Ipc.dll" })
			{
				var path = Path.Combine(AppContext.BaseDirectory, name);
				if (File.Exists(path))
					ticks += File.GetLastWriteTimeUtc(path).Ticks;
			}
		}
		catch { }
		return ticks.ToString();
	}

	public static string ComputeHash(
		List<(string Path, string Source)> sources,
		List<NuGetResolver.ResolvedPackage> packages)
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

		// NuGet package versions — update invalidates cache
		foreach (var pkg in packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
		{
			var pkgKey = $"{pkg.Name}@{pkg.Version}";
			sha.TransformBlock(Encoding.UTF8.GetBytes(pkgKey), 0, Encoding.UTF8.GetByteCount(pkgKey), null, 0);
		}

		sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
		return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
	}
}
