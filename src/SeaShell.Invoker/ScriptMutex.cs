using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SeaShell.Invoker;

/// <summary>
/// Cross-platform mutex for script instance control.
/// Windows: named kernel mutex. Linux: file lock (flock).
/// Implements IDisposable — hold the returned object for the script's lifetime.
/// </summary>
public sealed class ScriptMutex : IDisposable
{
	private Mutex? _winMutex;
	private Mutex? _winLegacyMutex;
	private FileStream? _lockFile;

	private ScriptMutex() { }

	/// <summary>
	/// Try to acquire a mutex for the given script.
	/// Returns the held mutex on success, or null if another instance holds it.
	/// </summary>
	public static ScriptMutex? TryAcquire(
		string identity, DirectiveScanner.MutexScope scope, Action<string>? log = null)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return TryAcquireWindows(identity, scope, log);
		else
			return TryAcquireLinux(identity, scope, log);
	}

	/// <summary>
	/// The well-known attach pipe name for this mutex identity.
	/// Used by both the server (running instance) and client (blocked caller).
	/// </summary>
	public static string GetAttachPipeName(string identity) =>
		$"seashell-attach-{identity}";

	private static ScriptMutex? TryAcquireWindows(
		string identity, DirectiveScanner.MutexScope scope, Action<string>? log)
	{
		// Windows kernel object naming: only Global\ and Local\ are valid prefix
		// separators. After the prefix, the name must be a flat string (no backslashes).
		// User scope is Global\ + SID token: cross-session (one instance per user
		// regardless of RDP/console session) and stable under LocalSystem.
		var name = scope switch
		{
			DirectiveScanner.MutexScope.Session =>
				$"Local\\SeaShell_{identity}_s{Process.GetCurrentProcess().SessionId}",
			DirectiveScanner.MutexScope.User =>
				$"Global\\SeaShell_{identity}_u{SeaShellIdentity.Current.Token}",
			DirectiveScanner.MutexScope.System =>
				$"Global\\SeaShell_{identity}",
			_ => throw new ArgumentException($"Invalid mutex scope: {scope}")
		};

		Mutex? legacy = null;
		try
		{
			if (scope == DirectiveScanner.MutexScope.User)
			{
				// Transition shim — remove in 0.7: pre-0.6.0 versions name the
				// user-scope mutex by Environment.UserName, unprefixed (session-local
				// namespace). Hold BOTH names so old and new versions exclude each
				// other while both are installed. Acquire order is fixed
				// (legacy first) in every version, so concurrent starters can't
				// deadlock by acquiring in opposite orders.
				legacy = new Mutex(false,
					$"SeaShell_{identity}_u{Environment.UserName.ToLowerInvariant()}",
					out var legacyNew);
				if (!legacyNew)
				{
					legacy.Dispose();
					return null;
				}
			}

			var mutex = new Mutex(false, name, out var createdNew);
			if (!createdNew)
			{
				mutex.Dispose();
				legacy?.Dispose();
				return null;
			}
			return new ScriptMutex { _winMutex = mutex, _winLegacyMutex = legacy };
		}
		catch (Exception ex)
		{
			legacy?.Dispose();
			log?.Invoke($"mutex acquire failed: {ex.Message}");
			return null;
		}
	}

	private static ScriptMutex? TryAcquireLinux(
		string identity, DirectiveScanner.MutexScope scope, Action<string>? log)
	{
		var lockPath = GetLinuxLockPath(identity, scope);

		try
		{
			var dir = Path.GetDirectoryName(lockPath)!;
			Directory.CreateDirectory(dir);

			var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			return new ScriptMutex { _lockFile = fs };
		}
		catch (IOException)
		{
			// File is locked by another instance
			return null;
		}
		catch (Exception ex)
		{
			log?.Invoke($"mutex acquire failed: {ex.Message}");
			return null;
		}
	}

	private static string GetLinuxLockPath(string identity, DirectiveScanner.MutexScope scope)
	{
		return scope switch
		{
			DirectiveScanner.MutexScope.Session =>
				Path.Combine(
					Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
						?? Path.Combine(Path.GetTempPath(), $"runtime-{Environment.UserName}"),
					"seashell", $"{identity}.lock"),
			DirectiveScanner.MutexScope.User =>
				Path.Combine(
					Environment.GetEnvironmentVariable("XDG_DATA_HOME")
						?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
					"seashell", "mutex", $"{identity}.lock"),
			DirectiveScanner.MutexScope.System =>
				Path.Combine(Path.GetTempPath(), $"seashell-mutex-{identity}.lock"),
			_ => throw new ArgumentException($"Invalid mutex scope: {scope}")
		};
	}

	public void Dispose()
	{
		if (_winMutex != null)
		{
			try { _winMutex.ReleaseMutex(); } catch { }
			_winMutex.Dispose();
			_winMutex = null;
		}

		if (_winLegacyMutex != null)
		{
			try { _winLegacyMutex.ReleaseMutex(); } catch { }
			_winLegacyMutex.Dispose();
			_winLegacyMutex = null;
		}

		if (_lockFile != null)
		{
			var path = _lockFile.Name;
			_lockFile.Dispose();
			_lockFile = null;
			try { File.Delete(path); } catch { }
		}
	}
}
