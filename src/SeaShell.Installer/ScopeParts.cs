using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaShell.Installer;

/// <summary>
/// The parts an identity's install consists of. <see cref="User"/> is always
/// part of a user's scope; <see cref="Elevator"/> exists on Windows only;
/// <see cref="System"/> is the Windows LocalSystem install or the Linux root
/// install.
/// </summary>
[Flags]
public enum ScopeParts
{
	None = 0,

	/// <summary>The invoking user's install (binaries, PATH, daemon task, associations).</summary>
	User = 1,

	/// <summary>The user's elevator task (Windows, registered elevated).</summary>
	Elevator = 2,

	/// <summary>Windows LocalSystem install, or the Linux root install.</summary>
	System = 4,
}

/// <summary>
/// Text form of <see cref="ScopeParts"/>: the tokens of <c>install.scope</c>
/// (one per line) and of the internal <c>--pass</c> / <c>--detect</c> lists
/// (comma-separated). Shared contract between bootstrapper versions, so
/// unknown tokens in the file are ignored rather than rejected.
/// </summary>
public static class ScopeTokens
{
	public const string User = "user";
	public const string Elevator = "elevator";
	public const string System = "system";

	private static readonly (ScopeParts Part, string Token)[] s_order =
	{
		(ScopeParts.User, User),
		(ScopeParts.Elevator, Elevator),
		(ScopeParts.System, System),
	};

	/// <summary>Everything the platform supports (what <c>--all</c> means).</summary>
	public static ScopeParts PlatformAll(bool isWindows) =>
		isWindows
			? ScopeParts.User | ScopeParts.Elevator | ScopeParts.System
			: ScopeParts.User | ScopeParts.System;

	/// <summary>Parts that need elevation (UAC on Windows, sudo on Linux) to install.</summary>
	public static ScopeParts NeedingElevation => ScopeParts.Elevator | ScopeParts.System;

	/// <summary>Tokens of the set parts, in canonical order.</summary>
	public static IReadOnlyList<string> ToTokens(ScopeParts parts) =>
		s_order.Where(p => (parts & p.Part) != 0).Select(p => p.Token).ToArray();

	/// <summary>Comma-separated list (<c>--pass</c> form); empty string for none.</summary>
	public static string FormatList(ScopeParts parts) => string.Join(',', ToTokens(parts));

	/// <summary>Human-readable list for output (<c>user, elevator</c>); "none" when empty.</summary>
	public static string Describe(ScopeParts parts) =>
		parts == ScopeParts.None ? "none" : string.Join(", ", ToTokens(parts));

	/// <summary>
	/// Parse a comma-separated <c>--pass</c> / <c>--detect</c> value. Strict:
	/// a typo in an internal flag must fail loudly instead of installing less.
	/// </summary>
	public static bool TryParseList(string text, out ScopeParts parts)
	{
		parts = ScopeParts.None;
		foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var part = FromToken(raw);
			if (part == null) return false;
			parts |= part.Value;
		}
		return parts != ScopeParts.None;
	}

	/// <summary>Parse <c>install.scope</c> content. Unknown lines are ignored.</summary>
	public static ScopeParts ParseFile(string content)
	{
		var parts = ScopeParts.None;
		foreach (var line in content.Split('\n'))
		{
			if (FromToken(line.Trim()) is { } part) parts |= part;
		}
		return parts;
	}

	/// <summary><c>install.scope</c> content: one token per line.</summary>
	public static string FormatFile(ScopeParts parts) =>
		string.Concat(ToTokens(parts).Select(t => t + "\n"));

	private static ScopeParts? FromToken(string token)
	{
		foreach (var (part, name) in s_order)
		{
			if (string.Equals(token, name, StringComparison.OrdinalIgnoreCase)) return part;
		}
		return null;
	}
}
