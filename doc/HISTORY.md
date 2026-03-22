# History

## v0.1.6 (2026-03-22)

**Binary running, script-initiated reload, and cross-platform service hosting.**

- **Binary running** — `sea myapp.dll` and `host.RunAsync("myapp.dll")` run pre-compiled
  assemblies with full Sea context. `ScriptCompiler.Compile()` detects `.dll`/`.exe`/extensionless
  binaries and stages them with companion files — both CLI and Host get this for free.
  Three .exe tiers: Tier 1 (apphost → redirect to .dll), Tier 2 (framework-dependent single-file),
  Tier 3 (self-contained single-file). Companion `.sea.json` file enables directives for binaries
  (`{"watch": true}`). ASP.NET Core auto-detected from PE assembly references.
- **`Sea.RequestReload()`** — Scripts can programmatically trigger their own reload with optional
  cache clearing. Works in both direct and watch mode. In direct mode the CLI opens a new daemon
  connection; in watch mode it sends `RecompileRequest` over the held connection. The Host handles
  it internally (it owns the compiler). New IPC messages: `ScriptReloadRequest` (tag 8),
  `RecompileRequest` (tag 30). `RunRequest` gained `ClearCache` field.
- **`CompilationCache.ClearScript()`** — Deletes all cache directories for a script, used by
  `RequestReload(clearCache: true)`.
- **`Sea.IsWatchMode`** — Public property set from `ScriptInit.Watch`.
- **`StartupHook`** — `DOTNET_STARTUP_HOOKS` contract class in SeaShell.Script for injecting
  Sea context into .dll binaries that don't reference SeaShell.Script.
- **`AssemblyInspector`** — PE metadata inspection via `System.Reflection.Metadata`. Checks for
  SeaShell.Script and ASP.NET Core references without loading the assembly.
- **`ScriptHost` reload loop** — `ExecuteAsync` now handles `ScriptReloadRequest` internally with
  a restart loop. Watch mode (`//sea_watch`) starts `ScriptWatcher` embedded in the Host — no
  daemon needed. `ScriptConnection.StopAsync()` sends `ScriptStop` for graceful shutdown.
- **`SeaShell.ServiceHost`** — New NuGet package. Cross-platform service hosting with a 6-line
  consumer API (`ServiceHostBuilder`). Auto-detects init system (Windows Service, systemd, runit,
  OpenRC, sysvinit). `install`/`uninstall`/`start`/`stop`/`status` management commands built into
  the binary. `ServiceHostWorker` runs scripts with crash recovery and optional NuGet update loop
  for zero-locking automatic updates. Embedded init script templates.
- **Graceful service shutdown** — `RunOneInstanceAsync` sends `ScriptStop` through IPC on
  cancellation token, waits for graceful exit, kills after timeout. No orphaned child processes.
- **systemd `KillMode=process`** — Only the main PID receives SIGTERM; child script process gets
  graceful `ScriptStop` via IPC instead of direct signal kill.
- **CI service smoke test** — Full install/start/reload/stop/uninstall cycle on both Windows and
  Linux runners. Verifies PID change (reload worked) and no orphaned processes.
- **Linux binary support** — Extensionless ELF binaries detected and handled as direct executables
  (same as Tier 2/3 .exe on Windows).
- **`HotSwapNotify` extended** — Carries `StartupHookPath` and `DirectExe` fields so watch-mode
  restarts preserve binary execution mode.

## v0.1.5 (2026-03-22)

**Daemon lifecycle, cross-platform packaging, and one-liner installers.**

- **`--stop` via IPC** — `sea --stop` now uses IPC first (works for any running daemon,
  including on Linux), then ends Task Scheduler tasks if registered. Reports actual state
  — no misleading output when nothing is running. Output aligned with `--status` format.
- **Task preference** — `StartDaemon` prefers a registered Task Scheduler task over
  `Process.Start`. Falls back to spawning a process if no task is registered.
- **Elevator auto-start** — When a `//sea_elevate` script fails because the elevator isn't
  connected, the CLI starts the elevator task and the daemon waits internally for the
  elevator's `ElevatorHello` (`AwaitElevatorMs` on `SpawnRequest`). One round trip, no
  CLI polling.
- **Independent binary search** — `--install-daemon` only searches for the daemon binary,
  `--install-elevator` only for the elevator. No more confusing cross-errors.
- **Dotnet tool mode** — `FindBinary` detects `.dll` (dotnet tool store) and registers
  tasks with `dotnet exec` as fallback. `BuildTaskXml` supports `<Arguments>` element.
