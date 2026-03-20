using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Ipc;
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

		var pipeName = $"seashell-{Guid.NewGuid():N}";

		var psi = new ProcessStartInfo
		{
			FileName = "dotnet",
			WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};

		psi.Environment["SEASHELL_PIPE"] = pipeName;

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

		// Connect to script's pipe server
		var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		pipe.Connect(5000);
		await using var channel = new MessageChannel(pipe);

		// Send ScriptInit with manifest data
		await channel.SendAsync(BuildScriptInit(compiled, args, workingDirectory), ct);

		// Read output and pipe messages concurrently.
		// Must drain the channel to unblock the script's ProcessExit flush.
		var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
		var stderrTask = proc.StandardError.ReadToEndAsync(ct);
		var channelTask = DrainChannelAsync(channel, ct);

		await proc.WaitForExitAsync(ct);

		var stdout = await stdoutTask;
		var stderr = await stderrTask;

		return new ScriptResult(proc.ExitCode, stdout, stderr);
	}

	// ── Helpers ─────────────────────────────────────────────────────────

	private static ScriptInit BuildScriptInit(ScriptCompiler.CompileResult compiled, string[]? args, string? workingDirectory)
	{
		string? scriptPath = null;
		string[]? sources = null;
		Dictionary<string, string>? packages = null;
		string[]? assemblies = null;

		if (compiled.ManifestPath != null && File.Exists(compiled.ManifestPath))
		{
			try
			{
				var json = File.ReadAllText(compiled.ManifestPath);
				var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
				var manifest = JsonSerializer.Deserialize<ManifestData>(json, opts);
				if (manifest != null)
				{
					scriptPath = manifest.ScriptPath;
					sources = manifest.Sources;
					packages = manifest.Packages;
					assemblies = manifest.Assemblies;
				}
			}
			catch { }
		}

		return new ScriptInit(
			scriptPath,
			workingDirectory ?? Environment.CurrentDirectory,
			args ?? Array.Empty<string>(),
			sources, packages, assemblies,
			false, 0, 0, null, false);
	}

	private static async Task DrainChannelAsync(MessageChannel channel, CancellationToken ct)
	{
		try
		{
			while (true)
			{
				var msg = await channel.ReceiveAsync(ct);
				if (msg == null) break;
			}
		}
		catch { }
	}

	private sealed class ManifestData
	{
		public string? ScriptPath { get; set; }
		public string[]? Sources { get; set; }
		public Dictionary<string, string>? Packages { get; set; }
		public string[]? Assemblies { get; set; }
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
