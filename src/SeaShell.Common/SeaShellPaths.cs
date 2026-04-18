using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SeaShell;

/// <summary>
/// Canonical paths for SeaShell runtime data: compilation cache, staged binaries, snippets.
///
/// Per-user by default, system-wide for service accounts. SEASHELL_DATA env var overrides.
///
///   Windows user:    %LOCALAPPDATA%\seashell\
///   Windows SYSTEM:  %ProgramData%\seashell\
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
	public static string VersionDir(string version) => Path.Combine(DataDir, $"v{version}");

	/// <summary>Binaries directory for a specific version.</summary>
	public static string BinDir(string version) => Path.Combine(VersionDir(version), "bin");

	/// <summary>Compiled script cache for a specific version.</summary>
	public static string CacheDir(string version) => Path.Combine(VersionDir(version), "cache");


	private static string ResolveDataDir()
	{
		// Explicit override
		var env = Environment.GetEnvironmentVariable("SEASHELL_DATA");
		if (!string.IsNullOrEmpty(env))
			return env;

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return IsWindowsSystemAccount()
				? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "seashell")
				: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "seashell");
		}

		// Linux / macOS
		if (GetEuid() == 0)
			return "/var/lib/seashell";

		var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
		if (!string.IsNullOrEmpty(xdg))
			return Path.Combine(xdg, "seashell");

		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "seashell");
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static bool IsWindowsSystemAccount()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return false;

		try
		{
			using var identity = WindowsIdentity.GetCurrent();
			var sid = identity.User;
			if (sid == null) return false;

			// S-1-5-18 = SYSTEM, S-1-5-19 = LocalService, S-1-5-20 = NetworkService
			return sid.Value is "S-1-5-18" or "S-1-5-19" or "S-1-5-20";
		}
		catch
		{
			return false;
		}
	}

	[DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
	private static extern uint GetEuid();
}
