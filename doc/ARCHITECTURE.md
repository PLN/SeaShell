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
│  Script process (per-script apphost)             │
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
| **SeaShell.Bootstrapper** | Dotnet tool (`seashell` command). Extracts per-RID archives, registers daemon/elevator tasks, manages PATH and file associations. `--system` installs for the LocalSystem target directly from an elevated console (no SYSTEM-context execution); `--all` does user + elevator + SYSTEM behind one UAC prompt. `schedule`/`unschedule` for Task Scheduler script management. |
| **SeaShell.Cli** | CLI entry point (sea.exe / seaw.exe). Argument parsing, REPL client, exit delay. Thin wrapper around Invoker. |
| **SeaShell.Invoker** | Shared execution engine. `ScriptInvoker` (compile, execute, watch, reload, restart, mutex, attach, elevate), `DaemonLauncher`, `DaemonManager`, `ScheduledTasks`, `DirectiveScanner`, `ScriptMutex`. Used by CLI, Host, and ServiceHost. |
| **SeaShell.Daemon** | Compilation server. Listens for `RunRequest`, compiles via Engine, returns artifact paths. Holds Elevator connection. Runs `ScriptWatcher` for hot-swap. |
| **SeaShell.Elevator** | Elevated worker (Windows). Connects to daemon, receives `SpawnRequest`, starts elevated processes. Registered via Task Scheduler. |
| **SeaShell.Engine** | Roslyn compiler, NuGet resolver, include system, `.deps.json` writer, compilation cache, script watcher, background updater. |
| **SeaShell.Script** | Runtime context (`Sea` static class). Loaded into every script process. Manages the script-side pipe server, lifecycle events, reload state, and attach server. |
| **SeaShell.Common** | Shared message types, `MessageChannel` (System.IO.Pipelines). Used by Script (in the script process) and Invoker (in the launcher). |
| **SeaShell.Protocol** | Daemon/Elevator protocol messages and `TransportStream` (platform IPC abstraction). |
| **SeaShell.Host** | Embeddable API + cross-platform service hosting. Wraps Invoker for applications that want to run scripts without the CLI. Auto-detects init system (Windows Service, systemd, runit, OpenRC). Management commands, crash recovery, zero-locking NuGet updates. |

## IPC Layers

There are three distinct IPC layers:

All layers use the same binary wire format:

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
  │       No transitive NuGet resolution or Roslyn compilation.
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
      └─ NativeLibrary.dll            ┘ native DLLs (copied flat)
