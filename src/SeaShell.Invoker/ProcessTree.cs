using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SeaShell.Invoker;

/// <summary>
/// A script process and every process it starts, so they can be terminated together —
/// including children still running after the script itself has exited, which are no
/// longer reachable through the parent chain.
///
/// Windows: the script is assigned to a job object, which its descendants join.
/// Linux: the script's environment carries a variable unique to this tree, which its
/// descendants inherit; <see cref="Kill"/> finds them by it in <c>/proc/*/environ</c>.
/// A descendant that breaks away from the job or drops the variable escapes.
/// </summary>
internal sealed class ProcessTree : IDisposable
{
	private readonly byte[]? _marker;
	private Process? _root;
	private SafeFileHandle? _job;

	private ProcessTree(byte[]? marker) => _marker = marker;

	/// <summary>Prepare <paramref name="psi"/> so the tree can be followed. Call before starting it.</summary>
	public static ProcessTree Prepare(ProcessStartInfo psi)
	{
		if (OperatingSystem.IsWindows())
			return new ProcessTree(null);

		var name = $"SEASHELL_TREE_{Guid.NewGuid():N}";
		psi.Environment[name] = "1";
		return new ProcessTree(Encoding.ASCII.GetBytes($"{name}=1"));
	}

	/// <summary>
	/// Track the started script. Windows: assign it to the job right away. The script does
	/// not start children before that: its runtime is still starting, and Sea's module
	/// initializer holds script code until the invoker connects, which comes later.
	/// </summary>
	public void Attach(Process root)
	{
		_root = root;
		if (OperatingSystem.IsWindows())
			_job = TryCreateJob(root);
	}

	/// <summary>Terminate the script and every process it started. Best effort; never throws.</summary>
	public void Kill()
	{
		if (_root == null) return;

		if (OperatingSystem.IsWindows())
		{
			if (_job != null)
				TerminateJobObject(_job, unchecked((uint)-1));
			// Also along the parent chain: without a job, or for a child started before the
			// script joined it. Windows keeps a child's parent id after the parent exits.
			try { _root.Kill(entireProcessTree: true); } catch { }
			return;
		}

		try
		{
			if (!_root.HasExited)
				_root.Kill(entireProcessTree: true);
		}
		catch { }

		// Orphans: children of an exited process were re-parented away from the tree.
		// Repeat while kills land, for children forked during a pass.
		for (var pass = 0; pass < 3 && KillMarked() > 0; pass++) { }
	}

	public void Dispose()
	{
		// No kill-on-close: closing the job leaves its processes running
		_job?.Dispose();
	}

	// ── Linux ───────────────────────────────────────────────────────────

	private int KillMarked()
	{
		var killed = 0;
		string[] dirs;
		try { dirs = Directory.GetDirectories("/proc"); }
		catch { return 0; }

		foreach (var dir in dirs)
		{
			if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId)
				continue;
			try
			{
				if (!HasEntry(File.ReadAllBytes(Path.Combine(dir, "environ")), _marker!))
					continue;
				using var process = Process.GetProcessById(pid);
				process.Kill();
				killed++;
			}
			catch { } // exited meanwhile, or another user's process
		}
		return killed;
	}

	/// <summary>Whether a NUL-separated environment block holds <paramref name="entry"/> as a whole entry.</summary>
	private static bool HasEntry(ReadOnlySpan<byte> environ, ReadOnlySpan<byte> entry)
	{
		while (!environ.IsEmpty)
		{
			var end = environ.IndexOf((byte)0);
			if ((end < 0 ? environ : environ[..end]).SequenceEqual(entry))
				return true;
			environ = end < 0 ? default : environ[(end + 1)..];
		}
		return false;
	}

	// ── Windows ─────────────────────────────────────────────────────────

	private static SafeFileHandle? TryCreateJob(Process root)
	{
		try
		{
			var job = CreateJobObject(IntPtr.Zero, null);
			if (job.IsInvalid)
			{
				job.Dispose();
				return null;
			}

			// A child that asks to break away (CREATE_BREAKAWAY_FROM_JOB) may, as it could without the job.
			// This limit is only accepted through the extended information class.
			var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
			limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_BREAKAWAY_OK;
			if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())
				&& AssignProcessToJobObject(job, root.SafeHandle))
				return job;

			job.Dispose();
			return null;
		}
		catch
		{
			return null;
		}
	}

	private const int JobObjectExtendedLimitInformation = 9;
	private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public UIntPtr MinimumWorkingSetSize;
		public UIntPtr MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public UIntPtr Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IO_COUNTERS
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	{
		public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
		public IO_COUNTERS IoInfo;
		public UIntPtr ProcessMemoryLimit;
		public UIntPtr JobMemoryLimit;
		public UIntPtr PeakProcessMemoryUsed;
		public UIntPtr PeakJobMemoryUsed;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetInformationJobObject(SafeFileHandle hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int infoLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, SafeProcessHandle hProcess);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool TerminateJobObject(SafeFileHandle hJob, uint uExitCode);
}
