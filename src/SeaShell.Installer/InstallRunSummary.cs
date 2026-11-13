using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaShell.Installer;

/// <summary>How one install pass ended.</summary>
public enum PassResult
{
	/// <summary>Installed (or repaired) this version.</summary>
	Installed,

	/// <summary><c>--if-needed</c>: the target already had this version and every requested part.</summary>
	Satisfied,

	/// <summary>A newer version is active on the target; nothing was changed.</summary>
	SkippedNewer,

	Failed,
}

/// <summary>
/// What an install run did, per scope part. Built up by the passes of one
/// process; the elevated helper hands its summary back to the non-elevated
/// invoker through the <c>--result-file</c> it was given, so the invoker can
/// record the scope and finish the user pass when the helper had to skip it.
/// </summary>
public sealed class InstallRunSummary
{
	/// <summary>Parts that ended installed — this run, or already in place under <c>--if-needed</c>.</summary>
	public ScopeParts Completed { get; set; }

	/// <summary>Parts that were already satisfied under <c>--if-needed</c>.</summary>
	public ScopeParts Satisfied { get; set; }

	/// <summary>Parts attempted and failed (including a pass that could not be run, such as a refused sudo).</summary>
	public ScopeParts Failed { get; set; }

	/// <summary>Targets left alone because a newer version is active there.</summary>
	public ScopeParts SkippedNewer { get; set; }

	/// <summary>The elevated helper ran as a different account and skipped the user parts.</summary>
	public bool UserSkipped { get; set; }

	/// <summary>Result of the helper's <c>--detect</c> probe, when it was asked for one.</summary>
	public ScopeDetection? Detection { get; set; }

	public int ExitCode => Failed != ScopeParts.None ? 1 : 0;

	/// <summary>Record one pass's result for the parts it covered.</summary>
	public void Add(ScopeParts parts, PassResult result)
	{
		switch (result)
		{
			case PassResult.Installed: Completed |= parts; break;
			case PassResult.Satisfied: Completed |= parts; Satisfied |= parts; break;
			case PassResult.SkippedNewer: SkippedNewer |= parts; break;
			case PassResult.Failed: Failed |= parts; break;
		}
	}

	/// <summary>Merge the elevated helper's summary into the invoker's.</summary>
	public void Merge(InstallRunSummary other)
	{
		Completed |= other.Completed;
		Satisfied |= other.Satisfied;
		Failed |= other.Failed;
		SkippedNewer |= other.SkippedNewer;
	}

	/// <summary>
	/// "Up to date" is printed only when every requested part was already
	/// satisfied — a part that was (re)installed, failed, or not reached means
	/// the run did something or did not finish.
	/// </summary>
	public bool AllSatisfied(ScopeParts requested) =>
		requested != ScopeParts.None && (Satisfied & requested) == requested && Failed == ScopeParts.None;

	// ── Result file (elevated helper → invoker) ────────────────────────
	// key=value lines; both ends are the same bootstrapper binary.

	public string Serialize()
	{
		var lines = new List<string>
		{
			$"completed={ScopeTokens.FormatList(Completed)}",
			$"satisfied={ScopeTokens.FormatList(Satisfied)}",
			$"failed={ScopeTokens.FormatList(Failed)}",
			$"skipped-newer={ScopeTokens.FormatList(SkippedNewer)}",
			$"user-skipped={(UserSkipped ? "true" : "false")}",
		};
		if (Detection is { } d)
		{
			lines.Add($"detected-present={ScopeTokens.FormatList(d.Present)}");
			lines.Add($"detected-unknown={ScopeTokens.FormatList(d.Unknown)}");
		}
		return string.Join("\n", lines) + "\n";
	}

	public static InstallRunSummary Parse(string content)
	{
		var values = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(l => l.Split('=', 2))
			.Where(kv => kv.Length == 2)
			.GroupBy(kv => kv[0], StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);

		ScopeParts List(string key) =>
			values.TryGetValue(key, out var v) && ScopeTokens.TryParseList(v, out var parts) ? parts : ScopeParts.None;

		var summary = new InstallRunSummary
		{
			Completed = List("completed"),
			Satisfied = List("satisfied"),
			Failed = List("failed"),
			SkippedNewer = List("skipped-newer"),
			UserSkipped = values.TryGetValue("user-skipped", out var skipped) && skipped == "true",
		};
		if (values.ContainsKey("detected-present") || values.ContainsKey("detected-unknown"))
		{
			var present = List("detected-present");
			var unknown = List("detected-unknown");
			summary.Detection = new ScopeDetection(
				PresenceOf(ScopeParts.Elevator, present, unknown),
				PresenceOf(ScopeParts.System, present, unknown));
		}
		return summary;
	}

	private static PartPresence PresenceOf(ScopeParts part, ScopeParts present, ScopeParts unknown) =>
		(present & part) != 0 ? PartPresence.Present
		: (unknown & part) != 0 ? PartPresence.Unknown
		: PartPresence.Absent;
}

/// <summary>What the user pass writes to <c>install.scope</c> after a run.</summary>
public static class ScopeRecording
{
	/// <summary>
	/// The parts to record, or null to leave the file untouched.
	///
	/// Only a run whose resolution records, and whose user pass ended with
	/// the user install in place, writes the file. Parts this run completed
	/// are recorded; a part it skipped (such as the elevator behind
	/// alternate-account UAC) is not. A part that failed this time but was
	/// known to exist before the run (<paramref name="knownBefore"/>: the
	/// remembered or detected scope) is kept, so one transient failure does
	/// not silently drop a SYSTEM install from every later update. A part
	/// left alone because a newer version is already active there is
	/// installed, just not by this run: it stays recorded, or a pinned older
	/// <c>update --version</c> would make every later update abandon it.
	/// Null as well when the result equals what <paramref name="recorded"/>
	/// already holds: a run that changed nothing does not rewrite the file.
	/// </summary>
	public static ScopeParts? PartsToRecord(
		ScopeResolution resolution, InstallRunSummary summary, ScopeParts knownBefore, ScopeFileRead recorded = default)
	{
		if (!resolution.Record) return null;
		if ((summary.Completed & ScopeParts.User) == 0) return null;

		var parts = ScopeParts.User
			| summary.Completed
			| (summary.SkippedNewer & ~ScopeParts.User)
			| (summary.Failed & knownBefore & ~ScopeParts.User);
		if (recorded.State == ScopeFileState.Read && recorded.Parts == parts) return null;
		return parts;
	}
}
