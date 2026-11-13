using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Makes SeaShell reachable by the interactive user: the bin directory in the
/// Windows user PATH, or a ~/.local/bin symlink on Unix. Never applied to the
/// SYSTEM target — that would edit the invoking admin's HKCU, and SYSTEM
/// consumers find binaries via markers rather than PATH.
///
/// Windows PATH edits read and write the raw registry value
/// (<c>HKCU\Environment\Path</c>, or the machine's) and keep its value kind:
/// <c>Environment.Get/SetEnvironmentVariable</c> would expand every
/// <c>%VAR%</c> entry and turn REG_EXPAND_SZ into REG_SZ for the whole
/// variable. Only the entries SeaShell adds, moves or removes change.
/// </summary>
internal static class PathEnvironment
{
	/// <summary><c>~/.local/bin</c>, where the Unix symlinks live.</summary>
	public static string LocalBinDir() =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

	private static readonly string[] s_linkNames = { "sea", "seashell" };

	/// <summary>
	/// Put the current bin directory on the user's PATH. Windows: ahead of
	/// <c>%USERPROFILE%\.dotnet\tools</c>, so the installed <c>seashell</c>
	/// wins over a global tool's alias. Unix: <c>sea</c> and <c>seashell</c>
	/// symlinks in <c>~/.local/bin</c>; the profile's PATH order is not ours to
	/// change, so report which <c>seashell</c> actually resolves.
	/// </summary>
	public static void Add(InstallTarget target)
	{
		var pathDir = SeaShellPaths.CurrentBinDir;

		if (target.IsWindows && OperatingSystem.IsWindows())
		{
			if (RawPath.Read(EnvironmentVariableTarget.User) is not { } existing) return;
			var toolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
			var wasPresent = WindowsPathEntries.Strip(existing.Value, new[] { pathDir }, RawPath.Expand).Changed;
			var result = WindowsPathEntries.EnsureBefore(existing.Value, pathDir, toolsDir, RawPath.Expand);
			if (!result.Changed) return;

			if (!RawPath.Write(EnvironmentVariableTarget.User, existing, result.Path)) return;
			Console.WriteLine(wasPresent
				? $"  Moved {pathDir} ahead of {toolsDir} in user PATH"
				: $"  Added {pathDir} to user PATH");
			Console.WriteLine("  NOTE: Restart your terminal for PATH changes to take effect.");
		}
		else if (!target.IsWindows)
		{
			var localBin = LocalBinDir();
			Directory.CreateDirectory(localBin);

			foreach (var name in s_linkNames)
			{
				var link = Path.Combine(localBin, name);
				var linkTarget = Path.Combine(pathDir, name);
				try
				{
					if (File.Exists(link) || IsDanglingSymlink(link)) File.Delete(link);
					File.CreateSymbolicLink(link, linkTarget);
					Console.WriteLine($"  Symlinked {name} -> {link}");
				}
				catch (Exception ex) { Console.Error.WriteLine($"  Warning: {name} symlink failed: {ex.Message}"); }
			}

			ReportSeashellOnPath(Path.Combine(localBin, "seashell"));
		}
	}

