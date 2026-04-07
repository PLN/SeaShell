namespace SeaShell.Invoker;

/// <summary>
/// How the script process's standard I/O streams are handled.
/// </summary>
public enum OutputMode
{
	/// <summary>Process inherits parent stdio. Output goes directly to the console. Used by CLI.</summary>
	Inherit,

	/// <summary>Stdout/stderr are redirected and captured in <see cref="ScriptResult"/>. Used by Host and ServiceHost.</summary>
	Capture,
}
