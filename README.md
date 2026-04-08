# {~} SeaShell

A C# and VB.NET scripting engine with a persistent daemon, NuGet support, hot-swap, REPL, and an embeddable host library. Built on Roslyn.

## Install

Requires .NET 10 SDK.

```
dotnet tool install -g SeaShell
seashell install
```

On Windows, run elevated (`gsudo seashell install`) to also register the
elevator task for UAC-free `//sea_elevate` scripts and the Windows Event Log
source. Without elevation the daemon is registered but the elevator is skipped.

For a system-wide install (SYSTEM/root), use `--system` with elevation:
```
gsudo seashell install --system      # Windows
sudo seashell install --system       # Linux
```
This installs to `%ProgramData%\seashell\bin` (Windows) or root's standard
location (Linux), adds to machine PATH, and skips Task Scheduler registration
— the daemon starts lazily on first `sea` invocation.

Run `seashell` with no arguments to see a dashboard with version, platform,
daemon/elevator state, and idle time. Use `seashell start` / `seashell stop`
to manage daemon lifecycle.

**Alternative** — standalone scripts (no dotnet tool):

Windows: `iex (irm https://raw.githubusercontent.com/PLN/SeaShell/main/install.ps1)`
Linux: `curl -fsSL https://raw.githubusercontent.com/PLN/SeaShell/main/install.sh | sh`

## Quick Start

```
sea                             Interactive REPL
sea script.cs                   Run a C# script
sea script.vb                   Run a VB.NET script
sea myapp.dll                   Run a pre-compiled .NET assembly
sea myapp.exe                   Run a single-file .exe
sea -i Humanizer.Core           REPL with NuGet packages preloaded
sea --associate .cs             Associate .cs files with SeaShell
seaw script.csw                 Run a window-mode script (no console)
seaw script.cs                  Run any script without a console window
```

NuGet packages are resolved automatically — including transitive dependencies and runtime-specific DLLs. Missing packages are downloaded on first use.

## Acknowledgements

