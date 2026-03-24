# Architecture

## Overview

SeaShell has five components that communicate over platform-native IPC:

```
┌──────────────────────────────────────────────────┐
│  CLI (sea.exe)                                   │
│  Parses args, talks to daemon, spawns scripts,   │
│  manages script pipe, handles exit delay.        │
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
│  Embeddable library. Compiles and runs scripts   │
│  without the daemon. Connects to the script pipe │
│  the same way the CLI does.                      │
└──────────────────────────────────────────────────┘
```

## Projects

| Project | Role |
|---|---|
| **SeaShell.Cli** | CLI entry point. Argument parsing, daemon lifecycle, script execution (direct, elevated, watch), REPL client, exit delay, file association. |
| **SeaShell.Daemon** | Compilation server. Listens for `RunRequest`, compiles via Engine, returns artifact paths. Holds Elevator connection. Runs `ScriptWatcher` for hot-swap. |
| **SeaShell.Elevator** | Elevated worker (Windows). Connects to daemon, receives `SpawnRequest`, starts elevated processes. Registered via Task Scheduler. |
| **SeaShell.Engine** | Roslyn compiler, NuGet resolver, include system, `.deps.json` writer, compilation cache, script watcher, background updater. |
| **SeaShell.Script** | Runtime context (`Sea` static class). Loaded into every script process. Manages the script-side pipe server, lifecycle events, and reload state. |
| **SeaShell.Ipc** | Shared message types and `MessageChannel` (System.IO.Pipelines). Used by both Script (in the script process) and Cli/Host (in the launcher). |
| **SeaShell.Protocol** | Daemon/Elevator protocol messages and `TransportStream` (platform IPC abstraction). |
| **SeaShell.Host** | Embeddable API. Wraps Engine + Script for applications that want to run scripts without the daemon. |
| **SeaShell.ServiceHost** | Cross-platform service hosting. Auto-detects init system (Windows Service, systemd, runit, OpenRC). Management commands, crash recovery, zero-locking NuGet updates. |

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
and the CLI (or Host) as client. Uses `MessageChannel` from `SeaShell.Ipc`.

Messages: `ScriptInit`, `ScriptReload`, `ScriptStop`, `ScriptExit`, `ScriptState`,
`HostMessage`, `ScriptMessage`.

The script creates the pipe server with a GUID-based name passed via the `SEASHELL_PIPE`
environment variable. The launcher connects as client after spawning the process.

`HostMessage`/`ScriptMessage` enable bidirectional application messaging between
Host and Script during execution. Binary `byte[]` payload with optional topic routing.

## Compilation Pipeline

```
Script source
  │
  ├─ IncludeResolver: walk //sea_inc directives, collect all sources
  ├─ NuGetResolver: walk //sea_nuget directives, resolve transitive deps
  │
  ├─ CompilationCache: SHA256(sources + packages + engine fingerprint)
  │   └─ Cache hit? → return existing artifacts
  │
  ├─ SourceSplitter: separate top-level statements from type declarations
  ├─ Inject _SeaShellMeta (global usings + _SeaShellBoot module initializer)
  ├─ Roslyn CSharpCompilation / VBCompilation → emit to MemoryStream
  │
  └─ ArtifactWriter:
      ├─ {name}.dll            compiled assembly
      ├─ {name}.runtimeconfig.json   runtime config + probing paths
      ├─ {name}.deps.json      assembly probing manifest
      ├─ {name}.sea.json       SeaShell metadata (sources, packages, assemblies)
      ├─ SeaShell.Script.dll   copied from engine dir
      ├─ SeaShell.Ipc.dll      copied from engine dir
      ├─ MessagePack.dll       copied from engine dir
      └─ MessagePack.Annotations.dll
```

The compiled script runs via `dotnet exec --runtimeconfig ... --depsfile ... assembly.dll`.
The `.deps.json` is critical — it tells the dotnet host where to find runtime-specific
and native DLLs, which is the key improvement over CS-Script's assembly loading.

The `.runtimeconfig.json` includes two `additionalProbingPaths`: (1) the engine binary
directory (where bundled DLLs live) and (2) the NuGet global cache (`~/.nuget/packages/`).
This ensures script subprocesses can find bundled DLLs even when running inside a host
application (e.g., csasvc) where the DLL copy might be skipped.

## NuGet Resolution

Packages are resolved from the global cache (`~/.nuget/packages/`). Transitive dependencies
are walked via `.nuspec` files. For each package:

1. Find the best TFM match (net10.0 → net9.0 → ... → netstandard2.0)
2. Collect compile-time DLLs (generic `lib/{tfm}/`) for Roslyn references
3. Collect runtime DLLs (platform-specific `runtimes/{rid}/lib/{tfm}/`) for `.deps.json`
4. Collect native DLLs (`runtimes/{rid}/native/`) for `.deps.json`
5. RID fallback chain: e.g. `win-x64` → `win` → `any`

Missing packages are downloaded automatically. The background updater checks all cached
packages every 8 hours against all configured NuGet sources (including private feeds).

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

## Elevated Console (Windows)

Elevated scripts run in a separate process spawned by the Elevator. To share the CLI's
console (so `Console.WriteLine`, `Console.Title`, and colored output work in the user's
terminal), the script calls:

1. `FreeConsole()` — detach from the hidden console
2. `AttachConsole(cliPid)` — attach to the CLI's console

The CLI's PID is passed via `SEASHELL_CLI_PID`. The Elevator spawns with
`WindowStyle.Hidden` to avoid a visible console flash.

## Cross-Platform

| Aspect | Windows | Linux |
|---|---|---|
| Daemon transport | Named pipes | Unix domain sockets |
| Script pipe | Named pipes (async) | Named pipes (async) |
| Pipe permissions | ACL (current user SID) | File mode 0600 |
| Elevation | Elevator via Task Scheduler | Ignored (`//sea_elevate` is a no-op) |
| Console management | FreeConsole/AttachConsole | N/A |
| Exit delay | Ephemeral console detection | N/A |
| Logging | EventLog sink | Syslog sink |
| Scheduled tasks | Task Scheduler XML | N/A (use systemd) |
