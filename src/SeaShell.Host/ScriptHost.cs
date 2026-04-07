using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
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
		ScriptConnection? connection = null,
		CancellationToken ct = default)
	{
		var compiled = _compiler.Compile(scriptPath, args ?? Array.Empty<string>());
		if (!compiled.Success)
			return new ScriptResult(-1, "", compiled.Error ?? "Compilation failed");

		return await ExecuteAsync(compiled, args, workingDirectory, environmentVars, connection, ct);
	}

	/// <summary>
	/// Compile and run a code snippet. Wraps it in a minimal script file.
	/// </summary>
	public async Task<ScriptResult> RunSnippetAsync(
		string code,
		string[]? args = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environmentVars = null,
		ScriptConnection? connection = null,
		CancellationToken ct = default)
	{
		var tempDir = SeaShellPaths.SnippetsDir;
		Directory.CreateDirectory(tempDir);
		var tempFile = Path.Combine(tempDir, $"snippet_{Guid.NewGuid():N}.cs");

		try
		{
			File.WriteAllText(tempFile, code);
			return await RunAsync(tempFile, args, workingDirectory, environmentVars, connection, ct);
		}
		finally
		{
			try { File.Delete(tempFile); } catch { }
		}
	}

	/// <summary>
	/// Run an already-compiled script. Use this when you want to compile once
	/// and run multiple times, or when you need to inspect the compilation result.
	/// Supports script-initiated reload and watch mode (file-change hot-swap).
	/// </summary>
	public async Task<ScriptResult> ExecuteAsync(
		ScriptCompiler.CompileResult compiled,
		string[]? args = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environmentVars = null,
		ScriptConnection? connection = null,
		CancellationToken ct = default)
	{
		if (!compiled.Success)
			return new ScriptResult(-1, "", compiled.Error ?? "Compilation failed");

		var reloadCount = 0;
		string? stateBase64 = null;
		var currentCompiled = compiled;
		var scriptPath = ReadScriptPathFromManifest(compiled);
		ScriptWatcher? watcher = null;
		SemaphoreSlim? fileChangeSignal = null;

		// Watch mode: monitor source files for changes
		if (compiled.Watch && scriptPath != null)
		{
			var sourceFiles = _compiler.GetLastResolvedSources(scriptPath);
			watcher = new ScriptWatcher(sourceFiles);
			fileChangeSignal = new SemaphoreSlim(0);
			watcher.Changed += _ => fileChangeSignal.Release();
		}

		try
		{
			while (true)
			{
				var result = await RunOneInstanceAsync(
					currentCompiled, args, workingDirectory, environmentVars,
					connection, reloadCount, stateBase64, watcher != null, ct);

				if (!result.ReloadRequested)
				{
					return new ScriptResult(result.ExitCode, result.Stdout, result.Stderr);
				}

				// Script or file change requested reload
				if (result.ClearCache && scriptPath != null)
				{
					_compiler.NuGetResolver.InvalidateCache();
					CompilationCache.ClearScript(
						SeaShellPaths.CacheDir,
						Path.GetFileNameWithoutExtension(scriptPath));
				}

				// Recompile (Host owns the compiler — no daemon needed)
				if (scriptPath != null)
				{
					var recompiled = _compiler.Compile(scriptPath, args ?? Array.Empty<string>());
					if (recompiled.Success)
					{
						currentCompiled = recompiled;
						// Update watcher with new source files
						if (watcher != null)
						{
							watcher.Dispose();
							var newSources = _compiler.GetLastResolvedSources(scriptPath);
							watcher = new ScriptWatcher(newSources);
							fileChangeSignal = new SemaphoreSlim(0);
							watcher.Changed += _ => fileChangeSignal!.Release();
						}
					}
				}

				stateBase64 = result.State;
				reloadCount++;
			}
		}
		finally
		{
			watcher?.Dispose();
		}
	}

	/// <summary>Run a single instance, returning when it exits or requests reload.</summary>
	private async Task<InstanceResult> RunOneInstanceAsync(
		ScriptCompiler.CompileResult compiled,
		string[]? args,
		string? workingDirectory,
		Dictionary<string, string>? environmentVars,
		ScriptConnection? connection,
		int reloadCount, string? stateBase64, bool watch,
		CancellationToken ct)
	{
		var pipeName = $"seashell-{Guid.NewGuid():N}";
		var psi = BuildProcessStartInfo(compiled, args, workingDirectory, environmentVars, pipeName);

		using var proc = Process.Start(psi)!;

		// Ensure the child process is always killed if we exit unexpectedly
		// (e.g., cancellation token fires mid-reload before graceful shutdown branch)
		try
		{

		// Connect to script's pipe server
		var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		try
		{
			pipe.Connect(5000);
		}
		catch
		{
			// Pipe connect failed — run without Sea context
			await proc.WaitForExitAsync();
			var o = await proc.StandardOutput.ReadToEndAsync();
			var e = await proc.StandardError.ReadToEndAsync();
			return new InstanceResult(proc.ExitCode, o, e, false, false, null);
		}

		await using var channel = new MessageChannel(pipe);

		if (connection != null)
			connection.Channel = channel;

		await channel.SendAsync(BuildScriptInit(compiled, args, workingDirectory,
			reloadCount, stateBase64, watch), ct);

		// Drain channel with reload awareness
		var reloadRequests = Channel.CreateUnbounded<(string? reason, bool clearCache)>();
		var drainTask = DrainChannelAsync(channel, connection, reloadRequests.Writer, CancellationToken.None);

		// Don't pass ct to stdout/stderr — we need to read remaining output during shutdown
		var stdoutTask = proc.StandardOutput.ReadToEndAsync();
		var stderrTask = proc.StandardError.ReadToEndAsync();
		var reloadTask = reloadRequests.Reader.ReadAsync(CancellationToken.None).AsTask();
		var exitTask = proc.WaitForExitAsync();

		// Also race against the cancellation token (service stopping)
		var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var ctReg = ct.Register(() => cancelTcs.TrySetResult());

		var completed = await Task.WhenAny(exitTask, reloadTask, cancelTcs.Task);

		if (completed == exitTask)
		{
			// Script exited normally
			var stdout = await stdoutTask;
			var stderr = await stderrTask;
			if (connection != null) connection.Channel = null;
			return new InstanceResult(proc.ExitCode, stdout, stderr, false, false, null);
		}

		if (completed == cancelTcs.Task)
		{
			// Service stopping — graceful shutdown
			try { await channel.SendAsync(new ScriptStop()); } catch { }

			// Wait for graceful exit
			if (!proc.WaitForExit(5000))
				try { proc.Kill(entireProcessTree: false); } catch { }

			var stdout = await stdoutTask;
			var stderr = await stderrTask;
			if (connection != null) connection.Channel = null;
			return new InstanceResult(proc.HasExited ? proc.ExitCode : -1, stdout, stderr, false, false, null);
		}

		// Script requested reload
		var (reason, clearCache) = await reloadTask;

		// Signal script to save state and exit
		string? state = null;
		try { await channel.SendAsync(new ScriptReload()); } catch { }
		try
		{
			using var cts = new CancellationTokenSource(5000);
			state = await WaitForStateAsync(channel, cts.Token);
		}
		catch { }

		// Kill old process
		if (!proc.WaitForExit(3000))
			try { proc.Kill(entireProcessTree: false); } catch { }

		var so = await stdoutTask;
		var se = await stderrTask;
		if (connection != null) connection.Channel = null;
		return new InstanceResult(0, so, se, true, clearCache, state);

		}
		finally
		{
			// Kill child process if still running (catches cancellation mid-reload, pipe failures, etc.)
			if (!proc.HasExited)
				try { proc.Kill(entireProcessTree: false); } catch { }
		}
	}

	private sealed record InstanceResult(
		int ExitCode, string Stdout, string Stderr,
		bool ReloadRequested, bool ClearCache, string? State);

	private static ProcessStartInfo BuildProcessStartInfo(
		ScriptCompiler.CompileResult compiled, string[]? args,
		string? workingDirectory, Dictionary<string, string>? environmentVars,
		string pipeName)
	{
		ProcessStartInfo psi;

		if (compiled.DirectExe)
		{
			psi = new ProcessStartInfo
			{
				FileName = compiled.AssemblyPath!,
				WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
		}
		else
		{
			psi = new ProcessStartInfo
			{
				FileName = "dotnet",
				WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
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
		}

		psi.Environment["SEASHELL_PIPE"] = pipeName;

		if (compiled.StartupHookPath != null)
			psi.Environment["DOTNET_STARTUP_HOOKS"] = compiled.StartupHookPath;

		if (environmentVars != null)
		{
			foreach (var (key, value) in environmentVars)
				psi.Environment[key] = value;
		}

		if (args != null)
		{
			foreach (var a in args)
				psi.ArgumentList.Add(a);
		}

		return psi;
	}

	// ── Helpers ─────────────────────────────────────────────────────────

	private static ScriptInit BuildScriptInit(ScriptCompiler.CompileResult compiled, string[]? args,
		string? workingDirectory, int reloadCount = 0, string? stateBase64 = null, bool watch = false)
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
			false, 0, reloadCount, stateBase64, watch);
	}

	private static string? ReadScriptPathFromManifest(ScriptCompiler.CompileResult compiled)
	{
		if (compiled.ManifestPath == null || !File.Exists(compiled.ManifestPath))
			return null;
		try
		{
			var json = File.ReadAllText(compiled.ManifestPath);
			var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
			var manifest = JsonSerializer.Deserialize<ManifestData>(json, opts);
			return manifest?.ScriptPath;
		}
		catch { return null; }
	}

	private static async Task DrainChannelAsync(MessageChannel channel, ScriptConnection? connection,
		ChannelWriter<(string?, bool)> reloadWriter, CancellationToken ct)
	{
		try
		{
			while (true)
			{
				var msg = await channel.ReceiveAsync(ct);
				if (msg == null) break;

				switch (msg.Value.Type)
				{
					case MessageType.ScriptMessage when connection != null:
						var sm = (ScriptMessage)msg.Value.Message;
						try { connection.RaiseMessageReceived(sm.Payload, sm.Topic); } catch { }
						break;

					case MessageType.ScriptReloadRequest:
						var req = (ScriptReloadRequest)msg.Value.Message;
						reloadWriter.TryWrite((req.Reason, req.ClearCache));
						break;
				}
			}
		}
		catch { }
	}

	private static async Task<string?> WaitForStateAsync(MessageChannel channel, CancellationToken ct)
	{
		try
		{
			while (true)
			{
				var msg = await channel.ReceiveAsync(ct);
				if (msg == null) return null;

				if (msg.Value.Type == MessageType.ScriptState)
				{
					var s = (ScriptState)msg.Value.Message;
					return s.Data;
				}
				if (msg.Value.Type == MessageType.ScriptExit)
					return null;
			}
		}
		catch { return null; }
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

	// ── ScriptConnection ────────────────────────────────────────────────

	/// <summary>
	/// Bidirectional application messaging between Host and Script.
	/// Create an instance, subscribe to MessageReceived, and pass it to
	/// ExecuteAsync/RunAsync/RunSnippetAsync.
	/// </summary>
	public sealed class ScriptConnection
	{
		/// <summary>
		/// Fires when the script sends an application message.
		/// Parameters: payload (raw bytes), topic (optional routing key).
		/// Called on the channel-drain thread.
		/// </summary>
		public event Action<byte[], string?>? MessageReceived;

		internal MessageChannel? Channel;

		/// <summary>Send a binary message to the running script.</summary>
		public async Task SendAsync(byte[] payload, string? topic = null, CancellationToken ct = default)
		{
			var ch = Channel ?? throw new InvalidOperationException("Script is not connected");
			await ch.SendAsync(new HostMessage(payload, topic), ct);
		}

		/// <summary>Send a string message (UTF-8) to the running script.</summary>
		public async Task SendAsync(string payload, string? topic = null, CancellationToken ct = default) =>
			await SendAsync(Encoding.UTF8.GetBytes(payload), topic, ct);

		/// <summary>Send a ScriptStop message to gracefully shut down the script.</summary>
		public async Task StopAsync(CancellationToken ct = default)
		{
			var ch = Channel ?? throw new InvalidOperationException("Script is not connected");
			await ch.SendAsync(new ScriptStop(), ct);
		}

		internal void RaiseMessageReceived(byte[] payload, string? topic) =>
			MessageReceived?.Invoke(payload, topic);
	}
}
