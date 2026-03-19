using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Engine;

namespace SeaShell.Host;

/// <summary>
/// Embeddable C# scripting host. Compiles and runs scripts without requiring
/// the SeaShell daemon. Use this to add scripting to your application.
///
/// Usage:
///   var host = new ScriptHost();
///   var result = await host.RunAsync("path/to/script.cs");
///   Console.WriteLine($"Exit: {result.ExitCode}, Output: {result.StandardOutput}");
///
/// Or compile and inspect without running:
///   var compiled = host.Compile("path/to/script.cs");
///   // compiled.AssemblyPath, compiled.DepsJsonPath, etc.
///
/// Or run a code snippet directly:
///   var result = await host.RunSnippetAsync("Console.WriteLine(42);");
/// </summary>
public sealed class ScriptHost
{
	private readonly ScriptCompiler _compiler;

	public ScriptHost()
	{
		_compiler = new ScriptCompiler();
	}

	// ── Compile ─────────────────────────────────────────────────────────

	/// <summary>
	/// Compile a script file. Returns the compiled artifacts (DLL, runtimeconfig, deps.json, manifest).
	/// Does not run the script.
	/// </summary>
	public ScriptCompiler.CompileResult Compile(string scriptPath, string[]? args = null)
	{
		return _compiler.Compile(scriptPath, args ?? Array.Empty<string>());
	}

	// ── Run ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Compile and run a script file. Captures stdout/stderr.
	/// </summary>
	public async Task<ScriptResult> RunAsync(
		string scriptPath,
		string[]? args = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environmentVars = null,
		CancellationToken ct = default)
	{
		var compiled = _compiler.Compile(scriptPath, args ?? Array.Empty<string>());
		if (!compiled.Success)
			return new ScriptResult(-1, "", compiled.Error ?? "Compilation failed");

		return await ExecuteAsync(compiled, args, workingDirectory, environmentVars, ct);
	}

	/// <summary>
	/// Compile and run a code snippet. Wraps it in a minimal script file.
	/// </summary>
	public async Task<ScriptResult> RunSnippetAsync(
		string code,
		string[]? args = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environmentVars = null,
		CancellationToken ct = default)
	{
		// Write snippet to a temp file, compile it, run it, clean up
		var tempDir = Path.Combine(Path.GetTempPath(), "seashell", "snippets");
		Directory.CreateDirectory(tempDir);
		var tempFile = Path.Combine(tempDir, $"snippet_{Guid.NewGuid():N}.cs");

		try
		{
			File.WriteAllText(tempFile, code);
			return await RunAsync(tempFile, args, workingDirectory, environmentVars, ct);
		}
		finally
		{
			try { File.Delete(tempFile); } catch { }
		}
	}

	/// <summary>
	/// Run an already-compiled script. Use this when you want to compile once
	/// and run multiple times, or when you need to inspect the compilation result.
	/// </summary>
	public async Task<ScriptResult> ExecuteAsync(
		ScriptCompiler.CompileResult compiled,
		string[]? args = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environmentVars = null,
		CancellationToken ct = default)
	{
		if (!compiled.Success)
			return new ScriptResult(-1, "", compiled.Error ?? "Compilation failed");

		var psi = new ProcessStartInfo
		{
			FileName = "dotnet",
			WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};

		if (compiled.ManifestPath != null)
			psi.Environment["SEASHELL_MANIFEST"] = compiled.ManifestPath;

		if (environmentVars != null)
		{
			foreach (var (key, value) in environmentVars)
				psi.Environment[key] = value;
		}

		psi.ArgumentList.Add("exec");
		if (compiled.RuntimeConfigPath != null)
		{
			psi.ArgumentList.Add("--runtimeconfig");
			psi.ArgumentList.Add(compiled.RuntimeConfigPath);
		}
		if (compiled.DepsJsonPath != null)
		{
			psi.ArgumentList.Add("--depsfile");
			psi.ArgumentList.Add(compiled.DepsJsonPath);
		}
		psi.ArgumentList.Add(compiled.AssemblyPath!);
		if (args != null)
		{
			foreach (var a in args)
				psi.ArgumentList.Add(a);
		}

		using var proc = Process.Start(psi)!;

		// Read output concurrently to avoid deadlocks
		var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
		var stderrTask = proc.StandardError.ReadToEndAsync(ct);

		await proc.WaitForExitAsync(ct);

		var stdout = await stdoutTask;
		var stderr = await stderrTask;

		return new ScriptResult(proc.ExitCode, stdout, stderr);
	}

	// ── NuGet ───────────────────────────────────────────────────────────

	/// <summary>Access the NuGet resolver for pre-downloading packages.</summary>
	public NuGetResolver NuGet => _compiler.NuGetResolver;

	/// <summary>
	/// Check all cached NuGet packages for updates and download new versions.
	/// Call this on whatever timer suits your application (e.g., every 8 hours).
	///
	/// Usage:
	///   var updater = host.CreateUpdater();
	///   updater.Log += msg => logger.Info(msg);
	///   var result = await updater.CheckForUpdatesAsync();
	/// </summary>
	public NuGetUpdater CreateUpdater() => new(_compiler.NuGetResolver);

	// ── Result ──────────────────────────────────────────────────────────

	public sealed record ScriptResult(
		int ExitCode,
		string StandardOutput,
		string StandardError)
	{
		public bool Success => ExitCode == 0;
	}
}