```

The compiled script runs via a per-script native apphost executable generated by
`AppHostWriter`. The apphost embeds the assembly path, so scripts appear as named
processes in task manager. Falls back to `dotnet exec` if apphost generation fails.

### Self-contained output

The output directory is completely standalone — no runtime dependency on the NuGet cache or
engine directory. All DLLs (bundled SeaShell runtime + NuGet packages) are physically copied
to the output dir at compile time. The `.deps.json` uses type:"project" entries exclusively,
which resolve from the app base directory. Managed and native assets are declared by
bare filename. No `additionalProbingPaths` in the runtimeconfig.

This means:
- **Cache-clearing safe** — existing artifacts run without the NuGet cache
- **User-identity independent** — service accounts don't need NuGet cache access at runtime
- **No file locks** — no probing into `~/.nuget/packages/` during execution
- **Portable** — the output dir could be copied to another machine with the same platform and .NET runtime

### Cache hash

The cache key is `SHA256(engine fingerprint + source paths + source content + direct NuGet versions)`.
Direct versions are resolved before cache lookup; transitive versions are excluded.
Full NuGet resolution is skipped on cache hits. Artifacts live in
`{DataDir}/v{version}/cache/{script}_{hash8}/`.

The engine fingerprint is the sum of file timestamps for the Engine, Script, Common, and
MessagePack DLLs. Any rebuild of the engine invalidates all cached scripts.

An output dir counts as a cache hit only once its `{script}.complete` marker exists; the
marker is written last and deleted first, so a crash or an interrupted delete leaves a
miss that the next compile repairs. Compiles of the same output dir are serialized.

### Cache expiry

`CacheTracker` (Engine) records, per output dir, when it was created and last hit. The
daemon owns the only instance for its version's cache dir, keeps it in memory, and writes
it to `cache/.tracker.json` (temp file + move) within 30s of a change and on shutdown.
Losing the file is harmless: dirs are re-adopted from their file times.

| Event | Effect |
|---|---|
| Compile creates an entry | kept 24h after the last hit |
| Compile recreates an entry the sweep removed, within 30 days of its last hit | promoted: kept 30 days after the last hit |
| Cache hit, or `CacheTouchRequest` keep-alive | last hit = now |
| Explicit clear (`clearCache`) | recreation keeps the previous keep period |
| Record of a removed entry, 30 days after its last hit | dropped |

The sweep runs hourly, first once the daemon has been up longer than the keep-alive
interval (10 min + margin) so a restarted daemon has heard from running scripts. It
deletes under the per-dir compile lock after re-checking expiry. While a script runs from
an entry, the invoker (`CacheKeepAlive`) sends `CacheTouchRequest(outputDir)` every
10 minutes, following reloads and hot swaps; a script compiled earlier and started via
`ExecuteAsync` sends one, awaited, before it starts. The entry is the manifest's dir, so
a binary run in place keeps its staged dir. Touches take the per-dir lock too: a touch
either lands before the sweep's re-check or reports the dir as gone.

Keep-alives are the main mechanism; the sweep also refuses to delete an entry that is
in use without them (invoker killed, older CLI, launched directly): on Linux it reads each
process's `/proc/PID/cmdline` (a script's apphost or `dotnet exec <dll>` names its entry;
readable even for non-dumpable processes) and `/proc/PID/maps`, matching the cache dir as
configured and as `realpath` resolves it. Processes in another mount namespace see other
paths and are covered only by keep-alives, as is a process started by hand with a
relative path from inside an entry that also made itself non-dumpable (its argv names no
cache path and its maps are unreadable); the invoker always launches by absolute path.
On Windows the sweep moves the entry into
`cache/.trash/` first — a rename Windows refuses while any file in the dir is open, and
which removes the entry all at once instead of leaving a half-deleted dir. An entry found
in use counts as hit.

## NuGet Resolution

Packages are resolved from the global cache (`~/.nuget/packages/`). Transitive dependencies
are walked via `.nuspec` files. For each package:

1. Find the best TFM match (net10.0 → net9.0 → ... → netstandard2.0)
2. Collect compile-time DLLs (generic `lib/{tfm}/`) for Roslyn references
3. Collect runtime DLLs (platform-specific `runtimes/{rid}/lib/{tfm}/`) for `.deps.json`
4. Collect native DLLs (`runtimes/{rid}/native/`) for `.deps.json`
5. RID fallback follows the portable RID graph: `win-x64` → `win` → `any`;
   `linux-x64` → `linux` → `unix-x64` → `unix` → `any`;
   `linux-musl-x64` → `linux-musl` → `linux-x64` → `linux` → `unix-x64` → `unix` → `any`;
   `osx-arm64` → `osx` → `unix-arm64` → `unix` → `any`.
   Architecture comes from `RuntimeInformation.ProcessArchitecture`.

Direct package versions are checked before cache lookup; transitive NuGet resolution
only runs on a cache miss. On a cache hit, the output dir supplies the runtime assets.

Missing packages are downloaded automatically via `dotnet restore` on a temporary project.
The background updater checks all cached packages every 8 hours against all configured
NuGet sources (including private feeds).

## Execution Paths

### Direct (non-elevated, non-watch)

```
CLI → Daemon.RunRequest → compile → RunResponse(artifacts)
CLI → Process.Start(apphost or dotnet exec, SEASHELL_PIPE=seashell-{guid})
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
Elevated script → Sea.Initialize → create pipe server → accept CLI connection
CLI → connect → send ScriptInit
Elevated script → enable SeaConsole relay over script pipe
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
  If no args: AllocConsole() (then report missing script)
  If error + no console: write to Windows Event Log
