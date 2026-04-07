// NO NAMESPACE — DOTNET_STARTUP_HOOKS requires the type to be named "StartupHook"
// in the global (root) namespace. The runtime uses Assembly.GetType("StartupHook")
// which only matches types without a namespace.

/// <summary>
/// Required by DOTNET_STARTUP_HOOKS. When SeaShell.Script.dll is specified as a
/// startup hook, the .NET runtime calls this before the application's Main().
/// This gives pre-compiled binaries that don't reference SeaShell.Script full
/// Sea context (IPC channel, lifecycle events, etc.).
/// </summary>
internal static class StartupHook
{
	internal static void Initialize() => SeaShell.Sea.EnsureInitialized();
}
