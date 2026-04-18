using System;
using System.IO;
using System.Threading.Tasks;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>
/// Orchestrates <c>--system</c> install/uninstall/status by dispatching the
/// operation to a transient SYSTEM-context scheduled task (see
/// <see cref="SystemOpRunner"/>). The elevated interactive user stages a
/// <c>.cmd</c> wrapper; SYSTEM runs it; we mirror the captured output back.
/// </summary>
internal static class SystemOps
{
	private const string StageRoot = @"C:\ProgramData\seashell-stage";

	private static string SystemSeaShellExe =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.Windows),
			"System32", "config", "systemprofile", ".dotnet", "tools", "seashell.exe");

	public static async Task<int> InstallAsync(string version, Action<string> writeLine, Action<string> writeErr)
	{
		var nupkgSource = LocateOwnNupkg(version);
		if (nupkgSource == null)
		{
			writeErr($"seashell: couldn't find own nupkg (looked under {DefaultToolStoreNupkgPath(version)}).");
			writeErr("           SYSTEM install needs a copy of the nupkg to stage; re-install the tool globally and retry.");
			return 1;
		}

		var stageDir = Path.Combine(StageRoot, $"install-{Guid.NewGuid():N}");
		var nupkgName = Path.GetFileName(nupkgSource);
		var sentinel = "%STAGE%op.result"; // cmd.exe variable, expanded at runtime

		// %~dp0 returns the stage path WITH a trailing backslash. Passing that
		// as `--add-source "%STAGE%"` lets cmd parse `\"` as an escaped quote —
		// dotnet then sees a bogus source and silently falls back to other
		// feeds (disastrous with --ignore-failed-sources). Strip the trailing
		// backslash via `%STAGE:~0,-1%` for the --add-source argument.
		var cmd =
			"@echo off\r\n" +
			"setlocal enabledelayedexpansion\r\n" +
			"set STAGE=%~dp0\r\n" +
			"set STAGE_NOSLASH=%STAGE:~0,-1%\r\n" +
			$"set VERSION={version}\r\n" +
			"\r\n" +
			"dotnet tool uninstall -g SeaShell >nul 2>&1\r\n" +
			"dotnet tool install -g SeaShell --add-source \"%STAGE_NOSLASH%\" --version %VERSION% > \"%STAGE%op.output\" 2>&1\r\n" +
			"if errorlevel 1 (\r\n" +
			$"    >\"{sentinel}\" echo EXITCODE=!errorlevel!\r\n" +
			$"    >>\"{sentinel}\" echo stage=install-tool-failed\r\n" +
			"    exit /b !errorlevel!\r\n" +
			")\r\n" +
			"\r\n" +
			"\"%SYSTEMROOT%\\System32\\config\\systemprofile\\.dotnet\\tools\\seashell.exe\" install >> \"%STAGE%op.output\" 2>&1\r\n" +
			"set EC=!errorlevel!\r\n" +
			$">\"{sentinel}\" echo EXITCODE=!EC!\r\n" +
			$">>\"{sentinel}\" echo stage=install-complete\r\n" +
			"exit /b !EC!\r\n";

		Directory.CreateDirectory(stageDir);
		File.Copy(nupkgSource, Path.Combine(stageDir, nupkgName), overwrite: true);

		writeLine("  SYSTEM install: staging nupkg, firing one-shot task as SYSTEM...");
		var result = await SystemOpRunner.RunAsync(stageDir, cmd, "Install",
			timeout: TimeSpan.FromSeconds(300), log: msg => writeLine($"  {msg}"));

		MirrorOutput(result.Output, writeLine);
		if (result.FailureReason != null) writeErr($"  seashell: {result.FailureReason}");
		return result.ExitCode == 0 ? 0 : (result.ExitCode > 0 ? result.ExitCode : 1);
	}

	public static async Task<int> UninstallAsync(Action<string> writeLine, Action<string> writeErr)
	{
		if (!File.Exists(SystemSeaShellExe))
		{
			writeLine("  No SYSTEM install detected — nothing to uninstall.");
			return 0;
		}

		var stageDir = Path.Combine(StageRoot, $"uninstall-{Guid.NewGuid():N}");
		var cmd =
			"@echo off\r\n" +
			"setlocal enabledelayedexpansion\r\n" +
			"set STAGE=%~dp0\r\n" +
			$"set SEASHELL={SystemSeaShellExe}\r\n" +
			"\r\n" +
			"\"%SEASHELL%\" uninstall > \"%STAGE%op.output\" 2>&1\r\n" +
			"set EC=!errorlevel!\r\n" +
			"dotnet tool uninstall -g SeaShell >> \"%STAGE%op.output\" 2>&1\r\n" +
			">\"%STAGE%op.result\" echo EXITCODE=!EC!\r\n" +
			">>\"%STAGE%op.result\" echo stage=uninstall-complete\r\n" +
			"exit /b !EC!\r\n";

		writeLine("  SYSTEM uninstall: firing one-shot task as SYSTEM...");
		var result = await SystemOpRunner.RunAsync(stageDir, cmd, "Uninstall",
			timeout: TimeSpan.FromSeconds(180), log: msg => writeLine($"  {msg}"));

		MirrorOutput(result.Output, writeLine);
		if (result.FailureReason != null) writeErr($"  seashell: {result.FailureReason}");
		return result.ExitCode == 0 ? 0 : (result.ExitCode > 0 ? result.ExitCode : 1);
	}

	public static async Task<int> StatusAsync(Action<string> writeLine, Action<string> writeErr)
	{
		if (!File.Exists(SystemSeaShellExe))
		{
			writeLine("  No SYSTEM install detected.");
			return 0;
		}

		var stageDir = Path.Combine(StageRoot, $"status-{Guid.NewGuid():N}");
		var cmd =
			"@echo off\r\n" +
			"setlocal enabledelayedexpansion\r\n" +
			"set STAGE=%~dp0\r\n" +
			$"set SEASHELL={SystemSeaShellExe}\r\n" +
			"\r\n" +
			"\"%SEASHELL%\" > \"%STAGE%op.output\" 2>&1\r\n" +
			"set EC=!errorlevel!\r\n" +
			">\"%STAGE%op.result\" echo EXITCODE=!EC!\r\n" +
			"exit /b !EC!\r\n";

		var result = await SystemOpRunner.RunAsync(stageDir, cmd, "Status",
			timeout: TimeSpan.FromSeconds(60), log: _ => { });

		MirrorOutput(result.Output, writeLine);
		if (result.FailureReason != null) writeErr($"  seashell: {result.FailureReason}");
		return result.ExitCode == 0 ? 0 : (result.ExitCode > 0 ? result.ExitCode : 1);
	}

	private static string? LocateOwnNupkg(string version)
	{
		var candidate = DefaultToolStoreNupkgPath(version);
		return File.Exists(candidate) ? candidate : null;
	}

	private static string DefaultToolStoreNupkgPath(string version)
	{
		// dotnet global-tool install layout:
		//   %USERPROFILE%\.dotnet\tools\.store\{id}\{ver}\{id}\{ver}\{id}.{ver}.nupkg
		// `Environment.ProcessPath` is %USERPROFILE%\.dotnet\tools\seashell.exe,
		// so go up one and descend into .store.
		var toolsDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
		var ver = version.ToLowerInvariant();
		return Path.Combine(toolsDir, ".store", "seashell", ver, "seashell", ver, $"seashell.{ver}.nupkg");
	}

	private static void MirrorOutput(string captured, Action<string> writeLine)
	{
		if (string.IsNullOrEmpty(captured)) return;
		foreach (var line in captured.Replace("\r\n", "\n").Split('\n'))
		{
			if (line.Length > 0) writeLine(line);
		}
	}
}
