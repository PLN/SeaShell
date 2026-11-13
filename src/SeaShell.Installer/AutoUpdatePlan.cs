using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeaShell.Installer;

/// <summary>One scheduled automatic-update entry: which target it updates and how it runs.</summary>
/// <param name="Parts">The parts this entry updates (<c>--pass</c>): user[,elevator], or system.</param>
/// <param name="Name">Windows task path / description for output.</param>
/// <param name="System">Runs as LocalSystem (Windows) / root (Linux).</param>
/// <param name="HighestPrivileges">Windows user task: run with the user's elevated token (the scope has an elevator).</param>
/// <param name="Time">Daily start time.</param>
/// <param name="Arguments">Arguments of the installed <c>seashell</c>.</param>
public sealed record AutoUpdateEntry(
	ScopeParts Parts, string Name, bool System, bool HighestPrivileges, TimeOnly Time, IReadOnlyList<string> Arguments);

/// <summary>
/// Opt-in automatic updates (<c>seashell update --auto on|off</c>). One entry
/// per target, each updating only itself: <c>update --pass &lt;target&gt;
/// --if-idle --quiet</c>, never rewriting the scope. Windows: task
/// <c>SeaShell Update (identity)</c>, and <c>SeaShell Update (SYSTEM)</c> when
/// the scope has the SYSTEM install. Linux: one crontab line tagged
/// <see cref="CronTag"/> per identity (root's own crontab for the root install).
/// The entry runs the installed copy in <c>current/bin</c>, so it survives
/// version changes, and records the absolute <c>dotnet</c> it was enabled
/// with (and the <c>--source</c>, if one was given).
/// </summary>
public static class AutoUpdatePlan
{
	/// <summary>
	/// Tag of the crontab line. Deliberately outside the <c># seashell:</c>
	/// namespace of scheduled scripts, which script listing, replacement and
	/// the path refresher match on.
	/// </summary>
	public const string CronTag = "# seashell-auto-update";

	/// <summary>The entries for a scope on this platform, in the order they are created.</summary>
	/// <param name="userDisplayName">Display name of the user identity (task name).</param>
	/// <param name="userToken">The user's identity token (randomises the time).</param>
	public static IReadOnlyList<AutoUpdateEntry> Entries(
		ScopeParts scope, bool isWindows, string machineName, string userDisplayName, string userToken,
		string dotnet, string? source)
	{
		var entries = new List<AutoUpdateEntry>();
		var userParts = scope & (ScopeParts.User | ScopeParts.Elevator) | ScopeParts.User;
		if (!isWindows) userParts = ScopeParts.User;
		entries.Add(new AutoUpdateEntry(
			userParts,
			isWindows ? SeaShellTaskNames.UpdateTaskPath(userDisplayName) : $"crontab of {userDisplayName}",
			System: false,
			HighestPrivileges: isWindows && (userParts & ScopeParts.Elevator) != 0,
			DailyTime(machineName, userToken),
			Arguments(userParts, dotnet, source)));

		if ((scope & ScopeParts.System) != 0)
			entries.Add(new AutoUpdateEntry(
				ScopeParts.System,
				isWindows ? SeaShellTaskNames.UpdateTaskPath("SYSTEM") : "crontab of root",
				System: true,
				HighestPrivileges: isWindows,
				DailyTime(machineName, isWindows ? "s-1-5-18" : "0"),
				Arguments(ScopeParts.System, dotnet, source)));
		return entries;
	}

	/// <summary>
	/// The entry recording <paramref name="dotnet"/> instead: the SYSTEM / root
	/// entry gets the one <see cref="SystemDotnet"/> chose, not the enabling user's.
	/// </summary>
	public static AutoUpdateEntry WithDotnet(AutoUpdateEntry entry, string dotnet, string? source) =>
		entry with { Arguments = Arguments(entry.Parts, dotnet, source) };

	/// <summary>Arguments of the scheduled <c>seashell</c>.</summary>
	public static IReadOnlyList<string> Arguments(ScopeParts parts, string dotnet, string? source)
	{
		var args = new List<string> { "update", "--pass", ScopeTokens.FormatList(parts), "--if-idle", "--quiet", "--dotnet", dotnet };
		if (!string.IsNullOrEmpty(source)) { args.Add("--source"); args.Add(source); }
		return args;
	}