	/// <summary>
	/// Unix: say which <c>seashell</c> the current PATH resolves first, when it
	/// is not the installed copy (a global tool in <c>~/.dotnet/tools</c> ahead
	/// of <c>~/.local/bin</c> would answer <c>seashell update</c> itself).
	/// </summary>
	private static void ReportSeashellOnPath(string installedLink)
	{
		foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
		{
			var candidate = Path.Combine(dir, "seashell");
			if (!File.Exists(candidate)) continue;
			if (string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(installedLink), StringComparison.Ordinal))
				Console.WriteLine($"  'seashell' on PATH resolves to {installedLink}");
			else
			{
				Console.WriteLine($"  NOTE: 'seashell' on PATH resolves to {candidate} first, not to {installedLink}.");
				Console.WriteLine($"        Put {Path.GetDirectoryName(installedLink)} earlier in PATH, or remove the other one.");
			}
			return;
		}
	}

	private static bool IsDanglingSymlink(string path)
	{
		try { return new FileInfo(path).LinkTarget != null; }
		catch { return false; }
	}

	/// <summary>
	/// Undo <see cref="Add"/>. Drops pre-0.5.0 layout entries too. Returns
	/// what could not be removed (null when everything went), for the
	/// uninstall's completeness report.
	/// </summary>
	public static string? Remove(InstallTarget target)
	{
		if (target.IsWindows)
		{
			// Drop both the current junction path and the pre-0.5.0 flat bin path.
			return StripEntries(new[]
			{
				SeaShellPaths.CurrentBinDir,
				Path.Combine(SeaShellPaths.DataDir, "bin"),
			}) ? null : "user PATH entry";
		}

		string? failed = null;
		foreach (var name in s_linkNames)
		{
			var link = Path.Combine(LocalBinDir(), name);
			if (!File.Exists(link) && !IsDanglingSymlink(link)) continue;
			// Only our own symlink: a file of that name that is not a link to a
			// SeaShell bin directory belongs to someone else.
			var linkTarget = SafeLinkTarget(link);
			if (linkTarget == null || !linkTarget.Contains("seashell", StringComparison.Ordinal)) continue;
			try { File.Delete(link); Console.WriteLine($"  Removed symlink {link}"); }
			catch (Exception ex)
			{
				Console.Error.WriteLine($"  Warning: could not remove {link}: {ex.Message}");
				failed = link;
			}
		}
		return failed;
	}

	private static string? SafeLinkTarget(string path)
	{
		try { return new FileInfo(path).LinkTarget; }
		catch { return null; }
	}

	/// <summary>Drop a stale flat-layout bin entry left by a pre-0.5.0 install.</summary>
	public static void StripLegacyBinEntries(InstallTarget target) =>
		StripEntries(new[] { Path.Combine(target.DataDir, "bin") });

	/// <summary>Drop user-scope PATH entries under the pre-0.5.0 common install.</summary>
	public static void StripUserEntriesWhere(Func<string, bool> isUnwanted) =>
		Apply(EnvironmentVariableTarget.User, isUnwanted);

	/// <summary>Machine-scope PATH entries; needs elevation to write.</summary>
	public static void StripMachineEntriesWhere(Func<string, bool> isUnwanted) =>
		Apply(EnvironmentVariableTarget.Machine, isUnwanted);

	private static bool StripEntries(string[] unwanted) =>
		Apply(EnvironmentVariableTarget.User, entry => Array.Exists(unwanted, u => WindowsPathEntries.SameDirectory(entry, u)));

	// True when nothing needed removing or the write succeeded.
	private static bool Apply(EnvironmentVariableTarget scope, Func<string, bool> isUnwanted)
	{
		if (!OperatingSystem.IsWindows()) return true;
		if (RawPath.Read(scope) is not { } existing) return false;
		var stripped = WindowsPathEntries.StripWhere(existing.Value, isUnwanted, RawPath.Expand);
		if (!stripped.Changed) return true;
		if (!RawPath.Write(scope, existing, stripped.Path)) return false;
		Console.WriteLine($"  Removed stale PATH entries ({scope})");
		return true;
	}

	/// <summary>The persisted PATH value as stored in the registry.</summary>
	/// <param name="Value">Raw value, <c>%VAR%</c> references unexpanded; empty when absent.</param>
	/// <param name="Kind">REG_EXPAND_SZ or REG_SZ; null when the value does not exist yet.</param>
	private readonly record struct RawPathValue(string Value, RegistryValueKind? Kind);

	[SupportedOSPlatform("windows")]
	private static class RawPath
	{
		private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

		public static string Expand(string entry) => Environment.ExpandEnvironmentVariables(entry);

		// Reading the persisted PATH can be denied by policy; that is not an error
		// worth failing the install over. Null = could not read.
		public static RawPathValue? Read(EnvironmentVariableTarget scope)
		{
			try
			{
				using var key = Open(scope, writable: false);
				if (key == null) return new RawPathValue("", null);
				var name = ValueName(key);
				if (name == null) return new RawPathValue("", null);
				var value = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
				return new RawPathValue(value, key.GetValueKind(name));
			}
			catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
			{
				Console.WriteLine($"  PATH ({scope}): access denied, skipping ({ex.Message})");
				return null;
			}
		}

		/// <summary>
		/// Write <paramref name="newValue"/> with the kind the value had (a new
		/// value is REG_EXPAND_SZ, as Windows creates it), then tell running
		/// programs (Explorer) that the environment changed.
		/// </summary>
		public static bool Write(EnvironmentVariableTarget scope, RawPathValue existing, string newValue)
		{
			try
			{
				using var key = Open(scope, writable: true)
					?? throw new IOException($"cannot open the {scope} environment key");
				var kind = existing.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString
					? existing.Kind.Value
					: RegistryValueKind.ExpandString;
				key.SetValue(ValueName(key) ?? "Path", newValue, kind);
			}
			catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
			{
				Console.WriteLine($"  PATH ({scope}): write denied, skipping ({ex.Message})");
				return false;
			}
			BroadcastEnvironmentChange();
			return true;
		}

		private static RegistryKey? Open(EnvironmentVariableTarget scope, bool writable) => scope switch
		{
			EnvironmentVariableTarget.Machine => Registry.LocalMachine.OpenSubKey(MachineKey, writable),
			_ => writable ? Registry.CurrentUser.CreateSubKey("Environment", true) : Registry.CurrentUser.OpenSubKey("Environment"),
		};

		// The value is usually "Path", but registry names are case-insensitive and
		// an existing name is kept as it is.
		private static string? ValueName(RegistryKey key) =>
			Array.Find(key.GetValueNames(), n => string.Equals(n, "Path", StringComparison.OrdinalIgnoreCase));

		private static void BroadcastEnvironmentChange()
		{
			try
			{
				SendMessageTimeout(new IntPtr(0xffff), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "Environment",
					0x0002 /* SMTO_ABORTIFHUNG */, 5000, out _);
			}
			catch { }
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
			uint flags, uint timeout, out IntPtr result);
	}
}
