using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace SeaShell.Engine;

/// <summary>
/// Generates per-script apphost executables by patching the .NET SDK's
/// apphost template with the compiled DLL name. The apphost is a native
/// PE/ELF binary (~157KB) that launches the script directly via hostfxr,
/// giving proper process names in Task Manager and correct ProcessPath.
/// </summary>
static class AppHostWriter
{
	private static readonly ILogger _log = Log.ForContext(typeof(AppHostWriter));

	/// <summary>
	/// The well-known placeholder embedded in the apphost template binary.
	/// Present twice: once in .rdata (detection sentinel) and once in .data
	/// (the writable path buffer). We patch the last occurrence.
	/// </summary>
	private static readonly byte[] Placeholder =
		Encoding.UTF8.GetBytes("c3ab8ff13720e8ad9047dd39466b3c89");

	/// <summary>Max DLL name length (UTF-8 bytes, null-terminated). Matches HostModel limit.</summary>
	private const int MaxPathBytes = 1024;

	/// <summary>
	/// Find the apphost template in the .NET SDK.
	/// Returns null if the SDK is not installed or the template is not found.
	/// </summary>
	public static string? FindAppHostTemplate()
	{
		try
		{
			var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
			var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
			var sdkDir = Path.Combine(dotnetRoot, "sdk");
			if (!Directory.Exists(sdkDir)) return null;

			// Pick the latest SDK version (same pattern as ArtifactWriter.FindRefAssemblyDir)
			var versionDir = Directory.GetDirectories(sdkDir)
				.Where(d => Version.TryParse(Path.GetFileName(d), out _))
				.OrderByDescending(d => Version.Parse(Path.GetFileName(d)!))
				.FirstOrDefault();
			if (versionDir == null) return null;

			var templateName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
				? "apphost.exe" : "apphost";
			var templatePath = Path.Combine(versionDir, "AppHostTemplate", templateName);

			return File.Exists(templatePath) ? templatePath : null;
		}
		catch (Exception ex)
		{
			_log.Debug("Failed to locate apphost template: {Message}", ex.Message);
			return null;
		}
	}

	/// <summary>
	/// Generate an apphost executable for the given DLL in the output directory.
	/// Returns the path to the generated executable, or null on failure.
	/// </summary>
	/// <param name="outputDir">Cache output directory containing the compiled DLL.</param>
	/// <param name="dllName">DLL filename (e.g., "MyScript.dll"). Not a full path.</param>
	/// <param name="windowsGuiSubsystem">
	/// If true and running on Windows, set the PE subsystem to WINDOWS_GUI (2)
	/// instead of CONSOLE (3), suppressing console window allocation.
	/// </param>
	public static string? Generate(string outputDir, string dllName, bool windowsGuiSubsystem)
	{
		var templatePath = FindAppHostTemplate();
		if (templatePath == null)
		{
			_log.Debug("Apphost template not found, skipping apphost generation");
			return null;
		}

		var dllNameBytes = Encoding.UTF8.GetBytes(dllName);
		if (dllNameBytes.Length >= MaxPathBytes)
		{
			_log.Warning("DLL name too long for apphost ({Length} bytes): {DllName}",
				dllNameBytes.Length, dllName);
			return null;
		}

		try
		{
			var data = File.ReadAllBytes(templatePath);

			// Find the LAST occurrence of the placeholder — that's the writable .data buffer.
			// The first occurrence is a read-only sentinel in .rdata.
			var offset = FindLastOccurrence(data, Placeholder);
			if (offset < 0)
			{
				_log.Warning("Apphost template missing placeholder — format may have changed");
				return null;
			}

			// Write DLL name (null-terminated, rest of buffer already zeroed)
			Array.Copy(dllNameBytes, 0, data, offset, dllNameBytes.Length);
			data[offset + dllNameBytes.Length] = 0;
			// Clear remainder of the original placeholder/hash bytes
			for (int i = dllNameBytes.Length + 1; i < Placeholder.Length * 2; i++)
			{
				if (offset + i < data.Length)
					data[offset + i] = 0;
			}

			// Patch PE subsystem for window mode (Windows only)
			if (windowsGuiSubsystem && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				PatchPeSubsystem(data, peSubsystem: 2); // IMAGE_SUBSYSTEM_WINDOWS_GUI

			// Determine output filename
			var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
				? Path.ChangeExtension(dllName, ".exe")
				: Path.GetFileNameWithoutExtension(dllName);
			var exePath = Path.Combine(outputDir, exeName);

			if (File.Exists(exePath))
				return exePath; // Already generated (cache identity)

			File.WriteAllBytes(exePath, data);

			// Set executable permission on Linux
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				try
				{
					File.SetUnixFileMode(exePath,
						UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
						UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
						UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
				}
				catch (Exception ex)
				{
					_log.Debug("Failed to set executable bit on {Path}: {Message}", exePath, ex.Message);
				}
			}

			_log.Debug("Generated apphost {ExePath} for {DllName}", exePath, dllName);
			return exePath;
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "Failed to generate apphost for {DllName}", dllName);
			return null;
		}
	}

	/// <summary>
	/// Check if an apphost was previously generated in the output directory.
	/// Returns the path if found, null otherwise. Used on cache-hit paths.
	/// </summary>
	public static string? FindExisting(string outputDir, string scriptName)
	{
		var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
			? $"{scriptName}.exe" : scriptName;
		var exePath = Path.Combine(outputDir, exeName);
		return File.Exists(exePath) ? exePath : null;
	}

	/// <summary>
	/// Find the last occurrence of a byte pattern in the data.
	/// The apphost template contains the placeholder twice: first in .rdata (read-only
	/// sentinel), then in .data (the writable path buffer). We need the latter.
	/// </summary>
	private static int FindLastOccurrence(byte[] data, byte[] pattern)
	{
		int last = -1;
		for (int start = 0; start <= data.Length - pattern.Length; )
		{
			var found = ((ReadOnlySpan<byte>)data.AsSpan(start)).IndexOf(pattern);
			if (found < 0) break;
			last = start + found;
			start = last + pattern.Length;
		}
		return last;
	}

	/// <summary>
	/// Patch the PE subsystem field in the binary data.
	/// PE subsystem 2 = WINDOWS_GUI (no console), 3 = CONSOLE (default).
	/// </summary>
	private static void PatchPeSubsystem(byte[] data, ushort peSubsystem)
	{
		if (data.Length < 0x40) return;

		// e_lfanew at offset 0x3C points to PE signature
		var peOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C));
		if (peOffset <= 0 || peOffset + 0x5E > data.Length) return;

		// Verify PE signature "PE\0\0"
		if (data[peOffset] != 'P' || data[peOffset + 1] != 'E' ||
		    data[peOffset + 2] != 0 || data[peOffset + 3] != 0)
			return;

		// Subsystem is at PE offset + 0x5C (OptionalHeader.Subsystem for PE32+)
		var subsystemOffset = peOffset + 0x5C;
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(subsystemOffset), peSubsystem);
	}
}
