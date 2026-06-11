using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SeaShell.Installer;

/// <summary>
/// Cross-platform per-target-identity lock that serializes <c>seashell install</c>
/// and <c>seashell uninstall</c> invocations. Windows: named kernel mutex.
/// Linux: file lock via <see cref="FileShare.None"/>.
/// The lock is scoped to the install TARGET, not the invoker — two elevated
/// admins racing a <c>--system</c> install contend on the same lock.
/// Returns null from <see cref="TryAcquire"/> when another instance already holds it.
/// </summary>
public sealed class InstallLock : IDisposable
{
	private Mutex? _winMutex;
	private FileStream? _lockFile;

	private InstallLock() { }

	public static InstallLock? TryAcquire(SeaShellIdentity target)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return TryAcquireWindows(target);
		return TryAcquireUnix(target);
	}

	private static InstallLock? TryAcquireWindows(SeaShellIdentity target)
	{
		// Per-target mutex: two installs for the same target identity race;
		// different targets don't. Global\ so the lock holds across logon
		// sessions (two consoles, RDP + console, or a scheduled context) —
		// unprefixed names land in the session-local namespace and would not
		// exclude each other.
		var name = $"Global\\SeaShell_install_{target.Token}";
		try
		{
			var mutex = new Mutex(false, name, out var createdNew);
			if (!createdNew) { mutex.Dispose(); return null; }
			return new InstallLock { _winMutex = mutex };
		}
		catch { return null; }
	}

	private static InstallLock? TryAcquireUnix(SeaShellIdentity target)
	{
		var path = Path.Combine(target.DataDir, "install.lock");
		try
		{
			Directory.CreateDirectory(target.DataDir);
			var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			return new InstallLock { _lockFile = fs };
		}
		catch (IOException) { return null; }
		catch { return null; }
	}

	public void Dispose()
	{
		if (_winMutex != null)
		{
			try { _winMutex.ReleaseMutex(); } catch { }
			_winMutex.Dispose();
			_winMutex = null;
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