seaw.exe → normal Invoker flow (same as sea.exe)
```

## Elevated Console (Windows)

Elevated scripts run in a separate process spawned by the Elevator. Console I/O and
control operations are relayed over the script pipe to the CLI's terminal:

1. `Sea.Initialize()` establishes the script pipe and receives `ScriptInit`.
2. `SeaConsole.EnableRelay()` redirects console streams and relays control operations.

The Elevator sets `SEASHELL_CLI_PID` in the child environment to enable the relay
and uses `WindowStyle.Hidden` to avoid a visible console flash.

## Script Mutex

`DirectiveScanner` reads the first ~20 lines of a script to detect mutex directives
before contacting the daemon. `ScriptMutex.TryAcquire()` is called pre-compilation.

| Scope | Windows | Linux |
|---|---|---|
| System | `Global\SeaShell_{identity}` (named kernel mutex) | File lock in the temp directory |
| User | `Global\SeaShell_{identity}_u{sid}` (named kernel mutex) | File lock in `$XDG_DATA_HOME/seashell/mutex/` (default `~/.local/share/seashell/mutex/`) |
| Session | `Local\SeaShell_{identity}_s{sessionId}` (named kernel mutex) | File lock in `$XDG_RUNTIME_DIR/seashell/` (fallback `{temp}/runtime-{username}/seashell/`) |

User scope additionally holds the pre-0.6.0 name (`SeaShell_{identity}_u{username}`,
session-local namespace) during the 0.6.x transition so mixed versions exclude
each other; the shim is removed in 0.7.

The identity is an FNV-1a hash of the normalized script path, so the same script always
gets the same mutex name regardless of how it's invoked (relative vs absolute path).

When `//sea_mutex_attach` is active, the running instance starts an `AttachServer` on
pipe `seashell-attach-{identity}`. Blocked callers connect instead of exiting. The
`Sea.Attached` event fires for each client with an `AttachContext` that provides the
caller's args, working directory, and bidirectional `Send`/`Receive`/`Close` methods.

## Identity

Canonical identity-derived values (daemon pipe/socket names, data directories, task
principals, Windows user-scope lock names) use `SeaShellIdentity`; legacy mutex
names and Unix session-lock fallback paths still use `Environment.UserName`.
The canonical token is the lowercase SID string on Windows
(`s-1-5-18` for SYSTEM) and the numeric uid on Unix.

The key property: a target identity is computable from a different security
context. `SeaShellIdentity.LocalSystem` gives an elevated administrator every
value of a SYSTEM install (well-known SID, `systemprofile` data dir) without
any code running as SYSTEM — which is what lets `seashell install --system`
run directly in the elevated bootstrapper.

Endpoint names are `seashell-{version}-{token}`; the version is everything up
to the first hyphen (versions never contain one), the token is the remainder,
so SID tokens with hyphens parse cleanly and pre-0.6.0 username-based names
parse identically. `SeaShellIdentity.LegacyToken` preserves the pre-0.6.0
username form so installers still recognize (and stop) old daemons during
migration.

## Version Matching

Each version has its own directory, daemon binary, and named pipe (the version is
part of the endpoint name). The invoker first probes its exact version's endpoint,
then accepts a reachable newer daemon for the same identity. If none is reachable,
it starts a daemon via Task Scheduler or a `daemon.path` marker from its version
or a newer installed version.

There is no protocol-version handshake. Messages use MessagePack maps keyed by
property name through `ContractlessStandardResolver`: additive fields are tolerated,
while property renames and type changes can break compatibility.

`PingResponse` carries: `Version` (4-part), `Pid`, `UptimeSeconds`, `ActiveScripts`,
`IdleSeconds`, `IdleTimeoutSeconds`, `ElevatorVersion`.

## Handle Inheritance

When spawning the daemon, `DaemonManager` prevents handle inheritance through two layers:

1. **Redirect + close** (cross-platform) — stdin/stdout/stderr are redirected and
   immediately closed after `Process.Start()`.
2. **StdHandleInheritGuard** (Windows) — temporarily clears `HANDLE_FLAG_INHERIT`
   on the standard handles before spawning. Prevents the daemon from inheriting any
   inheritable standard handles that would block SSH sessions, pipe readers, or test harnesses.

## Cross-Platform

| Aspect | Windows | Linux |
|---|---|---|
| Daemon transport | Named pipes | Unix domain sockets |
| Script pipe | Named pipes (async) | Named pipes (async) |
| Attach pipe | Named pipes | Named pipes |
| Daemon pipe/socket permissions | ACL (owner SID FullControl + elevated Administrators ReadWrite) | File mode 0600 |
| Mutex | Named kernel mutex (Global/Local) | flock |
| Elevation | Elevator via Task Scheduler | Ignored (`//sea_elevate` is a no-op) |
| Window mode | seaw.exe (WinExe subsystem) | N/A |
| Console management | Console allocation/attachment, elevated pipe relay | N/A |
| Exit delay | Parent process detection | N/A |
| Logging | EventLog sink | Syslog sink |
| Scheduled tasks | Task Scheduler XML | N/A (use systemd) |
