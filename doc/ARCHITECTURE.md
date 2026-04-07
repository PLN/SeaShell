# Architecture

## Overview

SeaShell has six runtime components that communicate over platform-native IPC,
plus a bootstrapper for installation:

```
┌──────────────────────────────────────────────────┐
│  Bootstrapper (seashell)                         │
│  Dotnet tool. Extracts per-RID archives,         │
│  registers daemon/elevator, manages PATH.        │
│  install / uninstall / start / stop / status     │
└──────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────┐
│  CLI (sea.exe / seaw.exe)                        │
│  Parses args, talks to daemon, spawns scripts,   │
│  manages script pipe, handles exit delay.        │
│  seaw.exe: WinExe subsystem (no console window). │
├──────────────────────────────────────────────────┤
│  Invoker (shared engine)                         │
│  Compilation, mutex, restart, attach, daemon     │
│  lifecycle. Used by CLI, Host, and ServiceHost.  │
└──────────┬───────────────────────────────────────┘
           │ Named pipes (Win) / Unix domain sockets (Linux)
┌──────────▼───────────────────────────────────────┐
│  Daemon (seashell-daemon)                        │
│  Persistent Roslyn host. Compiles scripts,       │
│  resolves NuGet, watches files, hosts REPL.      │
│  Stays warm — second compilation is instant.     │
└──────────┬───────────────────────────────────────┘
           │ Same transport, persistent connection
┌──────────▼───────────────────────────────────────┐
│  Elevator (seashell-elevator)     [Windows only] │
│  Pre-elevated worker. Connects TO daemon.        │
│  Spawns elevated script processes on demand.     │
└──────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────┐
│  Script process (dotnet exec)                    │
│  Runs the compiled script. Creates a named pipe  │
│  server; CLI connects as client.                 │
└──────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────┐
│  Host (SeaShell.Host)                            │
│  Embeddable library + service hosting.           │
│  Uses Invoker to compile and run scripts.        │
│  Connects to the script pipe like the CLI does.  │
└──────────────────────────────────────────────────┘
```

## Projects

| Project | Role |
|---|---|
| **SeaShell.Bootstrapper** | Dotnet tool (`seashell` command). Extracts per-RID archives, registers daemon/elevator tasks, manages PATH and file associations. |
| **SeaShell.Cli** | CLI entry point (sea.exe / seaw.exe). Argument parsing, REPL client, exit delay, file association. Thin wrapper around Invoker. |
| **SeaShell.Invoker** | Shared execution engine. `ScriptInvoker` (compile, execute, watch, reload, restart, mutex, attach, elevate), `DaemonLauncher`, `DaemonManager`, `ScheduledTasks`, `DirectiveScanner`, `ScriptMutex`. Used by CLI, Host, and ServiceHost. |
| **SeaShell.Daemon** | Compilation server. Listens for `RunRequest`, compiles via Engine, returns artifact paths. Holds Elevator connection. Runs `ScriptWatcher` for hot-swap. |
| **SeaShell.Elevator** | Elevated worker (Windows). Connects to daemon, receives `SpawnRequest`, starts elevated processes. Registered via Task Scheduler. |
| **SeaShell.Engine** | Roslyn compiler, NuGet resolver, include system, `.deps.json` writer, compilation cache, script watcher, background updater. |
| **SeaShell.Script** | Runtime context (`Sea` static class). Loaded into every script process. Manages the script-side pipe server, lifecycle events, reload state, and attach server. |
| **SeaShell.Common** | Shared message types, `MessageChannel` (System.IO.Pipelines). Used by Script (in the script process) and Invoker (in the launcher). |
| **SeaShell.Protocol** | Daemon/Elevator protocol messages and `TransportStream` (platform IPC abstraction). |
| **SeaShell.Host** | Embeddable API + cross-platform service hosting. Wraps Invoker for applications that want to run scripts without the CLI. Auto-detects init system (Windows Service, systemd, runit, OpenRC). Management commands, crash recovery, zero-locking NuGet updates. |

## IPC Layers

There are two distinct IPC layers:

Both layers use the same binary wire format:

