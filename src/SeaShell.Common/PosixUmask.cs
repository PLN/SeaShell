using System;
using System.Runtime.InteropServices;

namespace SeaShell;

/// <summary>
/// Minimal wrapper around the POSIX umask() syscall.
/// Used to close the TOCTOU gap between socket/pipe creation and permission setting.
/// </summary>
public static class PosixUmask
{
	[DllImport("libc", EntryPoint = "umask", SetLastError = true)]
	private static extern uint NativeUmask(uint mask);

	/// <summary>
	/// Set umask to owner-only (0077) before creating a socket or pipe,
	/// then restore the original umask when the returned scope is disposed.
	/// No-op on Windows.
	/// </summary>
	public static IDisposable RestrictiveUmask()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return NoOpDisposable.Instance;

		var old = NativeUmask(0x3F); // 0077 octal
		return new UmaskRestorer(old);
	}

	private sealed class UmaskRestorer(uint old) : IDisposable
	{
		public void Dispose() => NativeUmask(old);
	}

	private sealed class NoOpDisposable : IDisposable
	{
		public static readonly NoOpDisposable Instance = new();
		public void Dispose() { }
	}
}
