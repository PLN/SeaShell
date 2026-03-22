namespace SeaShell;

/// <summary>
/// Required by DOTNET_STARTUP_HOOKS. When SeaShell.Script.dll is specified as a
/// startup hook, the .NET runtime calls this before the application's Main().
/// This gives pre-compiled binaries that don't reference SeaShell.Script full
/// Sea context (IPC channel, lifecycle events, etc.).
/// </summary>
internal static class StartupHook
{
	internal static void Initialize() => Sea.EnsureInitialized();
}