```
[4-byte LE length][1-byte MessageType tag][MessagePack payload]
```

`MessageType` is a byte enum. MessagePack (contractless resolver) serializes plain C# records
without attributes. `byte[]` fields are native binary — no Base64 encoding.

### 1. Daemon protocol

CLI ↔ Daemon and Daemon ↔ Elevator communication. `TransportStream` wraps platform IPC
(named pipes on Windows, Unix domain sockets on Linux) and exposes a `MessageChannel`.
Defined in `SeaShell.Protocol`.

Messages: `RunRequest`/`RunResponse`, `PingRequest`/`PingResponse`, `HotSwapNotify`,
`SpawnRequest`/`SpawnResponse`, `ElevatorHello`/`ElevatorAck`, `ReplStartRequest`/`ReplEvalRequest`.

### 2. Script pipe

Launcher ↔ Script communication. Bidirectional named pipe with the script as server
and the CLI (or Host) as client. Uses `MessageChannel` from `SeaShell.Common`.

Messages: `ScriptInit`, `ScriptReload`, `ScriptStop`, `ScriptExit`, `ScriptState`,
`HostMessage`, `ScriptMessage`, `ScriptReloadRequest`.

The script creates the pipe server with a GUID-based name passed via the `SEASHELL_PIPE`
environment variable. The launcher connects as client after spawning the process.

`HostMessage`/`ScriptMessage` enable bidirectional application messaging between
Host and Script during execution. Binary `byte[]` payload with optional topic routing.

### 3. Attach pipe

Blocked-caller ↔ Running-instance communication for `//sea_mutex_attach`. The running
script starts an `AttachServer` on a well-known pipe name (`seashell-attach-{identity}`).
Blocked callers connect as clients. Same binary wire format.

Messages: `AttachHello` (args + CWD), `AttachMessage` (bidirectional binary payload),
`AttachClose` (exit code). Message types 50-52.

The identity is an FNV-1a hash of the normalized script path, computed by
`DirectiveScanner.ComputeIdentity()`. This is deterministic — any caller of the same
script gets the same pipe name.

## Compilation Pipeline

```
Script source
  │
  ├─ IncludeResolver: walk //sea_inc directives, collect all sources
  │
  ├─ CompilationCache: SHA256(sources + engine fingerprint + direct NuGet versions)
  │   └─ Cache hit? → return existing artifacts (self-contained output dir)
  │       No NuGet resolution, no disk I/O beyond source files + version dir listing.
  │
  │  ── Cache miss: full compilation ──
  │
  ├─ NuGetResolver: walk //sea_nuget directives, resolve transitive deps
  ├─ SourceSplitter: separate top-level statements from type declarations
  ├─ Inject _SeaShellMeta (global usings + _SeaShellBoot module initializer)
  ├─ Roslyn CSharpCompilation / VBCompilation → emit to MemoryStream
  │
  └─ Output dir (self-contained, like dotnet publish):
      ├─ {name}.dll                    compiled assembly
      ├─ {name}.runtimeconfig.json     framework refs (no probing paths)
      ├─ {name}.deps.json             all entries type:"project" (app base dir)
      ├─ {name}.sea.json              SeaShell metadata
      ├─ SeaShell.Script.dll          ┐
      ├─ SeaShell.Common.dll           │ bundled (from engine dir)
      ├─ MessagePack.dll              │
      ├─ MessagePack.Annotations.dll  ┘
      ├─ Serilog.dll                  ┐
      ├─ OtherPackage.dll             │ NuGet (copied from cache)
      └─ runtimes/{rid}/native/       ┘ native DLLs (structure preserved)
```

The compiled script runs via `dotnet exec --runtimeconfig ... --depsfile ... assembly.dll`.

### Self-contained output

The output directory is completely standalone — no runtime dependency on the NuGet cache or
engine directory. All DLLs (bundled SeaShell runtime + NuGet packages) are physically copied
to the output dir at compile time. The `.deps.json` uses type:"project" entries exclusively,
which resolve from the app base directory. No `additionalProbingPaths` in the runtimeconfig.

