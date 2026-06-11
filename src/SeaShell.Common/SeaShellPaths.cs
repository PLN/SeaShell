using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SeaShell;

/// <summary>
/// Canonical paths for SeaShell runtime data: compilation cache, staged binaries, snippets.
///
/// Strictly per-user, even under SYSTEM — on Windows we always resolve to
/// <c>%LocalAppData%</c>, which for SYSTEM is
/// <c>C:\Windows\System32\config\systemprofile\AppData\Local\</c>. That keeps
/// a SYSTEM install contained to the SYSTEM user profile rather than the
/// shared <c>%ProgramData%</c>. SEASHELL_DATA env var overrides.
///
///   Windows user:    %LOCALAPPDATA%\seashell\
///   Windows SYSTEM:  %SystemRoot%\System32\config\systemprofile\AppData\Local\seashell\
///   Linux user:      $XDG_DATA_HOME/seashell/  (or ~/.local/share/seashell/)
///   Linux root:      /var/lib/seashell/
/// </summary>
public static class SeaShellPaths
{
	/// <summary>Root data directory for SeaShell runtime data.</summary>
	public static string DataDir { get; } = ResolveDataDir();

	/// <summary>Temporary snippet files for ScriptHost.RunSnippetAsync.</summary>
	public static string SnippetsDir { get; } = Path.Combine(DataDir, "snippets");

	/// <summary>Junction/symlink to the active version directory.</summary>
	public static string CurrentDir { get; } = Path.Combine(DataDir, "current");

	/// <summary>Bin directory under the <see cref="CurrentDir"/> junction (for PATH and user-scheduled tasks).</summary>
	public static string CurrentBinDir { get; } = Path.Combine(CurrentDir, "bin");

	// ── Version-specific paths ─────────────────────────────────────────

	/// <summary>Per-version root directory: <c>DataDir/v{version}/</c></summary>
	public static string VersionDir(string version) => VersionDirIn(DataDir, version);

	/// <summary>Binaries directory for a specific version.</summary>
	public static string BinDir(string version) => BinDirIn(DataDir, version);

	/// <summary>Compiled script cache for a specific version.</summary>
	public static string CacheDir(string version) => CacheDirIn(DataDir, version);

	// ── Target-rooted variants ─────────────────────────────────────────
	// Same layout under an explicit data dir — used by the installer when the
	// target identity (e.g. SeaShellIdentity.LocalSystem) is not the ambient one.

	/// <summary>Junction/symlink to the active version under an explicit data dir.</summary>
	public static string CurrentDirIn(string dataDir) => Path.Combine(dataDir, "current");

	/// <summary>Bin directory under the <c>current</c> junction of an explicit data dir.</summary>
	public static string CurrentBinDirIn(string dataDir) => Path.Combine(CurrentDirIn(dataDir), "bin");

	/// <summary>Per-version root under an explicit data dir.</summary>
	public static string VersionDirIn(string dataDir, string version) => Path.Combine(dataDir, $"v{version}");

	/// <summary>Binaries directory under an explicit data dir.</summary>
	public static string BinDirIn(string dataDir, string version) => Path.Combine(VersionDirIn(dataDir, version), "bin");

	/// <summary>Compiled script cache under an explicit data dir.</summary>
	public static string CacheDirIn(string dataDir, string version) => Path.Combine(VersionDirIn(dataDir, version), "cache");


	private static string ResolveDataDir()
	{
		// Explicit override
		var env = Environment.GetEnvironmentVariable("SEASHELL_DATA");
		if (!string.IsNullOrEmpty(env))
			return env;

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			// Always LocalAppData — SYSTEM's LocalAppData lives under its own
			// user profile at %SystemRoot%\System32\config\systemprofile\, so
			// a SYSTEM install stays contained there. No ProgramData: that's
			// shared-state territory, which is not what we want.
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "seashell");
		}

		// Linux / macOS
		if (GetEuid() == 0)
			return "/var/lib/seashell";

		var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
		if (!string.IsNullOrEmpty(xdg))
			return Path.Combine(xdg, "seashell");

		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "seashell");
	}

	[DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
	private static extern uint GetEuid();
}