SeaShell is inspired by and grateful to [CS-Script](https://github.com/oleg-shilo/cs-script) by Oleg Shilo. CS-Script pioneered C# scripting and has been a daily workhorse for years. SeaShell builds on the ideas CS-Script established — directive-based includes, NuGet references, and the principle that C# should be as easy to run as a shell script — while taking a different architectural approach with the persistent daemon and .deps.json generation.

## Features

- **Persistent daemon** — Roslyn stays warm in memory. Second run is instant.
- **NuGet just works** — Transitive dependency resolution via .nuspec walking. Auto-download. Runtime-specific and native DLLs handled via .deps.json generation.
- **Hot-swap** — Edit a `//sea_watch` script while it runs. SeaShell recompiles and swaps the process, with state handoff between instances.
- **REPL** — `sea` with no arguments drops into an interactive session. Variables and state carry across evaluations.
- **Elevation** — `//sea_elevate` scripts run elevated via a pre-registered Task Scheduler worker. No UAC prompts. Falls back to `gsudo sea script.cs` if the elevator isn't running. Ignored on Linux.
- **Binary running** — Run pre-compiled `.dll` and `.exe` files with full Sea context. Single-file executables, apphosts, and plain DLLs all supported. Companion `.sea.json` enables directives for binaries.
- **Script-initiated reload** — `Sea.RequestReload()` triggers recompilation and hot-swap from within a script. Optional `clearCache` for forced fresh builds. Works in both direct and watch mode.
- **Service hosting** — `SeaShell.Host` includes service hosting (namespace `SeaShell.ServiceHost`). Runs scripts as system services. Windows Service, systemd, runit, OpenRC, sysvinit — auto-detected. Zero-locking automatic updates via NuGet.
- **Embeddable** — `SeaShell.Host` lets you compile and run scripts from your own application. No daemon required. Supports reload and watch mode internally.
- **VB.NET support** — `.vb` files compile through the same pipeline with VB-native directives (`'sea_nuget`).
- **CS-Script compatible** — `//css_inc`, `//css_nuget` directives are recognized. Existing `.cscs` scripts work with zero changes.
- **Cross-platform** — Named pipes on Windows, Unix domain sockets on Linux. Shebangs supported (`#!/usr/bin/env sea`).
- **Mixed declarations** — Classes, records, and top-level statements can appear in any order. No more "move your class to the bottom."
- **Background NuGet updates** — The daemon checks all cached packages every 8 hours, across all configured sources including private feeds from nuget.config.

## Directives

| Directive | Purpose |
|---|---|
| `//sea_nuget Serilog` | NuGet package (auto-download, transitive resolution) |
| `//sea_nuget Serilog 4.0.0` | NuGet package with specific version |
| `//sea_inc MyLib.cs` | Include a source file |
| `//sea_incdir %ProgramData%/mylibs` | Add an include search directory (env vars expanded) |
| `//sea_ref path/to/lib.dll` | Explicit assembly reference |
| `//sea_webapp` | Enable ASP.NET Core shared framework |
| `//sea_elevate` | Run elevated (via Elevator or gsudo; ignored on Linux) |
| `//sea_watch` | Hot-swap: recompile and restart on source file changes |
| `//sea_restart` | Auto-restart on exit (with crash backoff) |
| `//sea_mutex` | Single instance (system scope). `//sea_mutex session` or `user` for narrower scope |
| `//sea_mutex_attach` | Blocked callers attach to the running instance instead of exiting |
| `//sea_window` | Window-mode flag (for seaw.exe file association) |
| `//sea_console` | Allocate a console window (for seaw.exe scripts that need stdout) |

VB.NET uses the `'sea_` prefix: `'sea_nuget Serilog`

CS-Script compatibility: `//css_inc`, `//css_nuget`, `//css_ref`, `//css_webapp` are all recognized.

### Include Search Order

1. Script's own directory
2. `//sea_incdir` paths (in order declared, across all sources)
3. `SEASHELL_INC` environment variable
4. Platform include directory (`%ProgramData%\seashell\inc\` or `/usr/local/share/seashell/inc/`)
5. CS-Script fallback: `%ProgramData%\cs-script\inc\`

### Environment Variable Expansion

`//sea_incdir` and `//sea_ref` support cross-platform environment variable expansion:

```
//sea_incdir %ProgramData%/mylibs       Windows-style
//sea_incdir $HOME/scripts              Unix-style
//sea_incdir ${XDG_DATA_HOME}/inc       Unix-style braced
//sea_incdir ~/shared                   Home shorthand
```

All styles work on all platforms.

## Sea Runtime API

Every script has access to the `Sea` static class:

```csharp
// Script identity
Sea.ScriptPath          // Absolute path to the main script
Sea.ScriptDir           // Directory containing the script
Sea.ScriptName          // Filename without extension
Sea.ScriptFile          // Filename with extension
Sea.StartDir            // CWD where 'sea' was invoked (never changed)

// Environment
Sea.IsElevated          // Running with admin/root privileges?
Sea.IsConsoleEphemeral  // Console will close on exit? (double-clicked)
Sea.ExitDelay           // Seconds to delay before exit (default: 7, set to 0 to skip)
Sea.Args                // Script arguments

// Build metadata
Sea.Sources             // All compiled source files (main + includes)
Sea.Packages            // NuGet packages resolved (name -> version)
Sea.Assemblies          // All managed DLL paths

// Hot-swap lifecycle
Sea.IsReload            // True if this is a hot-swap restart
Sea.ReloadCount         // Number of reloads so far
Sea.IsWatchMode         // True when running with //sea_watch
Sea.IsShuttingDown      // True after Reloading/Stopping fires
Sea.ShutdownToken       // CancellationToken — cancelled on reload/stop
Sea.Reloading           // Event: script is about to be replaced
Sea.Stopping            // Event: clean stop requested (Ctrl+C)

// Restart
Sea.IsRestartMode       // True when running with //sea_restart
Sea.Restart             // Controls restart (default true, set false to stop)
Sea.RestartCount        // Number of restarts so far

// Mutex & Attach
Sea.MutexScope          // 0=None, 1=Session, 2=User, 3=System
Sea.MutexAttach         // True when //sea_mutex_attach is active
Sea.Attached            // Event: blocked caller attached (AttachContext)
Sea.IsWindowMode        // True when running as seaw.exe without console

// Script-initiated reload
Sea.RequestReload()                     // Trigger recompile + hot-swap
Sea.RequestReload(clearCache: true)     // Force fresh build (clear cache first)
Sea.RequestReloadAsync(...)             // Async variant

// Reload state handoff (max 8 KB)
Sea.SetReloadState(bytes)       // Pass state to next instance
Sea.SetReloadState(string)      // String convenience overload
Sea.GetReloadState()            // Retrieve state from previous instance
Sea.GetReloadStateString()      // String convenience overload

// Host↔Script messaging (when running via ScriptHost)
Sea.MessageReceived             // Event: Host sent a message (byte[] payload, string? topic)
Sea.SendMessage(bytes, topic)   // Send binary message to Host
Sea.SendMessage(string, topic)  // Send string message to Host (UTF-8)
Sea.SendMessageAsync(...)       // Async variants
```

## Hot-Swap Example

```csharp
//sea_watch

var counter = int.TryParse(Sea.GetReloadStateString(), out var c) ? c : 0;

Sea.Reloading += () => Sea.SetReloadState(counter.ToString());

while (!Sea.IsShuttingDown)
{
    counter++;
    Console.WriteLine($"counter={counter}");
    await Task.Delay(2000, Sea.ShutdownToken);
}
```

Edit and save the file — the counter picks up where it left off.

## Restart

`//sea_restart` scripts automatically restart when they exit:

```csharp
//sea_restart

Console.WriteLine($"Run #{Sea.RestartCount}");
await Task.Delay(10_000, Sea.ShutdownToken);
```

If the script exits within 5 seconds, crash backoff kicks in: 2s, 4s, 8s, 8s...
A successful run (longer than 5s) resets the backoff. Set `Sea.Restart = false` within
a script to prevent restart for that particular run.

## Script Mutex

`//sea_mutex` enforces single-instance execution. A second invocation exits immediately
with code 200 if another instance is already running.

```csharp
//sea_mutex
Console.WriteLine("Only one of me can run at a time");
await Task.Delay(60_000, Sea.ShutdownToken);
```

Three scopes control the mutex boundary:

| Directive | Scope |
|---|---|
| `//sea_mutex` | System-wide (default). One instance across all users and sessions. |
| `//sea_mutex user` | Per-user. Different users can each run one instance. |
| `//sea_mutex session` | Per-session. Same user can run one per login session. |

On Windows, scopes map to named kernel mutexes (`Global\`, default, `Local\`).
On Linux, file locks in scope-appropriate directories.

### Attach

`//sea_mutex_attach` extends the mutex: instead of exiting, blocked callers connect
to the running instance and exchange messages.

```csharp
//sea_mutex
//sea_mutex_attach

Sea.Attached += ctx =>
{
    Console.WriteLine($"Client attached from {ctx.WorkingDirectory}");
    Console.WriteLine($"Args: {string.Join(" ", ctx.Args)}");
    ctx.Send(Encoding.UTF8.GetBytes("hello from server"));
    ctx.Close(0);
};

Console.WriteLine("Listening for attach clients...");
await Task.Delay(Timeout.Infinite, Sea.ShutdownToken);
```

The `AttachContext` provides: `Args` (caller's arguments), `WorkingDirectory`
(caller's CWD), `Send(byte[])`, `Receive()`, and `Close(exitCode)`.

## seaw.exe (Window Mode)

`seaw.exe` is a WinExe-subsystem binary — no console window by default. Use it for
scripts that run in the background or show their own UI.

```
seaw background-task.csw        Run without a console window
seaw ui-app.cs                  Same — any script, not just .csw
```

Scripts that need stdout can request a console with `//sea_console`:

```csharp
//sea_console
Console.WriteLine("I have a console window");
```

When seaw.exe encounters an error without a console, it writes to the Windows Event
Log (SeaShell source) so errors aren't silently lost.

The `.csw` extension is a convention for window-mode scripts. Associate it with
seaw.exe via `seaw --associate .csw`.

## Embedding (SeaShell.Host)

```csharp
var host = new ScriptHost();

// Run a script file
var result = await host.RunAsync("path/to/script.cs");
Console.WriteLine(result.StandardOutput);

// Run a code snippet
var result = await host.RunSnippetAsync("""
    Console.WriteLine("Hello!");
    return 0;
    """);

// Compile once, run many times
var compiled = host.Compile("script.cs");
await host.ExecuteAsync(compiled);
await host.ExecuteAsync(compiled);

// Background NuGet updates on your schedule
var updater = host.CreateUpdater();
updater.Log += Console.WriteLine;
await updater.CheckForUpdatesAsync();
```

`Sea.IsConsoleEphemeral` is always `false` when running via Host. `Sea.ExitDelay` has no effect — the Host does not perform exit delays.

### Host↔Script Messaging

The Host and script can exchange binary messages during execution over the existing pipe:

```csharp
var conn = new ScriptHost.ScriptConnection();
conn.MessageReceived += (payload, topic) =>
    Console.WriteLine($"Script [{topic}]: {Encoding.UTF8.GetString(payload)}");

var result = await host.RunAsync("script.cs", connection: conn);
```

Script side:
```csharp
Sea.MessageReceived += (payload, topic) =>
    Sea.SendMessage("acknowledged", "reply");

Sea.SendMessage("{\"status\":\"ready\"}", "status");
```

Messages are binary (`byte[]`) with an optional `string` topic for routing. String convenience overloads encode as UTF-8.

## Service Hosting

Run any script or binary as a system service with automatic updates:

```csharp
using SeaShell.ServiceHost;

return await new ServiceHostBuilder()
    .ServiceName("myservice")
    .DisplayName("My Service")
    .Description("Runs my-script.cs as a system service")
    .RunScript("my-script.cs")           // or .RunAssembly("myapp.dll")
    .EnableNuGetUpdates(TimeSpan.FromHours(8))
    .RunAsync(args);
```

The binary handles both service mode and management commands:

```
myservice                   Run as foreground service (invoked by init system)
myservice install           Auto-detect platform, register service
myservice uninstall         Remove service registration
myservice start             Start the registered service
myservice stop              Stop the registered service
myservice status            Show service status
```

Supported init systems (auto-detected): Windows Service, systemd, runit, OpenRC, sysvinit.

**Zero-locking updates**: When `EnableNuGetUpdates` is configured, the service periodically checks for package updates. A new version produces a new compilation cache directory — the old process runs from the old directory, the new one starts from the new directory. No file locks are contested.

## Task Scheduler (Windows)

Register the daemon and optionally the elevator for automatic startup. Each is a separate, explicit registration:

```
sea --install-daemon              Register daemon (limited privileges)
sea --install-elevator            Register elevator (requires elevation once)
sea --uninstall-daemon            Remove daemon task
sea --uninstall-elevator          Remove elevator task
sea --start                       Start registered tasks
sea --stop                        Stop daemon and elevator
sea --status                      Show daemon and elevator status
```

The daemon starts automatically on first `sea` invocation even without Task Scheduler. When a task is registered, `sea` prefers starting it over spawning a separate process — this keeps the daemon under Task Scheduler management.

`--stop` uses IPC first (works for any running daemon, including on Linux), then ends Task Scheduler tasks. It reports actual state — no misleading output when nothing is running.

The elevator is optional — without it, elevated scripts fall back to `gsudo sea script.cs`. When the elevator task is registered but not running, `sea` auto-starts it on demand when an `//sea_elevate` script is run.

## Bootstrapper Commands

The `seashell` command (installed via `dotnet tool install -g SeaShell`) manages the
SeaShell installation:

```
seashell                          Dashboard: version, platform, daemon/elevator state
seashell install                  Extract archives, register daemon, update PATH
seashell install --system         System-wide install (requires elevation)
seashell uninstall                Remove binaries, daemon task, file associations
seashell uninstall --system       Remove system-wide install
seashell start                    Start daemon (and elevator if registered)
seashell stop                     Stop daemon and elevator
seashell status                   Show running state and versions
seashell schedule <script> <timing>  Schedule a script via Task Scheduler
seashell schedule                 List scheduled scripts
seashell unschedule <script>      Remove a scheduled script
```

`seashell install` and `seashell update` are identical — both extract the latest
archives and re-register tasks. On Windows, run elevated (`gsudo seashell install`)
to also register the elevator and Event Log source.

## Project Structure

```
SeaShell.Bootstrapper  Dotnet tool (seashell command) — install, uninstall, start, stop, status
SeaShell.Cli           CLI (sea.exe / seaw.exe) — argument parsing, script execution, exit delay
SeaShell.Invoker       Shared execution engine — compilation, mutex, restart, attach, daemon lifecycle
SeaShell.Daemon        Persistent compilation server, REPL host, file watcher
SeaShell.Elevator      Pre-elevated worker (connects to daemon, no public pipe)
SeaShell.Engine        Roslyn compiler, NuGet resolver, include system, .deps.json writer
SeaShell.Script        Sea runtime context (loaded into every script process)
SeaShell.Common        Shared message types, MessageChannel (MessagePack over System.IO.Pipelines)
SeaShell.Protocol      Daemon/Elevator protocol messages + transport (named pipes / UDS)
SeaShell.Host          Embeddable library + cross-platform service hosting (Windows Service, systemd, runit, OpenRC)
```

See [doc/ARCHITECTURE.md](doc/ARCHITECTURE.md) for the full architecture,
[doc/SECURITY.md](doc/SECURITY.md) for the security model, and
[doc/HISTORY.md](doc/HISTORY.md) for the changelog.

## File Association (Windows)

Register `.cs` files to open with SeaShell when double-clicked:

```
sea --associate              Associate .cs (default)
sea --associate .vb          Associate .vb
sea --unassociate .cs        Remove association
```

Per-user registration via HKCU — no elevation needed. The SeaShell icon appears on
associated files in Explorer.

## Building

```
dotnet build
```

Run from source:
```
dotnet run --project src/SeaShell.Cli -- script.cs
```

Pack NuGet packages:
```
dotnet pack -c Release -o ./nupkg
```

Install as a global tool (from local build):
```
dotnet tool install -g --add-source ./nupkg SeaShell
```

## Notes

### Data Directory

All runtime data lives in a persistent per-user directory (not temp):

| Context | Path |
|---------|------|
| Windows user | `%LOCALAPPDATA%\seashell\` |
| Windows SYSTEM | `%ProgramData%\seashell\` |
| Linux user | `~/.local/share/seashell/` |
| Linux root | `/var/lib/seashell/` |

Override with `SEASHELL_DATA` environment variable. Contains: `cache/` (compiled scripts), `daemon/` (staged daemon), `elevator/` (staged elevator), `snippets/` (REPL temp files).

The daemon and elevator binaries are staged to this directory before launching. This eliminates DLL lock conflicts — `dotnet build` always succeeds, even while the daemon is running.

### Compilation Cache

Compiled scripts are cached in `{DataDir}/cache/`. The cache key is a SHA256 hash of:
- All source files (main script + includes, by content)
- All resolved NuGet package versions (name@version)
- The Engine and Script assembly timestamps

A cache hit skips Roslyn entirely — the daemon returns the existing DLL. The cache invalidates automatically when:
- Any source file changes
- A NuGet package is updated to a new version
- The SeaShell engine or script runtime is rebuilt
- The .NET SDK is updated (Engine DLL is recompiled)

### Exit Delay (Windows)

When `sea.exe` detects it was launched from an ephemeral console (e.g., double-clicked from Explorer rather than run from a terminal), it shows an interactive countdown after the script exits. This prevents the console window from vanishing before the user can read the output.

- Default delay: 7 seconds (`Sea.ExitDelay`)
- Scripts can change it: `Sea.ExitDelay = 0` to exit immediately, or `Sea.ExitDelay = 30` for more time
- The countdown is interactive: Enter skips, Escape cancels, arrow keys adjust, P pauses
- Non-ephemeral consoles (terminals, IDE, CI) never delay
- The Host library never delays — `Sea.IsConsoleEphemeral` is always `false`

Detection uses parent process identification via `NtQueryInformationProcess`: if the
parent is a known shell or terminal (cmd, powershell, bash, Windows Terminal, etc.)
the console is persistent; otherwise it's ephemeral (e.g., double-clicked from Explorer).

### Webapp Hot-Swap

`//sea_webapp` and `//sea_watch` combine for live-reloading web services. The key requirement: the old Kestrel instance must release the port before the new one starts. The CLI already waits for the old process to exit before spawning the new one, but Kestrel needs a graceful shutdown to release the port cleanly:

```csharp
//sea_webapp
//sea_watch

var app = WebApplication.CreateBuilder().Build();
app.MapGet("/", () => "Hello!");

Sea.Reloading += () => app.StopAsync().Wait(TimeSpan.FromSeconds(2));

app.Run("http://localhost:5199");
```

Edit, save — the new version is serving on the same port in seconds. No downtime, no port conflicts. Without the `StopAsync` call, the killed process may leave the port in TIME_WAIT and the new instance would fail to bind.

### Elevation Cascade

When a script has `//sea_elevate`, the CLI resolves it in order:
1. Already elevated? (e.g., `gsudo sea script.cs`) — spawn directly
2. Elevator worker connected to daemon? — delegate to it (no UAC prompt)
3. Elevator task registered but not running? — auto-start it, wait for it to connect (up to 15s), then delegate
4. None of the above — error with a helpful message suggesting `gsudo` or `sea --install-elevator`

On Linux, `//sea_elevate` is silently ignored.

### NuGet Resolution

Packages are resolved from the global NuGet cache (`~/.nuget/packages/`). Transitive dependencies are walked via .nuspec files. Runtime-specific DLLs (`runtimes/{rid}/lib/{tfm}/`) and native DLLs (`runtimes/{rid}/native/`) are included in the generated `.deps.json`, which the dotnet host reads for correct assembly probing. This is the key improvement over CS-Script, which loads the generic `lib/{tfm}/` stub and fails on packages like `Microsoft.Data.SqlClient`.

All configured NuGet sources from the user's `nuget.config` are checked. The background updater probes sources before iterating packages (circuit breaker) and distinguishes 404 (package not on this source) from 5xx (service error) to avoid false positives.

### Logging

The Engine uses Serilog with `Log.ForContext<T>()` per class. The daemon configures:
- **Windows**: EventLog sink (Info+) — visible in Event Viewer under "SeaShell"
- **Linux**: Syslog sink (Info+) — visible in `journalctl`
- **Dev mode**: Console sink (Debug+) — enabled with `--console` flag or `SEASHELL_CONSOLE_LOG=1`

The Host inherits Serilog from the Engine. The caller configures `Log.Logger` before creating `ScriptHost`. If unconfigured, all log output is silently dropped.

## Documentation

- [ARCHITECTURE.md](doc/ARCHITECTURE.md) — Component diagram, IPC layers, compilation pipeline, execution paths
- [SECURITY.md](doc/SECURITY.md) — Threat model, IPC access control, elevation security
- [HISTORY.md](doc/HISTORY.md) — Changelog

## License

[MIT](LICENSE)