This means:
- **Cache-clearing safe** — `dotnet nuget locals all --clear` doesn't break cached scripts
- **User-identity independent** — service accounts don't need NuGet cache access at runtime
- **No file locks** — no probing into `~/.nuget/packages/` during execution
- **Portable** — the output dir could be copied to another machine and still work

### Cache hash

The cache key is `SHA256(engine fingerprint + source paths + source content)`. NuGet package
versions are NOT in the hash — packages are immutable by convention, and the output dir is
self-contained. NuGet resolution is skipped entirely on cache hits.

The engine fingerprint is the sum of file timestamps for the Engine, Script, Ipc, and
MessagePack DLLs. Any rebuild of the engine invalidates all cached scripts.

## NuGet Resolution

Packages are resolved from the global cache (`~/.nuget/packages/`). Transitive dependencies
are walked via `.nuspec` files. For each package:

1. Find the best TFM match (net10.0 → net9.0 → ... → netstandard2.0)
2. Collect compile-time DLLs (generic `lib/{tfm}/`) for Roslyn references
3. Collect runtime DLLs (platform-specific `runtimes/{rid}/lib/{tfm}/`) for `.deps.json`
4. Collect native DLLs (`runtimes/{rid}/native/`) for `.deps.json`
5. RID fallback chain: e.g. `win-x64` → `win` → `any`

NuGet resolution only runs on cache miss (first compilation or source change). On cache hit,
the self-contained output dir has everything needed — NuGet is not consulted.

Missing packages are downloaded automatically via `dotnet restore` on a temporary project.
The background updater checks all cached packages every 8 hours against all configured
NuGet sources (including private feeds).

## Execution Paths

### Direct (non-elevated, non-watch)

```
CLI → Daemon.RunRequest → compile → RunResponse(artifacts)
CLI → Process.Start(dotnet exec ..., SEASHELL_PIPE=seashell-{guid})
Script → Sea.Initialize → create pipe server → accept CLI connection
CLI → connect → send ScriptInit
Script → run Main → ProcessExit → send ScriptExit
CLI → read ScriptExit → exit code → optional exit delay
```

### Elevated (Windows)

```
CLI → Daemon.RunRequest → compile → RunResponse(elevated=true)
CLI → Daemon.SpawnRequest → if no elevator:
  CLI → start elevator task (if registered)
  CLI → Daemon.SpawnRequest(AwaitElevatorMs=15000)
  Daemon → wait for ElevatorHello (up to 15s)
Daemon → Elevator.HandleSpawn → Process.Start(elevated)
Elevated script → Sea.Initialize → FreeConsole + AttachConsole(CLI pid)
Elevated script → create pipe server → accept CLI connection
CLI → connect → send ScriptInit
Elevated script → run Main → ProcessExit → send ScriptExit
CLI → read ScriptExit → exit code
```

### Watch (hot-swap)

```
CLI → Daemon.RunRequest → compile → RunResponse(watch=true)
loop:
  CLI → spawn script → connect → send ScriptInit(reloadCount, state)
  Daemon → ScriptWatcher detects change → recompile → HotSwapNotify
  CLI → send ScriptReload to script
  Script → fire Reloading event → send ScriptState → exit
  CLI → read ScriptState + ScriptExit → increment reloadCount → loop
```

### Restart

```
CLI → Daemon.RunRequest → compile → RunResponse(restart=true)
loop:
  CLI → spawn script → connect → send ScriptInit(restartCount)
  Script → run Main → ProcessExit → send ScriptExit(restart=true)
  CLI → read ScriptExit → if restart && Sea.Restart:
    if exit was fast (<5s): exponential backoff (2s, 4s, 8s, 8s...)
    increment restartCount → loop
```

### Mutex (pre-compilation)

```
CLI → DirectiveScanner.Scan(script) → detect //sea_mutex [scope]
CLI → ScriptMutex.TryAcquire(identity, scope)
  Acquired? → proceed to Daemon.RunRequest (normal flow)
  Blocked + //sea_mutex_attach? → attach to running instance
  Blocked, no attach? → exit code 200
```

### Attach (blocked caller)

