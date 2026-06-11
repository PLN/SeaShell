using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SeaShell;

/// <summary>
/// An explicit identity for install and IPC addressing. Identity-derived values
/// (IPC address tokens, data directories, task principals) are computed from an
/// instance of this type — never from ambient <see cref="Environment.UserName"/>,
/// whose value is unpredictable under LocalSystem (.NET 10 returns
/// <c>{MachineName}$</c>) and locale-dependent for well-known accounts.
///
/// The token is canonical and stable: the lowercase Windows SID string
/// (<c>s-1-5-18</c> for SYSTEM) or the numeric Unix uid. Crucially, both are
/// computable for a TARGET identity from a different security context — an
/// elevated administrator can derive every value of a SYSTEM install without
/// any code running as SYSTEM.
/// </summary>
public sealed class SeaShellIdentity
{
	/// <summary>
	/// Canonical identity token used in pipe/socket names.
	/// Lowercase SID string (Windows) or numeric uid (Unix).
	/// </summary>
	public string Token { get; }

	/// <summary>
	/// Pre-0.6.0 identity token (lowercase <see cref="Environment.UserName"/>;
	/// <c>{machinename}$</c> for LocalSystem on .NET 10). Used only to recognize
	/// endpoints and artifacts created by older versions during migration.
	/// </summary>
	public string LegacyToken { get; }

	/// <summary>Human-readable name for task names and log output.</summary>
	public string DisplayName { get; }

	/// <summary>Root data directory for this identity (the <c>seashell</c> dir itself).</summary>
	public string DataDir { get; }

	/// <summary>Windows LocalSystem (S-1-5-18) or Unix root.</summary>
	public bool IsSystem { get; }

	private SeaShellIdentity(string token, string legacyToken, string displayName, string dataDir, bool isSystem)
	{
		Token = token;
		LegacyToken = legacyToken;
		DisplayName = displayName;
		DataDir = dataDir;
		IsSystem = isSystem;
	}

	/// <summary>True when an endpoint identity token belongs to this identity —
	/// matches both the canonical token and the legacy username-based token.</summary>
	public bool Matches(string endpointToken) =>
		string.Equals(endpointToken, Token, StringComparison.Ordinal)
		|| string.Equals(endpointToken, LegacyToken, StringComparison.Ordinal);

	/// <summary>The identity of the current process.</summary>
	public static SeaShellIdentity Current { get; } = ResolveCurrent();

	private static SeaShellIdentity? s_localSystem;

	/// <summary>
	/// The Windows LocalSystem account as an install TARGET, computed without
	/// running as SYSTEM: well-known SID <c>S-1-5-18</c>, data dir under the
	/// <c>systemprofile</c> user profile (which is what
	/// <c>%LocalAppData%</c> resolves to for a process running as SYSTEM).
	/// Windows only.
	/// </summary>
	public static SeaShellIdentity LocalSystem =>
		s_localSystem ??= ResolveLocalSystem();

	private static SeaShellIdentity ResolveCurrent()
	{
		var legacy = Environment.UserName.ToLowerInvariant();

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			string? sid = null;
			try
			{
				using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
				sid = id.User?.Value;
			}
			catch { }

			var token = sid?.ToLowerInvariant() ?? legacy;
			var isSystem = string.Equals(sid, "S-1-5-18", StringComparison.OrdinalIgnoreCase);
			return new SeaShellIdentity(
				token, legacy,
				isSystem ? "SYSTEM" : Environment.UserName,
				SeaShellPaths.DataDir, isSystem);
		}

		var euid = GetEuid();
		return new SeaShellIdentity(
			euid.ToString(), legacy, Environment.UserName,
			SeaShellPaths.DataDir, euid == 0);
	}

	private static SeaShellIdentity ResolveLocalSystem()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			throw new PlatformNotSupportedException("SeaShellIdentity.LocalSystem is Windows-only.");

		if (Current.IsSystem)
			return Current; // already SYSTEM — ambient resolution is authoritative

		var dataDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.Windows),
			"System32", "config", "systemprofile", "AppData", "Local", "seashell");

		return new SeaShellIdentity(
			token: "s-1-5-18",
			// .NET 10 Environment.UserName under LocalSystem is "{MachineName}$".
			legacyToken: $"{Environment.MachineName.ToLowerInvariant()}$",
			displayName: "SYSTEM",
			dataDir: dataDir,
			isSystem: true);
	}

	[DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
	private static extern uint GetEuid();
}
