using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaShell.Installer;

/// <summary>Owner and permission bits of one path (Linux <c>lstat</c>).</summary>
public readonly record struct PosixStat(int Uid, int Mode);

/// <summary>The <c>dotnet</c> for the SYSTEM / root entry (null: none qualifies), and why the others were passed over.</summary>
public sealed record SystemDotnetChoice(string? Dotnet, IReadOnlyList<string> Rejected);

/// <summary>
/// Which <c>dotnet</c> the SYSTEM / root automatic-update entry may record.
/// That entry runs with the highest privileges on the machine, so everything it
/// executes must be out of reach of unprivileged users: an SDK in a user's
/// profile (<c>~/.dotnet</c>, a per-user install) could be replaced by any
/// process of that user and would then run as SYSTEM / root.
/// <list type="bullet">
/// <item>Windows: the muxer lies under <c>%ProgramFiles%</c> (writable by
///   administrators only).</item>
/// <item>Linux: the symlink-free path of the muxer, and every directory above it,
///   are owned by root and writable by neither group nor others. <c>DOTNET_ROOT</c>
///   is that muxer's directory, and <c>PATH</c> is a fixed minimal list of system
///   directories that pass the same check — never the enabling user's.</item>
/// </list>
/// When the enabling user's <c>dotnet</c> does not qualify, the machine-wide
/// installs are tried; when none qualifies (and has an SDK), the SYSTEM / root
/// entry is not created. The user's own entries keep the user's <c>dotnet</c>:
/// they run as the user (the highest-privileges user task: the same trust
/// boundary as the elevator, which also runs the user's install elevated).
/// </summary>
public static class SystemDotnet
{
	/// <summary>Exit code of <c>update --auto on</c> when the SYSTEM / root entry was refused (the user entry is on).</summary>
	public const int RefusedExitCode = 3;

	/// <summary>Machine-wide Linux locations, tried after the enabling user's dotnet (resolved through symlinks).</summary>
	public static readonly IReadOnlyList<string> LinuxMachineWide = new[]
	{
		"/usr/bin/dotnet", "/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet", "/usr/local/bin/dotnet",
	};

	/// <summary>The minimal <c>PATH</c> of root's entry, before the check (directories that do not exist are left out).</summary>
	public static readonly IReadOnlyList<string> LinuxRootPath = new[]
	{
		"/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin",
	};

	/// <summary>Candidates in order: the enabling user's dotnet, then the machine-wide install(s).</summary>
	public static IReadOnlyList<string> Candidates(string userDotnet, bool isWindows, string programFiles) =>
		new[] { userDotnet }
			.Concat(isWindows ? new[] { programFiles.TrimEnd('\\') + @"\dotnet\dotnet.exe" } : LinuxMachineWide)
			.Where(p => !string.IsNullOrEmpty(p))
			.Distinct(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
			.ToArray();

	/// <summary>
	/// The first candidate that qualifies. <paramref name="examine"/> returns the
	/// path to record (Linux: resolved through symlinks) and why it does not
	/// qualify (null: it does). <see cref="SystemDotnetChoice.Dotnet"/> is null
	/// when none does: the SYSTEM / root entry is then not created, and
	/// <see cref="SystemDotnetChoice.Rejected"/> says why for each candidate.
	/// </summary>
	public static SystemDotnetChoice Choose(IEnumerable<string> candidates, Func<string, (string Path, string? Reason)> examine)
	{
		var rejected = new List<string>();
		foreach (var candidate in candidates)
		{
			var (path, reason) = examine(candidate);
			if (reason == null) return new SystemDotnetChoice(path, rejected);
			rejected.Add(path == candidate ? $"{candidate}: {reason}" : $"{candidate} (-> {path}): {reason}");
		}
		return new SystemDotnetChoice(null, rejected);
	}

	/// <summary>Windows: why <paramref name="dotnet"/> does not qualify, or null when it lies under <paramref name="programFiles"/>.</summary>
	public static string? WindowsDisqualified(string dotnet, string programFiles)
	{
		// Plain string rules (no Path API: the same decision on every platform).
		var path = dotnet.Replace('/', '\\');
		var root = programFiles.Replace('/', '\\').TrimEnd('\\') + "\\";
		if (path.Split('\\').Any(s => s == ".." || s == ".")) return "not a normalized absolute path";
		return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
			? null
			: $"not under {programFiles} (a location an unprivileged user may be able to change)";
	}

	/// <summary>
	/// Linux: why the muxer at <paramref name="realPath"/> (symlink-free) does not
	/// qualify, or null when it and every directory above it are owned by root and
	/// not group- or world-writable. <paramref name="stat"/> returns null for a
	/// path that cannot be examined, which disqualifies it.
	/// </summary>
	public static string? LinuxDisqualified(string realPath, Func<string, PosixStat?> stat)
	{
		if (!realPath.StartsWith('/')) return "not an absolute path";
		for (var path = realPath; ; path = ParentOf(path))
		{
			if (stat(path) is not { } st) return $"{path} cannot be examined";
			if (st.Uid != 0) return $"{path} is owned by uid {st.Uid}, not root";
			if ((st.Mode & 0b000_010_010) != 0) return $"{path} is writable by {((st.Mode & 0b000_000_010) != 0 ? "others" : "its group")} (mode {Convert.ToString(st.Mode & 0xFFF, 8)})";
			if (path == "/") return null;
		}
	}

	/// <summary>
	/// Linux: root's <c>PATH</c>: <paramref name="dotnetRoot"/> (already checked)
	/// and the directories of <see cref="LinuxRootPath"/> that exist and qualify.
	/// </summary>
	public static string LinuxPathFor(string dotnetRoot, Func<string, PosixStat?> stat) =>
		string.Join(':', new[] { dotnetRoot }.Concat(LinuxRootPath.Where(d => stat(d) != null && LinuxDisqualified(d, stat) == null)).Distinct());

	private static string ParentOf(string path)
	{
		var trimmed = path.TrimEnd('/');
		var slash = trimmed.LastIndexOf('/');
		return slash <= 0 ? "/" : trimmed[..slash];
	}
}