```
Blocked CLI → connect to seashell-attach-{identity} pipe
Blocked CLI → send AttachHello(args, cwd)
Running script → Sea.Attached event fires with AttachContext
  Script calls ctx.Send/ctx.Receive for bidirectional messaging
  Script calls ctx.Close(exitCode)
Blocked CLI → receives AttachClose → exits with that code
```

### seaw.exe (Window Mode)

```
seaw.exe → IsSeawExe() = true → no console allocated
  If script path: DirectiveScanner.Scan() → //sea_console?
    Yes: AllocConsole() or AttachConsole(parent)
    No:  stay windowless
  If no args: AllocConsole() (dashboard needs stdout)
  If error + no console: write to Windows Event Log
seaw.exe → normal Invoker flow (same as sea.exe)
```

## Elevated Console (Windows)

Elevated scripts run in a separate process spawned by the Elevator. To share the CLI's
console (so `Console.WriteLine`, `Console.Title`, and colored output work in the user's
terminal), the script calls:

1. `FreeConsole()` — detach from the hidden console
2. `AttachConsole(cliPid)` — attach to the CLI's console

The CLI's PID is passed via `SEASHELL_CLI_PID`. The Elevator spawns with
`WindowStyle.Hidden` to avoid a visible console flash.

## Script Mutex

`DirectiveScanner` reads the first ~20 lines of a script to detect mutex directives
before contacting the daemon. `ScriptMutex.TryAcquire()` is called pre-compilation.

| Scope | Windows | Linux |
|---|---|---|
| System | `Global\SeaShell_{identity}` (named kernel mutex) | flock in `/tmp/` |
| User | `SeaShell_{identity}_u{username}` (named kernel mutex) | flock in `~/.local/share/seashell/` |
| Session | `Local\SeaShell_{identity}_s{sessionId}` (named kernel mutex) | flock in `$XDG_RUNTIME_DIR/` |

The identity is an FNV-1a hash of the normalized script path, so the same script always
gets the same mutex name regardless of how it's invoked (relative vs absolute path).

When `//sea_mutex_attach` is active, the running instance starts an `AttachServer` on
pipe `seashell-attach-{identity}`. Blocked callers connect instead of exiting. The
`Sea.Attached` event fires for each client with an `AttachContext` that provides the
caller's args, working directory, and bidirectional `Send`/`Receive`/`Close` methods.

## Daemon Hash & Version Check

The daemon computes a self-hash using SHA256 of all assembly `FullName` strings (ordered)
in its base directory. `DaemonManager.ComputeDirHash()` uses the same algorithm on the
staged binary directory. On script run, the CLI pings the daemon and compares hashes —
a mismatch triggers automatic restart.

`PingResponse` carries: `Version` (4-part), `Pid`, `DaemonHash`, `UptimeSeconds`,
`ActiveScripts`, `IdleSeconds`, `IdleTimeoutSeconds`, `ElevatorVersion`.

## Handle Inheritance

When spawning the daemon, `DaemonManager` prevents handle inheritance through two layers:

1. **Redirect + close** (cross-platform) — stdin/stdout/stderr are redirected and
   immediately closed after `Process.Start()`.
2. **StdHandleInheritGuard** (Windows) — temporarily clears `HANDLE_FLAG_INHERIT`
   on the standard handles before spawning. Prevents the daemon from inheriting any
   inheritable handles that would block SSH sessions, pipe readers, or test harnesses.

## Cross-Platform

| Aspect | Windows | Linux |
|---|---|---|
| Daemon transport | Named pipes | Unix domain sockets |
| Script pipe | Named pipes (async) | Named pipes (async) |
| Attach pipe | Named pipes | Named pipes |
| Pipe permissions | ACL (current user SID) | File mode 0600 |
| Mutex | Named kernel mutex (Global/Local) | flock |
| Elevation | Elevator via Task Scheduler | Ignored (`//sea_elevate` is a no-op) |
| Window mode | seaw.exe (WinExe subsystem) | N/A |
| Console management | FreeConsole/AttachConsole | N/A |
| Exit delay | Parent process detection | N/A |
| Logging | EventLog sink | Syslog sink |
| Scheduled tasks | Task Scheduler XML | N/A (use systemd) |
