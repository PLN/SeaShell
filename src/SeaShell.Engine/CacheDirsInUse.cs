using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SeaShell.Engine;

/// <summary>
/// Which cache entry dirs a running process uses. Linux only (on Windows the sweep
/// detects use by the rename failing). Two sources per process:
/// <list type="bullet">
/// <item><c>/proc/PID/cmdline</c> — readable even for processes that made themselves
///   non-dumpable; a script's apphost path or <c>dotnet exec &lt;dll&gt;</c> names its entry.</item>
/// <item><c>/proc/PID/maps</c> — every mapped assembly, native library and apphost, as the
///   kernel's resolved path.</item>
/// </list>
/// Paths are matched against the cache dir as configured and as resolved by
/// <c>realpath</c>, since maps report symlink-free paths. Processes in another mount
/// namespace (containers) may see the cache under a different path and are not
/// detected; their invoker's keep-alives still protect them. Other users' processes
/// are unreadable and skipped — their scripts don't run from this user's cache.
/// </summary>
internal static class CacheDirsInUse
{
	public static HashSet<string> Find(string cacheDir)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		if (!OperatingSystem.IsLinux()) return result;

		var prefixes = new[] { Path.GetFullPath(cacheDir), RealPath(cacheDir) }
			.Where(p => p != null)
			.Select(p => p!.TrimEnd('/') + "/")
			.Distinct()
			.ToArray();

		IEnumerable<string> pids;
		try { pids = Directory.EnumerateDirectories("/proc"); }
		catch { return result; }

		foreach (var pidDir in pids)
		{
			if (!int.TryParse(Path.GetFileName(pidDir), out _)) continue;
			try
			{
				foreach (var arg in File.ReadAllText(Path.Combine(pidDir, "cmdline")).Split('\0'))
					AddIfUnder(arg, prefixes, result);
			}
			catch { } // exited meanwhile
			try
			{
				foreach (var line in File.ReadLines(Path.Combine(pidDir, "maps")))
					AddIfUnder(MapsPath(line), prefixes, result);
			}
			catch { } // exited, or not ours to read (cmdline above still covered it)
		}
		return result;
	}

	/// <summary>
	/// The pathname field of a <c>/proc/PID/maps</c> line
	/// (<c>address perms offset dev inode pathname</c>), or null for anonymous mappings.
	/// </summary>
	internal static string? MapsPath(string line)
	{
		var fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
		return fields.Length == 6 ? fields[5].Trim() : null;
	}

	internal static void AddIfUnder(string? path, string[] prefixes, HashSet<string> result)
	{
		if (string.IsNullOrEmpty(path)) return;
		foreach (var prefix in prefixes)
		{
			if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
			var rest = path.AsSpan(prefix.Length);
			var slash = rest.IndexOf('/');
			if (slash > 0) result.Add(rest[..slash].ToString());
		}
	}

	private static string? RealPath(string path)
	{
		try
		{
			var resolved = realpath(path, IntPtr.Zero);
			if (resolved == IntPtr.Zero) return null;
			try { return Marshal.PtrToStringUTF8(resolved); }
			finally { free(resolved); }
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return null; // unusual libc: match the configured path only
		}
	}

	[DllImport("libc", SetLastError = true)]
	private static extern IntPtr realpath(string path, IntPtr resolvedPath);

	[DllImport("libc")]
	private static extern void free(IntPtr ptr);
}
