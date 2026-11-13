using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SeaShell.Installer;

/// <summary>
/// Windows command-line encoding (the rules <c>CommandLineToArgvW</c> and the
/// C runtime parse with): an argument with whitespace or a quote is enclosed
/// in quotes; backslashes are literal except before a quote, so a run of them
/// is doubled when a quote follows — an embedded one (escaped as <c>\"</c>) or
/// the closing one. <c>C:\feed dir\</c> becomes <c>"C:\feed dir\\"</c>; quoted
/// naively as <c>"C:\feed dir\"</c> the child reads <c>C:\feed dir"</c>.
/// Used for every command line of a scheduled task.
/// </summary>
public static class WindowsCommandLine
{
	public static string Quote(string arg)
	{
		if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"')) return arg;

		var sb = new StringBuilder("\"");
		var backslashes = 0;
		foreach (var c in arg)
		{
			if (c == '\\') { backslashes++; continue; }
			if (c == '"')
			{
				sb.Append('\\', backslashes * 2 + 1).Append('"');
				backslashes = 0;
				continue;
			}
			sb.Append('\\', backslashes).Append(c);
			backslashes = 0;
		}
		return sb.Append('\\', backslashes * 2).Append('"').ToString();
	}

	public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));
}
