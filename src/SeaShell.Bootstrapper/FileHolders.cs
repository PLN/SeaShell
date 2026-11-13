using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SeaShell.Bootstrapper;

/// <summary>A process that has a file open or mapped.</summary>
internal readonly record struct FileHolder(int Pid, string Name)
{
	public override string ToString() => $"{Name} (pid {Pid})";
}

/// <summary>
/// Which processes hold a set of files (Windows Restart Manager). Used for
/// diagnostics when a file cannot be replaced or deleted, and by uninstall
/// to tell "held only by the seashell that started us" from "in use by a
/// running script". Empty off Windows, or when the query fails.
/// </summary>
internal static class FileHolders
{
	public static IReadOnlyList<FileHolder> Find(IEnumerable<string> files)
	{
		if (!OperatingSystem.IsWindows()) return Array.Empty<FileHolder>();
		var paths = files.ToArray();
		if (paths.Length == 0) return Array.Empty<FileHolder>();

		var key = new StringBuilder(CchRmSessionKey + 1);
		if (RmStartSession(out var session, 0, key) != 0) return Array.Empty<FileHolder>();
		try
		{
			if (RmRegisterResources(session, (uint)paths.Length, paths, 0, IntPtr.Zero, 0, null) != 0)
				return Array.Empty<FileHolder>();

			uint needed = 0, count = 0, reasons = 0;
			var rc = RmGetList(session, out needed, ref count, null, ref reasons);
			if (rc == 0) return Array.Empty<FileHolder>();
			if (rc != ErrorMoreData) return Array.Empty<FileHolder>();

			var infos = new RmProcessInfo[needed + 4];
			count = (uint)infos.Length;
			if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0) return Array.Empty<FileHolder>();

			return infos.Take((int)count)
				.Select(i => new FileHolder(i.Process.ProcessId, NameOf(i)))
				.Distinct()
				.ToArray();
		}
		catch { return Array.Empty<FileHolder>(); }
		finally { RmEndSession(session); }
	}

	private static string NameOf(RmProcessInfo info)
	{
		try
		{
			using var p = Process.GetProcessById(info.Process.ProcessId);
			return p.ProcessName + ".exe";
		}
		catch { return string.IsNullOrEmpty(info.AppName) ? "?" : info.AppName; }
	}

	private const int CchRmSessionKey = 32;
	private const int ErrorMoreData = 234;

	[StructLayout(LayoutKind.Sequential)]
	private struct RmUniqueProcess
	{
		public int ProcessId;
		public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct RmProcessInfo
	{
		public RmUniqueProcess Process;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
		public int ApplicationType;
		public uint AppStatus;
		public uint TSSessionId;
		[MarshalAs(UnmanagedType.Bool)] public bool Restartable;
	}

	[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
	private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

	[DllImport("rstrtmgr.dll")]
	private static extern int RmEndSession(uint sessionHandle);

	[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
	private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[] fileNames,
		uint applicationCount, IntPtr applications, uint serviceCount, string[]? serviceNames);

	[DllImport("rstrtmgr.dll")]
	private static extern int RmGetList(uint sessionHandle, out uint procInfoNeeded, ref uint procInfo,
		[In, Out] RmProcessInfo[]? affectedApps, ref uint rebootReasons);
}
