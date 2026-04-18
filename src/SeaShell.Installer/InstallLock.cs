using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SeaShell.Installer;

/// <summary>
/// Cross-platform per-user lock that serializes <c>seashell install</c> and
/// <c>seashell uninstall</c> invocations. Windows: named kernel mutex.
/// Linux: file lock via <see cref="FileShare.None"/>.
/// Returns null from <see cref="TryAcquire"/> when another instance already holds it.
/// </summary>
public sealed class InstallLock : IDisposable
{
	private Mutex? _winMutex;
	private FileStream? _lockFile;

	private InstallLock() { }

	public static InstallLock? TryAcquire()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return TryAcquireWindows();
		return TryAcquireUnix();
	}

	private static InstallLock? TryAcquireWindows()
	{
		// Per-user mutex: two shells under the same account race; different accounts don't.
		var name = $"SeaShell_install_u{Environment.UserName.ToLowerInvariant()}";
		try
		{
			var mutex = new Mutex(false, name, out var createdNew);
			if (!createdNew) { mutex.Dispose(); return null; }
			return new InstallLock { _winMutex = mutex };
		}
		catch { return null; }
	}

	private static InstallLock? TryAcquireUnix()
	{
		var path = Path.Combine(SeaShellPaths.DataDir, "install.lock");
		try
		{
			Directory.CreateDirectory(SeaShellPaths.DataDir);
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
