using SeaShell.Ipc;

namespace SeaShell.Invoker;

/// <summary>
/// Compiled script artifacts returned by the daemon. This is the shared currency
/// between compilation (daemon) and execution (Invoker).
/// </summary>
public sealed record CompiledScript(
	string AssemblyPath,
	string? DepsJsonPath,
	string? RuntimeConfigPath,
	string? ManifestPath,
	string? StartupHookPath,
	bool DirectExe,
	bool Elevated,
	bool Watch,
	bool Restart,
	byte MutexScope = 0,
	bool MutexAttach = false)
{
	/// <summary>Create from a daemon RunResponse.</summary>
	public static CompiledScript? FromRunResponse(RunResponse response)
	{
		if (!response.Success || response.AssemblyPath == null)
			return null;

		return new CompiledScript(
			response.AssemblyPath,
			response.DepsJsonPath,
			response.RuntimeConfigPath,
			response.ManifestPath,
			response.StartupHookPath,
			response.DirectExe,
			response.Elevated,
			response.Watch,
			response.Restart,
			response.MutexScope,
			response.MutexAttach);
	}
}
