# {~} SeaShell

A C# and VB.NET scripting engine with a persistent daemon, NuGet support, hot-swap, REPL, and an embeddable host library. Built on Roslyn.

## Install

Requires the .NET 10 SDK.

```
dnx SeaShell install --all
```

This fetches the latest SeaShell package and installs everything the platform
supports, then remembers that scope:

- **Windows**, behind a single UAC prompt: the per-user install, the elevator
  task (for UAC-free `//sea_elevate` scripts), the Windows Event Log source,
  and the SYSTEM install (so Windows services running as LocalSystem can use
  SeaShell). From a non-elevated console it self-elevates and streams the
  elevated output back, then starts the daemon and elevator and verifies the
  whole chain.
- **Linux**: the per-user install, then root's own install (for services
  running as root) through `sudo`.

`dnx SeaShell install` (without a scope flag) installs the remembered scope
(see [Scope](#scope)). On a first install there is none, so it is the user-only
install: no elevation, daemon task and file associations registered, elevator
skipped unless the console is already elevated. Later, a plain `install` or
`update` covers whatever was installed before — including the elevator and
SYSTEM after an `install --all`, with one UAC prompt — and `--user` narrows the
scope back to the user install.

The install ships its own `seashell`, next to `sea` in `current/bin` (on the
Windows user PATH, and symlinked into `~/.local/bin` on Linux). Later updates
are one command:

```
seashell update                   # latest version, same scope as before
seashell                          # dashboard: versions, scope, daemon/elevator state
```

`seashell update` fetches the latest package into a temporary tool directory
(`dotnet tool install SeaShell --tool-path <temp>`, so it needs NuGet access at
that moment), runs that bootstrapper's install for every target that is not
already on that version, and removes the directory again. It prints
`Up to date` when there was nothing to do. `--version <v>` picks a version,
`--source <feed>` replaces the configured NuGet sources.

### Scope

The scope — which parts an install consists of: `user`, `elevator` (Windows),
`system` (the Windows SYSTEM install or the Linux root install) — is recorded
in `{DataDir}/install.scope` after each install. `seashell install` and
`seashell update` without flags reuse it, so a plain update after
`install --all` updates the elevator and SYSTEM too (one UAC prompt or `sudo`
password, only when the install has such parts). The flags override and
re-record it:

| Flag | Scope |
|------|-------|
| `--all` | everything the platform supports |
| `--user` | user only; prints what it leaves behind and how to remove that |
| `--system` | the SYSTEM / root install only (run elevated: `gsudo` / `sudo`); the user's recorded scope is not changed |

Installs made by 0.6.2 or earlier have no recorded scope; it is detected from
what is installed (the elevator task, a SYSTEM daemon task or running SYSTEM
daemon, `/var/lib/seashell/current`). Where a non-elevated console cannot see
whether a SYSTEM install exists, the run updates the user install only, as
0.6.2 did, says so, and records nothing; `seashell install --all` records the
full scope.

When the UAC prompt is answered with a different (administrator) account, the
elevated part installs SYSTEM only and says so; the original console then
installs the user part itself, without the elevator. A standard user cannot
have an elevator: it runs with the user's own elevated token.

The SYSTEM install lives under SYSTEM's own user profile
(`%SystemRoot%\System32\config\systemprofile\AppData\Local\seashell\`) with a
boot-triggered daemon task (`S-1-5-18` principal), started right away. The
elevated bootstrapper performs it directly — identity-derived values (data dir,
pipe names, task principals) are computed from SIDs, so nothing needs to
execute as SYSTEM. The Linux root install lives in root's own data directory
(`/var/lib/seashell/`); like every SeaShell install it belongs to one identity
and is not shared with other users.

### Alternative: global tool

```
dotnet tool install -g SeaShell
seashell install --all
```

A global tool carries its own version: `seashell install` installs exactly that
version (also to repair or reinstall). `seashell update` from it fetches the
latest, like the installed copy does. Once a newer version is installed, an
older global tool is removed (`dotnet tool uninstall -g SeaShell`) because the
installed `seashell` supersedes it; on Windows `current\bin` is kept ahead of
`%USERPROFILE%\.dotnet\tools` in the user PATH. When the global tool is in use
by the running command, the one-line fix is printed instead.

Which `seashell` runs: after the first install, `seashell` on PATH is the
installed copy (`current/bin`), not the global tool. The installed copy carries
no package of its own, so its `install` normally fetches the latest version
from the configured NuGet sources — like `update`, it prints which sources it
searches and which version it resolved, and every configured feed counts (a
private feed with newer CI builds wins over nuget.org; pass
`--source https://api.nuget.org/v3/index.json` to use nuget.org only). The
classic route still means what it did: when a global SeaShell tool newer than
the installed version is present (`dotnet tool update -g SeaShell`), the
installed copy's `install` runs that global tool instead of fetching, so
`dotnet tool update -g SeaShell && seashell install --all` installs exactly
the global tool's version.

### Automatic updates

Off by default. `seashell update --auto on` schedules a daily `seashell update`
for the remembered scope, one entry per target, each updating only itself:

- **Windows**: task `SeaShell Update ({user})` in `\SeaShell\` (runs while you are
  logged on; with highest privileges when the scope has the elevator, so the
  elevator is re-registered without a prompt), and `SeaShell Update (SYSTEM)`
  when the scope has the SYSTEM install. Enabling either needs the one UAC
  prompt.
- **Linux**: one line in your crontab tagged `# seashell-auto-update`, and one
  in root's crontab (through `sudo`) for the root install.

Each entry runs the installed `current/bin/seashell` (so it survives version
changes) with an absolute `dotnet` path checked when you enable it, at a time of
day derived from the machine and account. Windows Task Scheduler catches up a
run missed while the machine was off or you were logged off; plain Linux cron
does not (the next day's run updates). A `--source <feed>` given with
`--auto on` is stored in the entry (a local path as an absolute path); otherwise
each identity uses its own NuGet configuration (SYSTEM and root: usually
nuget.org only).

The SYSTEM / root entry runs as SYSTEM / root, so it records a `dotnet` that
only administrators can change: one under `%ProgramFiles%`, or on Linux one
owned by root all the way up (`/usr/bin/dotnet`, `/usr/share/dotnet`,
`/usr/lib/dotnet`, ...) — never an SDK in your profile (`~/.dotnet`). With only
a per-user SDK, `--auto on` turns on your own entry, says why the SYSTEM / root
one was not created, and exits with code 3; install the .NET SDK machine-wide
and run it again.

An update is skipped while the target's daemon has connected sessions (watch
sessions, REPLs, compiles that an update would cut off); plain running scripts
keep running from their own directory, as with a manual update. A scheduled run
prints nothing unless it installed something or failed. Every run appends its
complete outcome — time, command line, exit code and all output — to
`update.log` in the data directory (`%LOCALAPPDATA%\seashell`,
`~/.local/share/seashell`; rolled over to `update.log.1` at 1 MB), and also
writes it to the Windows Event Log (source `SeaShell`, registered by an elevated
install) or syslog. `seashell status` shows whether automatic updates are on and
points to `update.log` when the last scheduled run failed;
`seashell update --auto off` removes the entries, and so does
`seashell uninstall`. See [doc/SECURITY.md](doc/SECURITY.md#automatic-updates).

### Uninstall

```
seashell uninstall
```

removes the remembered scope — with the same one UAC prompt (Windows) or
`sudo` (Linux) as install when it has elevated parts: binaries and version
directories, the daemon, elevator and automatic-update tasks or crontab line,
the PATH entry / `~/.local/bin` symlinks, file associations, `install.scope`
and `update.check`. `--user` removes the user install (and its elevator) and
leaves SYSTEM / root; `--system` removes only that; `--all` everything.

Scripts scheduled with `seashell schedule` are listed and **kept** — they work
again after a reinstall; `seashell uninstall --purge` removes them as well,
listing each. The NuGet global packages cache and the Windows Event Log source
`SeaShell` stay. Run from the installed `seashell` on Windows, the uninstall
continues from a temporary copy and its own version directory is removed after
it exits — unless a reinstall of that version has made it current again by
then. A version directory that a running script or `seaw` window still
executes from is left untouched and named; the uninstall then reports
"incomplete" with a non-zero exit code instead of "Uninstalled.". The
`dotnet tool uninstall -g SeaShell` hint appears only when a global tool is
installed.

### What stays manual

- Installing the .NET 10 SDK (the installer checks for it before changing
  anything).
- One migration run for installs made by 0.6.2 or earlier, whose `seashell
  update` cannot fetch a newer version: `dnx SeaShell install` (it detects and
  records the existing scope), or the old two commands once more
  (`dotnet tool update -g SeaShell`, `seashell install --all`).
- Opening a new terminal after a PATH change. On Linux, `~/.local/bin` must be
  on PATH; the installer prints the `export PATH=...` line when it is not.
- Opting in to automatic updates (`seashell update --auto on`).
- NuGet source configuration for SYSTEM / root when you use a private feed:
  they use their own NuGet configuration.
- Rebuilding services to opt in to the builder option for package-update
  reloads (see [Service Hosting](#service-hosting)).

Use `seashell start` / `seashell stop` to manage the daemon lifecycle.

## Quick Start

```
sea                             Interactive REPL
sea script.cs                   Run a C# script
sea script.vb                   Run a VB.NET script
sea myapp.dll                   Run a pre-compiled .NET assembly
sea myapp.exe                   Run a single-file .exe
sea -i Humanizer.Core           REPL with NuGet packages preloaded
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
- **Elevation** — `//sea_elevate` scripts run elevated via a pre-registered Task Scheduler worker. No UAC prompts. Requires a running or registered elevator unless the CLI is already elevated. Ignored on Linux.
- **Binary running** — Run pre-compiled `.dll` and `.exe` files with full Sea context. Single-file executables, apphosts, and plain DLLs all supported. Companion `.sea.json` enables directives for binaries.
- **Script-initiated reload** — `Sea.RequestReload()` triggers recompilation and hot-swap from within a script. Optional `clearCache` for forced fresh builds. Works in both direct and watch mode.
- **Service hosting** — `SeaShell.Host` includes service hosting (namespace `SeaShell.ServiceHost`). Runs scripts as system services. Windows Service, systemd, runit, OpenRC, sysvinit — auto-detected. `install` registers and starts the service; optionally the script is reloaded when a package update changes its build.
- **Embeddable** — `SeaShell.Host` lets you compile and run scripts from your own application, through the installed SeaShell daemon (started on demand). Supports reload and watch mode internally.
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
| `//sea_elevate` | Run elevated (via Elevator; ignored on Linux) |
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

On Windows, system and user scopes use `Global\` (user scope includes the SID);
session scope uses `Local\`. On Linux, file locks in scope-appropriate directories.

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

The `.csw` extension is a convention for window-mode scripts. `seashell install`
associates it with seaw.exe.

## Embedding (SeaShell.Host)

```csharp
using SeaShell.Host;

var host = new ScriptHost();

// Run a script file
var result = await host.RunAsync("path/to/script.cs");
Console.WriteLine(result.StandardOutput);

// Run a code snippet
var snippet = await host.RunSnippetAsync("""
    Console.WriteLine("Hello!");
    return 0;
    """);

// Compile once, run many times (null if compilation failed)
var compiled = await host.CompileAsync("script.cs");
if (compiled != null)
{
    await host.ExecuteAsync(compiled);
    await host.ExecuteAsync(compiled);
}
```

`ScriptHost` compiles through the SeaShell daemon, so SeaShell must be installed on the machine. The daemon is started on demand; a running daemon of a newer version is used as well. Package updates need nothing from the host: the daemon keeps its NuGet cache up to date (see **Background NuGet updates** above), and a script that references a package without a version compiles against the newest cached version at the time of the compile.

`Sea.IsConsoleEphemeral` is always `false` when running via Host. `Sea.ExitDelay` has no effect — the Host does not perform exit delays.

### Host↔Script Messaging

The Host and script can exchange binary messages during execution over the existing pipe:

```csharp
using System.Text;
using SeaShell.Host;
using SeaShell.Invoker;   // ScriptConnection

var host = new ScriptHost();
var conn = new ScriptConnection();
conn.MessageReceived += (payload, topic) =>
    Console.WriteLine($"Script [{topic}]: {Encoding.UTF8.GetString(payload)}");

var result = await host.RunAsync("script.cs", connection: conn);
```

While the script runs, `conn.SendAsync(payload, topic)` sends to it.

Script side:
```csharp
Sea.MessageReceived += (payload, topic) =>
    Sea.SendMessage("acknowledged", "reply");

Sea.SendMessage("{\"status\":\"ready\"}", "status");
```

Messages are binary (`byte[]`) with an optional `string` topic for routing. String convenience overloads encode as UTF-8.

## Service Hosting

Run any script or binary as a system service:

```csharp
using SeaShell.ServiceHost;

return await new ServiceHostBuilder()
    .ServiceName("myservice")
    .DisplayName("My Service")
    .Description("Runs my-script.cs as a system service")
    .RunScript("my-script.cs")           // or .RunAssembly("myapp.dll")
    .ReloadOnPackageUpdate()             // optional, RunScript only (see below)
    .RunAsync(args);
```

Relative script and assembly paths resolve against the executable's directory, which is also the default working directory (`.WorkingDirectory(...)` changes it; `.EnvironmentVariable(...)` sets variables for the script). When the workload exits it is started again, after five seconds if it failed.

The binary handles both service mode and management commands:

```
myservice                   Run as foreground service (invoked by init system)
myservice install           Auto-detect platform, register the service and start it
myservice uninstall         Stop the service and remove its registration
myservice start             Start the registered service (already running is a success)
myservice stop              Stop the registered service
myservice status            Show service status
```

Supported init systems (auto-detected): Windows Service, systemd, runit, OpenRC, sysvinit. `install`, `uninstall`, `start` and `stop` need administrator rights (root on Linux). If the service does not start, `install` leaves it registered and exits with the start's error code; fix the cause and run `myservice start` (running `install` again fails on Windows, where the service is already registered).

**Package updates**: the daemon downloads newer package versions every 8 hours; a running service keeps the build it started with. With `.ReloadOnPackageUpdate(interval)` (default: every hour) the service asks the daemon to compile the script again — a cache hit while nothing has changed. When a newer version of a package referenced without a version (`//sea_nuget Name`) or an edited source file gives a new compilation cache directory, the running instance is reloaded as by `Sea.RequestReload()`: it gets `Sea.Reloading` (and `Sea.ShutdownToken` is cancelled), can pass state on with `Sea.SetReloadState(...)`, and the new build starts from its own directory, so no files are contested. A compile error leaves the running instance alone and is logged as a warning. There is no rollback once a reload has begun: the old instance stops before the new build is compiled again and started, so if that fails, no instance runs until the service's restart loop (every five seconds) gets one going, and that one starts without the saved state. Pinned versions (`//sea_nuget Name 1.2.3`) never change. A `//sea_elevate` script that a non-elevated service runs through the elevator is not reloaded.

## Task Scheduler (Windows)

`seashell install` registers scheduled tasks automatically:

- `SeaShell Daemon ({user}) {version}` — LogonTrigger, LeastPrivilege. Auto-starts the daemon at user logon.
- `SeaShell Elevator ({user}) {version}` — LogonTrigger, HighestAvailable. Only registered when the scope includes the elevator (`--all`), or when `seashell install` runs elevated.
- `SeaShell Daemon (SYSTEM) {version}` — BootTrigger, HighestAvailable. Only registered when the scope includes SYSTEM (`--all`, `--system`).
- `SeaShell Update ({user})` / `SeaShell Update (SYSTEM)` — daily, only with automatic updates on (`seashell update --auto on`, see [Automatic updates](#automatic-updates)). Install and stop never end these; uninstall removes them.

Use `sea --start` / `sea --stop` / `sea --status` (or `seashell start`/`stop`/`status`) for lifecycle and state. `sea --stop` uses IPC first (works on Linux too), then ends Task Scheduler tasks. The daemon also starts lazily on first `sea` invocation if no task is registered.

The elevator is optional — without it, `//sea_elevate` scripts require an already
elevated CLI. When its task is registered but not running, `sea` auto-starts it on demand.

## Bootstrapper Commands

`seashell` — the copy shipped in `current/bin`, a global tool, or
`dnx SeaShell` — manages the SeaShell installation:

```
seashell                          Dashboard: version, platform, scope, daemon/elevator state
seashell install                  Install the version this bootstrapper carries, remembered scope
                                  (the installed copy carries none: same as update)
seashell install --all            Everything: user + elevator + SYSTEM (Windows, one UAC prompt)
                                  or user + root (Linux, through sudo); remembered
seashell install --user           User only; remembered
seashell install --system         SYSTEM / root only (requires elevation); user scope unchanged
seashell update                   Fetch the latest version and install it, remembered scope
seashell update --version <v>     Fetch a specific version
seashell update --source <feed>   Fetch from this NuGet source instead of the configured ones
seashell update --auto on|off     Daily unattended update of the remembered scope [--source <feed>]
seashell uninstall                Remove the remembered scope (see Uninstall); lists scheduled scripts
seashell uninstall --purge        ... and removes the scripts scheduled with seashell schedule
seashell uninstall --user         User install (and its elevator) only; SYSTEM / root stays
seashell uninstall --system       SYSTEM / root install only (requires elevation)
seashell uninstall --all          Everything the platform supports
seashell start                    Start daemon (and elevator if registered)
seashell stop                     Stop daemon and elevator
seashell status                   Show running state and versions
seashell status --system          Show SYSTEM installation status
seashell schedule <script> <timing>  Schedule a script via Task Scheduler
seashell schedule                 List scheduled scripts
seashell unschedule <script>      Remove a scheduled script
```

`seashell install` reinstalls (repairs) the version the bootstrapper carries;
`seashell update` always looks for the latest one and installs it only where it
is missing. `update` takes the same scope flags as `install`.

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

`seashell install` registers `.cs` and `.csw` per-user (HKCU), pointing at
the per-version `sea.exe` / `seaw.exe`. Associations are re-registered on
install or update. Double-click a `.cs` file in Explorer to run
it with SeaShell; `.csw` files are run by `seaw.exe` in Windows subsystem
mode (no console flash).

Associations are removed by `seashell uninstall`. No separate registration
commands — they're part of the install flow.

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
The `SeaShell` tool package embeds the per-RID install archives only with
`-p:EmbedArchives=true`, after `src/SeaShell.Bootstrapper/archives/{rid}.zip`
has been staged (see the package job in `.gitea/workflows/ci.yml`). Without
them the tool carries no version of its own, and `seashell install` fetches the
latest published package, like `seashell update`.

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
| Windows SYSTEM | `%SystemRoot%\System32\config\systemprofile\AppData\Local\seashell\` |
| Linux user | `$XDG_DATA_HOME/seashell/` (or `~/.local/share/seashell/`) |
| Linux root | `/var/lib/seashell/` |

Override with `SEASHELL_DATA` environment variable. Layout under each:
`v{version}/bin/` (per-version binaries), `v{version}/cache/` (compiled
scripts), `v{version}/daemon.path`/`daemon.pid`/`daemon.pipe` (markers),
`current/` (junction to the active version), `snippets/` (shared REPL temp
files).

Side-by-side versioning: each installed version has its own directory; the
`current` junction points at whichever version `PATH` should resolve to.
`seashell install` of a newer version updates the junction and cleans up
older ones only after the running daemon confirms no active clients.

### Compilation Cache

Compiled scripts are cached in `{DataDir}/v{version}/cache/{script}_{hash8}/`. The cache key is a SHA256 hash of:
- All source files (main script + includes, by path and content)
- Resolved direct NuGet package versions (name@version; transitives excluded)
- The engine fingerprint (Engine, Script, Common, and MessagePack assembly timestamps)

A cache hit skips Roslyn entirely — the daemon returns the existing artifacts. The cache invalidates automatically when:
- Any source file changes
- A direct NuGet package resolves to a different version
- An assembly in the engine fingerprint is rebuilt

Unused entries are cleaned up automatically. The daemon tracks when each entry was
created and last hit (`cache/.tracker.json`) and sweeps hourly:
- An entry not hit for **24 hours** is deleted — one-off scripts and edited versions don't pile up.
- An entry recreated within **30 days** of its last hit (a weekly job, say) is promoted
  and then kept until it goes **30 days** without a hit.
- A running script counts as a hit: its `sea`/host process pings the daemon every
  10 minutes, and the sweep never deletes an entry whose files a running process has
  loaded — long-running services and watch sessions keep their files.

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

Edit, save — the CLI waits for the old process to exit before starting the replacement.
The `StopAsync` call gives Kestrel a chance to shut down cleanly during reload.

### Elevation Cascade

When a script has `//sea_elevate`, the CLI resolves it in order:
1. Already elevated? (e.g., `gsudo sea script.cs`) — spawn directly
2. Elevator worker connected to daemon? — delegate to it (no UAC prompt)
3. Elevator task registered but not running? — auto-start it, wait for it to connect (up to 15s), then delegate
4. None of the above — error (`Elevation failed: ...`)

On Linux, `//sea_elevate` is silently ignored.

### NuGet Resolution

Packages are resolved from the global NuGet cache (`~/.nuget/packages/`). Transitive dependencies are walked via .nuspec files. Runtime-specific DLLs (`runtimes/{rid}/lib/{tfm}/`) and native DLLs (`runtimes/{rid}/native/`) are copied flat into the script output directory and declared by bare filename in the generated `.deps.json` (all libraries are type:"project"), which the dotnet host reads for correct assembly probing. This is the key improvement over CS-Script, which loads the generic `lib/{tfm}/` stub and fails on packages like `Microsoft.Data.SqlClient`.

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
