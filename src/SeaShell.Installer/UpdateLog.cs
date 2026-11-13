using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SeaShell.Installer;

/// <summary>How a scheduled update run ended, as its <see cref="UpdateLog"/> entry records it.</summary>
public sealed record UpdateLogRun(DateTimeOffset Time, int ExitCode, string Outcome);

/// <summary>
/// <c>{DataDir}/update.log</c>: the complete outcome of every scheduled
/// (<c>--quiet</c>) update — time, command line, exit code, summary and the
/// whole captured output — appended in addition to the Event Log / syslog,
/// which a non-elevated user install may not be able to write to (the Event
/// Log source is registered by an elevated install). Bounded: at
/// <see cref="RollOverBytes"/> the file becomes <c>update.log.1</c> (replacing
/// the previous one) and a new one starts.
/// </summary>
public static class UpdateLog
{
	public const string FileName = "update.log";
	public const long RollOverBytes = 1_000_000;
	private const string Header = "=== ";

	public static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

	/// <summary>True when the file has reached the size at which it is rolled over before the next entry.</summary>
	public static bool ShouldRollOver(long length) => length >= RollOverBytes;

	/// <summary>
	/// The time entries carry: the machine's local time, as ISO 8601 with its offset.
	/// </summary>
	public static DateTimeOffset Timestamp(DateTimeOffset now) => now.ToLocalTime();

	/// <summary>One entry: a header line (parsed by <see cref="LastRun"/>), the command line, the summary, the output.</summary>
	public static string Entry(DateTimeOffset time, int exitCode, string outcome, string commandLine, string summary, string output)
	{
		var sb = new StringBuilder();
		sb.Append(Header).Append(time.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture))
			.Append(" exit ").Append(exitCode.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(outcome).Append('\n');
		sb.Append("command: ").Append(commandLine).Append('\n');
		sb.Append("summary: ").Append(summary).Append('\n');
		if (output.Length > 0) sb.Append(output.Replace("\r\n", "\n").TrimEnd('\n')).Append('\n');
		return sb.Append('\n').ToString();
	}

	/// <summary>The last run recorded in <paramref name="content"/>; null when there is none.</summary>
	public static UpdateLogRun? LastRun(string content)
	{
		// From the end: the last line that parses as a header (captured output
		// may contain lines that merely start like one).
		var lines = content.Split('\n');
		for (var i = lines.Length - 1; i >= 0; i--)
		{
			if (!lines[i].StartsWith(Header, StringComparison.Ordinal)) continue;
			var fields = lines[i][Header.Length..].TrimEnd('\r').Split(' ');
			if (fields.Length == 4 && fields[1] == "exit"
				&& DateTimeOffset.TryParse(fields[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
				&& int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exit))
				return new UpdateLogRun(time, exit, fields[3]);
		}
		return null;
	}
}
