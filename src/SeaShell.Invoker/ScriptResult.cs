namespace SeaShell.Invoker;

/// <summary>
/// Result of running a script. Contains exit code and optionally captured output
/// (when using <see cref="OutputMode.Capture"/>).
/// </summary>
public sealed record ScriptResult(
	int ExitCode,
	string StandardOutput,
	string StandardError,
	int ExitDelay = 0)
{
	public bool Success => ExitCode == 0;
}
