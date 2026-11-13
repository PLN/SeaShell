using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using SeaShell;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Windows uninstall from the installed copy (<c>current\bin\seashell.exe</c>):
/// a running image cannot be deleted, so this process copies the
/// bootstrapper's own files to a temporary directory, re-runs the uninstall
/// from there (<c>--relocated-from &lt;pid&gt;</c>), forwards its output and exit
/// code, and finally starts the copy once more, hidden, as
/// <c>finish-uninstall</c> (<see cref="Finish"/>): it waits for this process to
/// exit and removes its version directory — under the install lock, only if
/// it is still retired then, and only when nothing runs from it — and then
/// has the temporary copy removed after it exits itself.
/// </summary>
internal static class SelfRelocation
{
	/// <summary>True when this bootstrapper runs from inside a SeaShell data directory.</summary>
	public static bool Needed()
	{
		if (!OperatingSystem.IsWindows()) return false;
		var self = AppContext.BaseDirectory;
		return Inside(self, SeaShellIdentity.Current.DataDir) || Inside(self, SeaShellIdentity.LocalSystem.DataDir);
	}

	public static int Run(string[] args)
	{
		var sourceDir = AppContext.BaseDirectory;
		var copyDir = Path.Combine(Path.GetTempPath(), $"{CopyPrefix}{Guid.NewGuid():N}");
		try
		{
			Directory.CreateDirectory(copyDir);
			foreach (var file in BootstrapperFiles(sourceDir))
				File.Copy(file, Path.Combine(copyDir, Path.GetFileName(file)), overwrite: true);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"  ERROR: could not copy the bootstrapper to {copyDir} to uninstall from there: {ex.Message}");
			Console.Error.WriteLine("         Nothing was changed. Run the uninstall from a global tool or 'dnx SeaShell uninstall' instead.");
			TryDelete(copyDir);
			return 1;
		}

		var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
		var command = SelfCommand.Build(
			RelocatedPath(Environment.ProcessPath, sourceDir, copyDir),
			entry == null ? null : Path.Combine(copyDir, Path.GetFileName(entry)));
		if (command == null)
		{
			Console.Error.WriteLine("  ERROR: cannot determine how to start the relocated bootstrapper; nothing was changed.");
			TryDelete(copyDir);
			return 1;
		}