	/// <summary>
	/// A start time per machine and identity: spread over the day so a fleet
	/// does not hit the feed at once, and stable, so turning it off and on
	/// again keeps the time.
	/// </summary>
	public static TimeOnly DailyTime(string machineName, string identityToken)
	{
		// FNV-1a: string.GetHashCode is randomised per process.
		uint hash = 2166136261;
		foreach (var c in $"{machineName.ToLowerInvariant()}|{identityToken.ToLowerInvariant()}")
		{
			hash ^= c;
			hash *= 16777619;
		}
		return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(hash % (24 * 60)));
	}

	/// <summary>
	/// The crontab line for an entry. Cron runs with an almost empty
	/// environment, so <c>HOME</c>, <c>DOTNET_ROOT</c> and <c>PATH</c> are set
	/// explicitly for the apphost and for <c>dotnet tool install</c>. Output is
	/// not discarded: <c>--quiet</c> prints only when something was done or
	/// failed, which is what cron mails.
	/// </summary>
	public static string CronLine(AutoUpdateEntry entry, string seashellPath, string home, string dotnetRoot, string path) =>
		$"{entry.Time.Minute} {entry.Time.Hour} * * * " +
		$"HOME={CronQuote(home)} DOTNET_ROOT={CronQuote(dotnetRoot)} PATH={CronQuote(path)} " +
		$"{CronQuote(seashellPath)} {string.Join(' ', entry.Arguments.Select(CronQuote))} {CronTag}";

	/// <summary>True for the automatic-update line of a crontab.</summary>
	public static bool IsCronLine(string line) =>
		line.TrimEnd().EndsWith(CronTag, StringComparison.Ordinal);

	/// <summary>The crontab with its automatic-update line replaced by <paramref name="line"/> (null: removed).</summary>
	public static List<string> WithCronLine(IEnumerable<string> crontab, string? line)
	{
		var lines = crontab.Where(l => !IsCronLine(l)).ToList();
		while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
		if (line != null) lines.Add(line);
		return lines;
	}

	/// <summary>Parse a <c>--auto</c> value.</summary>
	public static bool? ParseSwitch(string? value) => value?.ToLowerInvariant() switch
	{
		"on" => true,
		"off" => false,
		_ => null,
	};

	/// <summary>
	/// Why <paramref name="value"/> cannot be stored in an entry (null: it can):
	/// a line break would end the crontab line, or the task's command line.
	/// </summary>
	public static string? StoredValueError(string value) =>
		value.IndexOfAny(new[] { '\n', '\r' }) >= 0 ? "contains a line break" : null;

	/// <summary>
	/// <c>--source</c> as stored in the entry, which runs from another working
	/// directory: a local path (one that exists here) becomes absolute; a URL,
	/// and anything else (a configured source name), stays as given.
	/// </summary>
	public static string ResolveSource(string source, string workingDirectory, Func<string, bool> exists)
	{
		if (source.Contains("://", StringComparison.Ordinal)) return source;
		var full = Path.GetFullPath(source, workingDirectory);
		return exists(full) ? full : source;
	}

	/// <summary>
	/// One word of a crontab command for <c>/bin/sh</c>. Cron itself turns every
	/// unescaped <c>%</c> into a line break (cronie / Vixie cron, even inside
	/// quotes; BusyBox crond leaves it alone), and a line break ends the entry.
	/// So a <c>%</c> is never written literally: it is produced by
	/// <c>$(printf '\045')</c>, which reads the same to every cron and to the
	/// shell. Line breaks are rejected (<see cref="StoredValueError"/>).
	/// </summary>
	internal static string CronQuote(string s)
	{
		if (StoredValueError(s) is { } error) throw new ArgumentException($"'{s}' {error}; it cannot be written into a crontab line.");
		if (s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || "/._-,:=+@".Contains(c))) return s;
		return "'" + s.Replace("'", @"'\''").Replace("%", @"'""$(printf '\045')""'") + "'";
	}
}