- **Cross-platform nupkg** — CI packs on Windows (gets `.exe` WinExe apphosts natively),
  then injects Linux ELF apphosts into the nupkg. Both platforms get native executables
  — daemon and elevator use WinExe subsystem on Windows (no console window).
- **CI lifecycle tests** — `--status`, `--stop`, and idempotent stop tested on both
  Windows and Linux runners.
- **`install.ps1`** — One-liner Windows installer: checks .NET 10, installs/updates tool,
  stops running instances, registers daemon task, optionally registers elevator via gsudo,
  associates `.cs`. Usage: `iex (irm https://raw.githubusercontent.com/PLN/SeaShell/main/install.ps1)`
- **`install.sh`** — One-liner Linux installer: checks .NET 10, stops running instances,
  installs/updates tool. Usage: `curl -fsSL https://raw.githubusercontent.com/PLN/SeaShell/main/install.sh | sh`

## v0.1.2 (2026-03-20)

**Binary IPC and Host↔Script messaging.**

- **Binary wire format** — Replaced JSON Envelope framing with binary MessagePack
  serialization. Wire format: `[4-byte LE length][1-byte type tag][MessagePack payload]`.
  `MessageType` byte enum replaces string-based type discrimination.
- **MessagePack** — Contractless resolver (plain C# records, no attributes). Zero-copy
  `byte[]` payloads, no Base64 encoding for binary data. ~2-10x faster than JSON.
- **Host↔Script messaging** — New `HostMessage`/`ScriptMessage` records with binary payload
  and optional topic. `Sea.MessageReceived` event + `Sea.SendMessage`/`SendMessageAsync` on
  the script side. `ScriptHost.ScriptConnection` with event-based receive + `SendAsync` on
  the Host side. CLI silently ignores script messages.
- **TransportStream simplified** — Now a thin wrapper exposing `MessageChannel`. Raw
  `SendAsync(byte[])`/`ReceiveAsync()` replaced with typed binary messaging.
- **`Envelope.cs` deleted** — All JSON serialization removed from IPC layer.
- **GitHub Actions CI** — Build + smoke tests on Windows and Linux. Tag-triggered
  NuGet publish (`v*` tags push to nuget.org).

## v0.1.1 (2026-03-20)

**Unified IPC and branding.**

- **SeaShell.Ipc** — New shared IPC project. `MessageChannel` over `System.IO.Pipelines`
  with length-prefixed `Envelope` framing. Replaces ad-hoc env vars and sidecar files
  with a single bidirectional named pipe between launcher and script.
- **Script pipe** — Script creates the pipe server, CLI/Host connects as client.
  Full lifecycle control: `ScriptInit` → `ScriptReload`/`ScriptStop` → `ScriptState` → `ScriptExit`.
- **Elevated console** — Elevated scripts call `FreeConsole` + `AttachConsole` to share
  the CLI's console window. Console title changes, colored output, and interactive input
  all work transparently. `WindowStyle.Hidden` eliminates the brief console flash.
- **Per-user task names** — Scheduled tasks include the username (`SeaShell Daemon (user)`)
  to avoid conflicts on multi-user machines.
- **Pipe security** — Per-user ACL on Windows (current user SID, FullControl regardless
  of integrity level). Owner-only socket permissions on Linux (0600).
- **`_SeaShellBoot` module initializer** — Injected into every compiled script (C# and VB.NET)
  to guarantee `Sea.Initialize()` runs even if the script never references `Sea.*`.
- **`{~}` branding** — SVG/ICO icon embedded in all binaries. `--help` and `--version`
  show the `{~} SeaShell` header with version and copyright.
- **`--associate`/`--unassociate`** — Per-user file type registration via HKCU
  (no elevation needed). Double-click a `.cs` file to run it with SeaShell.
- **Package hygiene** — Protocol, Daemon, and Elevator marked `IsPackable=false`.
  Package READMEs added for all public packages.

## v0.1.0 (2026-03-19)

**Initial release.**

- Roslyn-based C# and VB.NET scripting engine
- Persistent daemon with warm Roslyn instance
- NuGet resolution with transitive dependencies and runtime-specific DLLs
- `.deps.json` generation for correct assembly probing
- Hot-swap (`//sea_watch`) with state handoff between instances
- REPL with stateful sessions
- Elevation via Task Scheduler worker (`//sea_elevate`)
- Embeddable host library (`SeaShell.Host`)
- CS-Script directive compatibility (`//css_inc`, `//css_nuget`, etc.)
- Cross-platform: named pipes on Windows, Unix domain sockets on Linux
- Background NuGet update checker (8-hour cycle)
- Mixed declaration order (classes and top-level statements in any order)
- Interactive exit delay for ephemeral consoles
