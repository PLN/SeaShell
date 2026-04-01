using System;
using System.IO;

namespace SeaShell.Invoker;

/// <summary>
/// Lightweight pre-compilation scanner for directives that must be evaluated
/// before the daemon is contacted. Reads the first ~20 lines of a script file
/// and detects //sea_mutex, //sea_mutex_attach, //sea_window, //sea_console
/// by simple string matching. No Roslyn, no full parse.
/// </summary>
public static class DirectiveScanner
{
	public enum MutexScope : byte { None, Session, User, System }

	public sealed record ScanResult
	{
		public MutexScope Mutex { get; init; }
		public bool MutexAttach { get; init; }
		public bool Window { get; init; }
		public bool Console { get; init; }
	}

	private static readonly ScanResult _empty = new();

	/// <summary>
	/// Quick-scan a script file for pre-compilation directives.
	/// Returns a default result if the file cannot be read.
	/// </summary>
	public static ScanResult Scan(string scriptPath)
	{
		try
		{
			using var reader = new StreamReader(scriptPath);
			var mutex = MutexScope.None;
			var mutexAttach = false;
			var window = false;
			var console = false;
			var linesRead = 0;

			while (linesRead < 20 && reader.ReadLine() is { } line)
			{
				linesRead++;
				var trimmed = line.AsSpan().Trim();

				// Only look at //sea_ directives (C#) and 'sea_ (VB)
				if (trimmed.StartsWith("//sea_") || trimmed.StartsWith("'sea_"))
				{
					var prefixLen = trimmed.StartsWith("//") ? 6 : 5;
					var rest = trimmed[prefixLen..].Trim();

					if (rest.StartsWith("mutex_attach"))
					{
						mutexAttach = true;
						// mutex_attach implies mutex — parse scope from remainder
						var arg = rest["mutex_attach".Length..].Trim();
						mutex = ParseScope(arg, MutexScope.System);
					}
					else if (rest.StartsWith("mutex"))
					{
						var arg = rest["mutex".Length..].Trim();
						mutex = ParseScope(arg, MutexScope.System);
					}
					else if (rest.StartsWith("window"))
					{
						window = true;
					}
					else if (rest.StartsWith("console"))
					{
						console = true;
					}
				}

				// Stop at first real code line (not a comment, blank, using, or attribute)
				if (!trimmed.IsEmpty
					&& !trimmed.StartsWith("//") && !trimmed.StartsWith("'")
					&& !trimmed.StartsWith("using ") && !trimmed.StartsWith("global using ")
					&& !trimmed.StartsWith("Imports ")
					&& !trimmed.StartsWith("[") && !trimmed.StartsWith("<"))
					break;
			}

			if (mutex == MutexScope.None && !mutexAttach && !window && !console)
				return _empty;

			return new ScanResult
			{
				Mutex = mutex,
				MutexAttach = mutexAttach,
				Window = window,
				Console = console,
			};
		}
		catch
		{
			return _empty;
		}
	}

	/// <summary>
	/// Compute a stable identity string for mutex/attach pipe naming.
	/// FNV-1a hash of the absolute, lower-cased script path.
	/// </summary>
	public static string ComputeIdentity(string scriptPath)
	{
		var normalized = Path.GetFullPath(scriptPath).ToLowerInvariant();
		var hash = Fnv1a64(normalized);
		return hash.ToString("x16");
	}

	private static MutexScope ParseScope(ReadOnlySpan<char> arg, MutexScope defaultScope)
	{
		if (arg.IsEmpty) return defaultScope;

		if (arg.StartsWith("system", StringComparison.OrdinalIgnoreCase)) return MutexScope.System;
		if (arg.StartsWith("user", StringComparison.OrdinalIgnoreCase)) return MutexScope.User;
		if (arg.StartsWith("session", StringComparison.OrdinalIgnoreCase)) return MutexScope.Session;

		return defaultScope;
	}

	private static ulong Fnv1a64(string data)
	{
		const ulong fnvOffsetBasis = 14695981039346656037;
		const ulong fnvPrime = 1099511628211;

		var hash = fnvOffsetBasis;
		foreach (var c in data)
		{
			hash ^= c;
			hash *= fnvPrime;
		}
		return hash;
	}
}
