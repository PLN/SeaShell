using System;

namespace SeaShell.Installer;

/// <summary>
/// Outcome of probing the machine for one part. <see cref="Unknown"/> is a
/// failed or denied query: it must never be read as <see cref="Absent"/>, or
/// a SYSTEM install that a non-elevated user cannot see would silently drop
/// out of the remembered scope.
/// </summary>
public enum PartPresence
{
	Absent,
	Present,
	Unknown,
}

/// <summary>
/// What scope detection found for an install without <c>install.scope</c>
/// (made by 0.6.2 or earlier). The user part is implied.
/// </summary>
public readonly record struct ScopeDetection(PartPresence Elevator, PartPresence System)
{
	public ScopeParts Present =>
		(Elevator == PartPresence.Present ? ScopeParts.Elevator : ScopeParts.None)
		| (System == PartPresence.Present ? ScopeParts.System : ScopeParts.None);

	public ScopeParts Unknown =>
		(Elevator == PartPresence.Unknown ? ScopeParts.Elevator : ScopeParts.None)
		| (System == PartPresence.Unknown ? ScopeParts.System : ScopeParts.None);
}

/// <summary>The scope-related flags of one invocation.</summary>
public sealed record ScopeRequest
{
	/// <summary><c>--all</c>: everything the platform supports; re-records.</summary>
	public bool All { get; init; }

	/// <summary><c>--user</c>: user only; re-records (a narrowing when more exists).</summary>
	public bool User { get; init; }

	/// <summary><c>--system</c>: system only; never touches the user's recorded scope.</summary>
	public bool System { get; init; }

	/// <summary>
	/// Internal <c>--pass</c> targeting (relaunches, scheduled updates): exactly
	/// these parts, never rewriting the recorded scope.
	/// </summary>
	public ScopeParts? Pass { get; init; }
}

/// <summary>Where the effective scope came from.</summary>
public enum ScopeSource
{
	Pass,
	All,
	User,
	System,
	Recorded,
	Detected,
}

/// <summary>The effective scope of one install/update/uninstall invocation.</summary>
public sealed record ScopeResolution
{
	/// <summary>Parts to act on.</summary>
	public required ScopeParts Parts { get; init; }

	/// <summary>
	/// Parts that may exist but could not be checked from this context. Only
	/// set for <see cref="ScopeSource.Detected"/>. An elevated helper can
	/// resolve them; without elevation they are left alone (0.6.2 behaviour).
	/// </summary>
	public required ScopeParts Unresolved { get; init; }

	public required ScopeSource Source { get; init; }

	/// <summary>
	/// Whether the outcome of this invocation rewrites <c>install.scope</c>.
	/// False for <c>--system</c>, <c>--pass</c>, an unreadable file, and a
	/// detection that left parts unresolved.
	/// </summary>
	public required bool Record { get; init; }

	/// <summary>
	/// A detection without a scope file is recorded once its unresolved parts
	/// have been resolved (by the elevated helper). False for an unreadable
	/// file, which is never overwritten from a guess.
	/// </summary>
	public bool RecordWhenResolved { get; init; }

	/// <summary>
	/// Parts known to exist that this request drops from the recorded scope
	/// (<c>--user</c> over an install with an elevator or SYSTEM part).
	/// </summary>
	public required ScopeParts LeftBehind { get; init; }
}

/// <summary>
/// The sticky-scope rule: flags override and re-record; without flags the
/// remembered scope applies; without a remembered scope it is detected, and
/// an uncertain detection never narrows anything. Shared by install, update
/// and uninstall.
/// </summary>
public static class ScopeResolver
{
	/// <summary>Null when the flags are consistent, otherwise the error to print.</summary>
	public static string? Validate(ScopeRequest request)
	{
		var count = (request.All ? 1 : 0) + (request.User ? 1 : 0) + (request.System ? 1 : 0)
			+ (request.Pass != null ? 1 : 0);
		return count > 1 ? "--all, --user, --system and --pass are mutually exclusive" : null;
	}

	/// <summary>
	/// Resolve the effective scope. <paramref name="detect"/> is only invoked
	/// when the answer depends on it (no usable scope file, or <c>--user</c>
	/// over an install without one).
	/// </summary>
	public static ScopeResolution Resolve(
		ScopeRequest request, ScopeFileRead recorded, Func<ScopeDetection> detect, bool isWindows)
	{
		var supported = ScopeTokens.PlatformAll(isWindows);

		if (request.Pass is { } pass)
			return Fixed(pass & supported, ScopeSource.Pass, record: false);

		if (request.System)
			return Fixed(ScopeParts.System, ScopeSource.System, record: false);

		if (request.All)
			return Fixed(supported, ScopeSource.All, record: true);

		if (request.User)
		{
			var existing = recorded.State == ScopeFileState.Read
				? recorded.Parts
				: detect().Present;
			return new ScopeResolution
			{
				Parts = ScopeParts.User,
				Unresolved = ScopeParts.None,
				Source = ScopeSource.User,
				Record = true,
				LeftBehind = existing & supported & ~ScopeParts.User,
			};
		}

		if (recorded.State == ScopeFileState.Read)
			return Fixed((recorded.Parts | ScopeParts.User) & supported, ScopeSource.Recorded, record: true);

		// No usable file: detect. The user part is always included.
		var detection = detect();
		var unresolved = detection.Unknown & supported;
		return new ScopeResolution
		{
			Parts = (ScopeParts.User | detection.Present) & supported,
			Unresolved = unresolved,
			Source = ScopeSource.Detected,
			// An unreadable file is left alone, and an uncertain answer is not
			// written down: a later elevated run may still find more.
			Record = recorded.State == ScopeFileState.Missing && unresolved == ScopeParts.None,
			RecordWhenResolved = recorded.State == ScopeFileState.Missing,
			LeftBehind = ScopeParts.None,
		};
	}

	/// <summary>
	/// Fold the result of an elevated helper's detection of
	/// <see cref="ScopeResolution.Unresolved"/> parts back into the scope.
	/// </summary>
	public static ScopeResolution WithResolved(ScopeResolution resolution, ScopeDetection elevatedDetection)
	{
		var stillUnknown = resolution.Unresolved & elevatedDetection.Unknown;
		return resolution with
		{
			Parts = resolution.Parts | (resolution.Unresolved & elevatedDetection.Present),
			Unresolved = stillUnknown,
			Record = resolution.Record || (resolution.RecordWhenResolved && stillUnknown == ScopeParts.None),
		};
	}

	private static ScopeResolution Fixed(ScopeParts parts, ScopeSource source, bool record) => new()
	{
		Parts = parts,
		Unresolved = ScopeParts.None,
		Source = source,
		Record = record,
		LeftBehind = ScopeParts.None,
	};
}
