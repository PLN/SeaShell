using System;
using System.IO;
using System.Linq;

namespace SeaShell.Installer;

/// <summary>
/// Detects and removes pre-0.5.0 SeaShell state left in the data directory.
/// Pre-0.5.0 used a flat layout (<c>$DataDir/bin/</c>, <c>$DataDir/cache/</c>,
/// <c>$DataDir/daemon/</c>, <c>$DataDir/elevator/</c>, <c>$DataDir/seashell.json</c>).
/// After the 0.5.0 versioning rework, these directories shadow the new
/// <c>$DataDir/v{version}/</c> + <c>current/</c> junction layout — PATH
/// lookups and loader probing still find the legacy binaries.
///
/// <para>
/// Uses rename-then-delete so deletion tolerates locked binaries on Windows:
/// renaming a directory succeeds even if a process still holds a file handle
/// inside it, which moves the legacy path out of the way immediately. The
/// renamed <c>{name}.remove-{stamp}</c> directory can then be swept later
/// by the next install once the lock is released.
/// </para>
/// </summary>
public static class LegacyCleanup
{
	private static readonly string[] LegacyDirectories =
		new[] { "bin", "cache", "daemon", "elevator" };
	private const string LegacyJsonFile = "seashell.json";
	private const string QuarantineSuffix = ".remove-";

	/// <summary>
	/// Scan <paramref name="dataDir"/> for pre-0.5.0 state and remove it. Never
	/// touches per-version directories (<c>v*</c>) or the <c>current</c> junction.
	/// Swallows errors by design — a leftover legacy dir does not block the
	/// install from succeeding, and <c>VerifyInstallation</c> will catch any
	/// genuine blocker.
	/// </summary>
	public static void SweepFilesystem(string dataDir, Action<string>? log = null)
	{
		if (!Directory.Exists(dataDir)) return;

		// Pass 1: opportunistically finish quarantines from prior installs.
		SweepQuarantines(dataDir, log);

		// Pass 2: rename each legacy top-level directory aside and try to delete.
		var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		foreach (var name in LegacyDirectories)
		{
			var src = Path.Combine(dataDir, name);
			if (!Directory.Exists(src)) continue;

			// Skip if it's a reparse point (e.g. the `current` junction happens
			// to share a name with a legacy dir in some future layout).
			var attrs = new DirectoryInfo(src).Attributes;
			if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;

			var quarantine = Path.Combine(dataDir, $"{name}{QuarantineSuffix}{stamp}");
			try
			{
				Directory.Move(src, quarantine);
			}
			catch (Exception ex)
			{
				log?.Invoke($"Legacy {name}/ could not be renamed: {ex.Message}");
				continue;
			}

			if (TryDeleteRecursive(quarantine))
				log?.Invoke($"Removed legacy {name}/");
			else
				log?.Invoke($"Legacy {name}/ quarantined as {Path.GetFileName(quarantine)} — next install will retry");
		}

		// Pass 3: plain file (no lock concern).
		var jsonPath = Path.Combine(dataDir, LegacyJsonFile);
		if (File.Exists(jsonPath))
		{
			try
			{
				File.Delete(jsonPath);
				log?.Invoke($"Removed legacy {LegacyJsonFile}");
			}
			catch { }
		}
	}

	private static void SweepQuarantines(string dataDir, Action<string>? log)
	{
		string[] entries;
		try { entries = Directory.GetDirectories(dataDir, $"*{QuarantineSuffix}*"); }
		catch { return; }

		foreach (var entry in entries)
		{
			var name = Path.GetFileName(entry);
			// Only touch our own quarantine pattern: `{legacy}.remove-{digits}`
			var marker = name.IndexOf(QuarantineSuffix, StringComparison.Ordinal);
			if (marker <= 0) continue;
			var prefix = name[..marker];
			if (!LegacyDirectories.Contains(prefix)) continue;

			if (TryDeleteRecursive(entry))
				log?.Invoke($"Cleared deferred removal: {name}");
		}
	}

	private static bool TryDeleteRecursive(string path)
	{
		try
		{
			Directory.Delete(path, recursive: true);
			return true;
		}
		catch
		{
			return false;
		}
	}
}