		Console.WriteLine($"  Uninstalling from a temporary copy ({copyDir}), as this seashell runs from the install itself.");
		Console.WriteLine();
		var arguments = command.PrefixArguments.Concat(args).Concat(new[] { "--relocated-from", Environment.ProcessId.ToString() }).ToList();
		int exit;
		try
		{
			exit = DotnetHost.RunInherited(command.FileName, arguments);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"  ERROR: could not run the relocated bootstrapper: {ex.Message}");
			TryDelete(copyDir);
			return 1;
		}

		// What this process holds goes once it has exited: its own version
		// directory, if it is still retired by then (Finish decides), and the copy.
		var ownVersion = typeof(SelfRelocation).Assembly.GetName().Version?.ToString(4);
		var ownVersionDir = ownVersion == null ? null : SeaShellPaths.VersionDirIn(SeaShellIdentity.Current.DataDir, ownVersion);
		var finishArgs = command.PrefixArguments.Concat(new[] { FinishCommand, "--relocated-from", Environment.ProcessId.ToString() }).ToList();
		if (ownVersionDir != null && Directory.Exists(ownVersionDir)) finishArgs.Add(ownVersionDir);
		StartHidden(command.FileName, finishArgs, $"the removal of {ownVersionDir ?? "this seashell's version directory"} and {copyDir} after exit");
		return exit;
	}

	/// <summary>The internal command of the hidden helper (not in <c>--help</c>).</summary>
	public const string FinishCommand = "finish-uninstall";

	/// <summary>
	/// <c>finish-uninstall --relocated-from &lt;pid&gt; [versionDir]</c>, run hidden
	/// from the temporary copy: wait for the original seashell to exit, then
	/// remove its version directory if it is still retired (no <c>current</c>
	/// pointing at it, no <c>daemon.path</c> marker: an immediate reinstall of
	/// the same version keeps it), under the install lock so that a reinstall
	/// cannot run in between, and with the in-use guard of
	/// <see cref="VersionDirRemoval"/>. Finally the copy this runs from is
	/// removed after it exits.
	/// </summary>
	public static int Finish(BootstrapperOptions options)
	{
		if (options.RelocatedFrom is { } pid)
		{
			try
			{
				using var original = Process.GetProcessById(pid);
				original.WaitForExit();
			}
			catch (ArgumentException) { } // already gone
		}

		var exit = 0;
		foreach (var dir in options.Arguments) exit |= RemoveIfRetired(dir);
		RemoveOwnCopyAfterExit();
		return exit;
	}

	private static int RemoveIfRetired(string dir)
	{
		var identity = SeaShellIdentity.Current;
		var dataDir = Path.GetFullPath(identity.DataDir).TrimEnd(Path.DirectorySeparatorChar);
		if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(dir)), dataDir, StringComparison.OrdinalIgnoreCase))
			return 1; // only a version directory of this identity

		// Still in use for a moment after the original exits (image sections,
		// scanners), or the lock held by a reinstall: retry for two minutes.
		var deadline = DateTime.UtcNow.AddMinutes(2);
		while (true)
		{
			using (var installLock = InstallLock.TryAcquire(identity))
			{
				if (installLock != null)
				{
					if (!Directory.Exists(dir)) return 0;
					var keep = UninstallPlanner.KeepDeferredDir(VersionDiscovery.ParseVersionDir(Path.GetFileName(dir)),
						VersionDiscovery.GetCurrentVersion(dataDir), MarkerFile.Exists(dir, MarkerFile.DaemonPath));
					if (keep != null) return 0;

					var (result, _) = VersionDirRemoval.Remove(dir);
					if (result == VersionDirRemovalResult.Removed)
					{
						try { if (!Directory.EnumerateFileSystemEntries(dataDir).Any()) Directory.Delete(dataDir); } catch { }
						return 0;
					}
					if (result == VersionDirRemovalResult.PartlyRemoved) return 1;
				}
			}
			if (DateTime.UtcNow >= deadline) return 1;
			Thread.Sleep(1000);
		}
	}

	private const string CopyPrefix = "seashell-uninstall-";

	// The copy (seashell-uninstall-* in the temp directory) cannot delete
	// itself while it runs: a hidden PowerShell waits for this process.
	private static void RemoveOwnCopyAfterExit()
	{
		var copyDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
		if (!Path.GetFileName(copyDir).StartsWith(CopyPrefix, StringComparison.OrdinalIgnoreCase)
			|| !Inside(copyDir, Path.GetTempPath()))
			return;

		static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
		var script = new StringBuilder();
		script.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
		script.AppendLine($"Wait-Process -Id {Environment.ProcessId}");
		script.AppendLine($"$d = {Quote(copyDir)}");
		script.AppendLine("for ($i = 0; $i -lt 30 -and (Test-Path -LiteralPath $d); $i++) { Remove-Item -LiteralPath $d -Recurse -Force; if (Test-Path -LiteralPath $d) { Start-Sleep -Seconds 1 } }");
		StartHidden(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
			new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand",
				Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString())) },
			$"the removal of {copyDir} after exit");
	}

	private static void StartHidden(string fileName, IEnumerable<string> arguments, string what)
	{
		try
		{
			// Not the inherited working directory: it may be inside the directory being removed.
			var psi = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
			foreach (var a in arguments) psi.ArgumentList.Add(a);
			Process.Start(psi)?.Dispose();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"  WARNING: could not start {what} ({ex.Message}); delete what is left by hand.");
		}
	}

	// The apphost lives next to its dll; under `dotnet seashell.dll` the
	// process path is the dotnet host and stays as it is.
	private static string? RelocatedPath(string? processPath, string sourceDir, string copyDir) =>
		processPath != null && Inside(processPath, sourceDir)
			? Path.Combine(copyDir, Path.GetFileName(processPath))
			: processPath;

	/// <summary>
	/// The files the bootstrapper needs: <c>seashell.*</c> and the runtime
	/// assemblies its deps.json lists (not the 40 MB of daemon, compiler and
	/// CLI next to it). Every file of the directory when deps.json cannot be read.
	/// </summary>
	private static IEnumerable<string> BootstrapperFiles(string dir)
	{
		var all = Directory.GetFiles(dir);
		try
		{
			using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "seashell.deps.json")));
			var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var target in deps.RootElement.GetProperty("targets").EnumerateObject())
			foreach (var library in target.Value.EnumerateObject())
			{
				if (!library.Value.TryGetProperty("runtime", out var runtime)) continue;
				foreach (var asset in runtime.EnumerateObject()) names.Add(Path.GetFileName(asset.Name));
			}
			var needed = all.Where(f => names.Contains(Path.GetFileName(f))
				|| Path.GetFileName(f).StartsWith("seashell.", StringComparison.OrdinalIgnoreCase)).ToArray();
			return names.All(n => needed.Any(f => string.Equals(Path.GetFileName(f), n, StringComparison.OrdinalIgnoreCase)))
				? needed
				: all;
		}
		catch { return all; }
	}

	private static bool Inside(string path, string dir)
	{
		var full = Path.GetFullPath(path);
		var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
	}

	private static void TryDelete(string dir)
	{
		try { Directory.Delete(dir, true); } catch { }
	}
}
