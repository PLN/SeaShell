using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace SeaShell.Cli;

/// <summary>
/// Interactive console countdown. Shows remaining time, lets the user skip (Enter),
/// cancel (Escape), pause (P), or adjust time (arrow keys).
/// Windows-only — no-op on other platforms.
/// Adapted from System.Console.Ext.cs ConsoleDelayer.
/// </summary>
[SupportedOSPlatform("windows")]
static class ConsoleDelayer
{
	/// <summary>
	/// Run an interactive countdown delay.
	/// Returns 0 if completed/skipped, >0 (remaining seconds) if cancelled.
	/// </summary>
	public static int Delay(int seconds)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return 0;
		if (seconds <= 0) return 0;
		if (Console.IsInputRedirected) return 0;

		var until = DateTime.Now.AddSeconds(seconds);
		var paused = false;
		var remaining = TimeSpan.FromSeconds(seconds);
		string? originalTitle = null;
		var titleSupported = false;

		try { originalTitle = Console.Title; titleSupported = true; } catch { }

		Console.WriteLine($"Exiting in {seconds}s...   Enter=Skip   Esc=Cancel   P=Pause   Up/Down=+/-1s   Left/Right=+/-10s");

		try
		{
			while (true)
			{
				var eta = paused ? remaining : until - DateTime.Now;
				if (eta.TotalSeconds <= 0) break;

				// Update status
				var fractions = eta.TotalSeconds < 10 ? 1 : 0;
				var status = $"{(paused ? "Paused" : "Exiting in")} {FormatTime(eta, fractions)}...";
				if (titleSupported)
					Console.Title = $"{status} - {originalTitle}";
				else
					Console.Write($"\r{status}   ");

				// Poll for key
				var key = ReadKeyWithTimeout(TimeSpan.FromMilliseconds(50));
				switch (key.Key)
				{
					case ConsoleKey.Escape:
					case ConsoleKey.Q:
						return Math.Max(1, (int)eta.TotalSeconds);

					case ConsoleKey.Enter:
					case ConsoleKey.W:
						return 0;

					case ConsoleKey.UpArrow: Adjust(1); break;
					case ConsoleKey.DownArrow: Adjust(-1); break;
					case ConsoleKey.RightArrow:
					case ConsoleKey.Spacebar: Adjust(10); break;
					case ConsoleKey.LeftArrow:
					case ConsoleKey.Backspace: Adjust(-10); break;
					case ConsoleKey.PageUp: Adjust(600); break;
					case ConsoleKey.PageDown: Adjust(-600); break;

					case ConsoleKey.P:
						if (paused)
						{
							until = DateTime.Now.Add(remaining);
							paused = false;
						}
						else
						{
							remaining = until - DateTime.Now;
							paused = true;
						}
						break;
				}

				void Adjust(int delta)
				{
					if (paused)
						remaining = remaining.Add(TimeSpan.FromSeconds(delta));
					else
						until = until.AddSeconds(delta);
				}
			}
		}
		finally
		{
			if (titleSupported && originalTitle != null)
				try { Console.Title = originalTitle; } catch { }
			if (!titleSupported)
				Console.WriteLine();
		}

		return 0;
	}

	private static string FormatTime(TimeSpan ts, int fractions)
	{
		if (ts.TotalHours >= 1)
			return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
		if (ts.TotalMinutes >= 1)
			return $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s";
		return fractions > 0
			? $"{ts.TotalSeconds:F1}s"
			: $"{(int)ts.TotalSeconds}s";
	}

	// Non-blocking key read with timeout — avoids the blocking Console.ReadKey
	private static readonly BlockingCollection<ConsoleKeyInfo> _keyQueue = new();
	private static readonly ConsoleKeyInfo _noKey = new('\0', ConsoleKey.NoName, false, false, false);
	private static readonly Task _keyReader = Task.Factory.StartNew(() =>
	{
		try
		{
			if (Console.IsInputRedirected) return;
			while (!_keyQueue.IsAddingCompleted)
				_keyQueue.Add(Console.ReadKey(true));
		}
		catch { }
	}, TaskCreationOptions.LongRunning);

	private static ConsoleKeyInfo ReadKeyWithTimeout(TimeSpan timeout)
	{
		return !_keyReader.IsCompleted && _keyQueue.TryTake(out var key, timeout) ? key : _noKey;
	}
}
