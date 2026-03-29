using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SeaShell.Invoker;
using SeaShell.Protocol;

namespace SeaShell.Host;

/// <summary>
/// Embeddable C# scripting host. Compiles and runs scripts via the SeaShell daemon.
///
/// Usage:
///   var host = new ScriptHost();
///   var result = await host.RunAsync("path/to/script.cs");
///   Console.WriteLine($"Exit: {result.ExitCode}, Output: {result.StandardOutput}");
///
/// Or compile without running:
///   var compiled = await host.CompileAsync("path/to/script.cs");
///
/// Or run a code snippet directly:
///   var result = await host.RunSnippetAsync("Console.WriteLine(42);");
/// </summary>
public sealed class ScriptHost
{
	private readonly ScriptInvoker _invoker;
	private readonly string _daemonAddress;

	/// <param name="log">Optional log callback for daemon/execution progress messages.</param>
	public ScriptHost(Action<string>? log = null)
	{
		_invoker = new ScriptInvoker(log);
		_daemonAddress = TransportEndpoint.GetDaemonAddress(TransportEndpoint.CurrentUserIdentity, TransportEndpoint.CurrentVersion);
	}

	// ── Compile ─────────────────────────────────────────────────────────

	/// <summary>
	/// Compile a script file via the daemon. Returns the compiled artifacts.
	/// </summary>
	public async Task<CompiledScript?> CompileAsync(
		string scriptPath, string[]? args = null, CancellationToken ct = default)
	{
		return await _invoker.CompileAsync(scriptPath, args ?? Array.Empty<string>(), _daemonAddress, ct);
	}

	// ── Run ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Compile and run a script file. Captures stdout/stderr.
	/// Handles elevation, watch mode, and script-initiated reload automatically.
	/// </summary>
	public async Task<ScriptResult> RunAsync(
		string scriptPath,
		string[]? args = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environmentVars = null,
		ScriptConnection? connection = null,
		CancellationToken ct = default)
	{
		return await _invoker.RunAsync(
			scriptPath, args ?? Array.Empty<string>(), _daemonAddress,
			OutputMode.Capture, connection,
			workingDirectory, environmentVars, ct);
	}

	/// <summary>
	/// Compile and run a code snippet. Uses content-based hashing so identical
	/// snippets are cached by the daemon without redundant compilation.
	/// </summary>
	public async Task<ScriptResult> RunSnippetAsync(
		string code,
		string[]? args = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environmentVars = null,
		ScriptConnection? connection = null,
		CancellationToken ct = default)
	{
		// Deterministic path from content — identical code always maps to the same
		// snippet name, so the daemon's compilation cache hash is stable.
		var codeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)))[..16];
		var scriptName = $"snippet_{codeHash}";
		var snippetPath = Path.Combine(SeaShellPaths.SnippetsDir, $"{scriptName}.cs");

		Directory.CreateDirectory(SeaShellPaths.SnippetsDir);
		File.WriteAllText(snippetPath, code);

		return await RunAsync(snippetPath, args, workingDirectory, environmentVars, connection, ct);
	}

	/// <summary>
	/// Run an already-compiled script. Use this when you want to compile once
	/// and run multiple times.
	/// </summary>
	public async Task<ScriptResult> ExecuteAsync(
		CompiledScript compiled,
		string[]? args = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environmentVars = null,
		ScriptConnection? connection = null,
		CancellationToken ct = default)
	{
		return await _invoker.ExecuteAsync(
			compiled, args ?? Array.Empty<string>(),
			_daemonAddress, null,
			OutputMode.Capture, connection,
			workingDirectory, environmentVars, ct);
	}
}
