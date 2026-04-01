using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SeaShell.Cli;

static class ConsoleHelper
{
	/// <summary>
	/// If the console is ephemeral (double-clicked), show an interactive countdown
	/// before the window closes. The script controls the duration via Sea.ExitDelay,
	/// which is reported back through the script pipe's EXIT message.
	/// </summary>
	public static void ExitDelay(bool isConsoleEphemeral)
	{
		if (!isConsoleEphemeral) return;

		var delay = ScriptRunner.LastExitDelay;
		if (delay > 0 && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			ConsoleDelayer.Delay(delay);
	}

	/// <summary>
	/// Detect whether this console window will close when the process exits
	/// (e.g., double-clicked .exe with no parent shell).
	///
	/// Uses parent process detection: if the parent is a known terminal/shell
	/// process, the console is persistent. If the parent is explorer.exe or
	/// unknown, the console is ephemeral (will close on exit).
	///
	/// This is more robust than GetConsoleProcessList counting, which varies
	/// across .NET versions (in-process hosting vs child process), terminal
	/// types, and file association launches.
	/// </summary>
	public static bool IsConsoleEphemeral()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return false; // Linux: no ephemeral console concern

		try
		{
			var parentPid = GetParentProcessId();
			if (parentPid == 0) return false; // can't determine → safe default

			using var parent = Process.GetProcessById((int)parentPid);
			var name = parent.ProcessName.ToLowerInvariant();

			// Known terminal/shell processes → console is persistent
			return name is not (
				"cmd" or "powershell" or "pwsh" or           // Windows shells
				"bash" or "sh" or "zsh" or "mintty" or       // Git Bash / MSYS2
				"windowsterminal" or "wt" or                  // Windows Terminal
				"conhost" or "openconsole" or                 // Console hosts
				"alacritty" or "wezterm-gui" or               // Third-party terminals
				"code" or "devenv" or                         // IDEs
				"ssh" or "sshd" or                            // Remote sessions
				"cscs" or "dotnet"                            // Script/build hosts
			);
		}
		catch
		{
			return false; // on error, don't delay
		}
	}

	// ── Parent process ID via NtQueryInformationProcess ─────────────────

	private static uint GetParentProcessId()
	{
		try
		{
			using var current = Process.GetCurrentProcess();
			var pbi = new PROCESS_BASIC_INFORMATION();
			var status = NtQueryInformationProcess(
				current.Handle, 0, ref pbi,
				(uint)Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
			return status == 0 ? (uint)(nint)pbi.InheritedFromUniqueProcessId : 0;
		}
		catch { return 0; }
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PROCESS_BASIC_INFORMATION
	{
		public nint Reserved1;
		public nint PebBaseAddress;
		public nint Reserved2_0;
		public nint Reserved2_1;
		public nint UniqueProcessId;
		public nint InheritedFromUniqueProcessId;
	}

	[DllImport("ntdll.dll")]
	private static extern int NtQueryInformationProcess(
		nint processHandle, int processInformationClass,
		ref PROCESS_BASIC_INFORMATION processInformation,
		uint processInformationLength, out uint returnLength);
}
