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

	/// <summary>
	/// The run was cancelled through its token: the script was asked to stop and, if it
	/// had not exited within five seconds, terminated. A run with captured output also
	/// terminates every process the script started. The output is what was captured
	/// until then. An elevated script started through the elevator is only asked to stop.
	/// </summary>
	public bool Cancelled { get; init; }
}
