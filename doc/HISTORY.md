# History

## v0.3.17 (2026-03-29)

**Invoker refactoring: shared execution engine, package consolidation, security hardening.**

The v0.3 line extracts the execution logic into a shared Invoker library. CLI, Host,
and ServiceHost are now thin wrappers around the same core — same code paths for
compilation, execution, elevation, watch mode, and reload.

- **SeaShell.Invoker** — New library containing the shared client-side execution engine:
  `ScriptInvoker` (compile, execute, watch, reload, elevate), `DaemonLauncher` (ensure
  daemon running), `DaemonManager` (status, stop, binary staging, version-check),
  `ScheduledTasks` (Windows Task Scheduler management), `ServiceManifest` (installation
  state tracking).
- **CLI gutted** — `ScriptRunner` reduced from 560 to 35 lines. All execution logic
  delegated to `ScriptInvoker`. `DaemonManager` and `ScheduledTasks` moved to Invoker.
- **Host rewritten** — `ScriptHost` is now a thin wrapper around `ScriptInvoker` with
  `OutputMode.Capture`. Dropped direct Engine/Roslyn dependency — compilation happens
  in the daemon, not in the host process.
- **ServiceHost merged into Host** — `ServiceHostBuilder`, `ServiceHostWorker`, init system
  detection, and all service installers moved into SeaShell.Host. Same NuGet package, same
  namespace. Consumers reference one package instead of two.
- **Package consolidation** — 7 public NuGet packages → 3: SeaShell (CLI tool),
  SeaShell.Host (library + service hosting), SeaShell.Service (daemon/elevator binaries).
  Internal projects (Common, Engine, Protocol, Invoker, Script) are bundled, not published.
- **Service manifest** — `seashell.json` in the data directory tracks installations per
  version: staged binary paths, hashes, pipe addresses, and registered scheduled tasks.
  File-locked for concurrent access. `ServiceManifest.GetOrStageDaemon()` finds daemon
  binaries from the CLI tool directory or the SeaShell.Service NuGet package, stages them,
  and records the result.
- **Side-by-side versioning** — Daemon pipe/socket addresses include the 3-part assembly
  version (`seashell-{version}-{identity}`). Multiple SeaShell versions can run concurrently
  without conflicts. Each version gets its own daemon, cache, and staged binaries.
- **Daemon idle timeout** — Configurable via `SEASHELL_IDLE_TIMEOUT` environment variable
  or `--idle-timeout` flag. Supports `ms`, `s`, `m`, `h` suffixes. Zero (default) means
  stay active indefinitely. Useful for CI and ephemeral environments.
- **Security: service installer argument injection** — All five service installers
  (Windows Service, systemd, sysvinit, OpenRC, runit) switched from string-based
  `ProcessStartInfo` arguments to `ArgumentList`. Prevents argument injection if service
  names or paths contain special characters.
- **Security: Unix socket TOCTOU** — Socket and pipe creation on Linux now sets
  `umask(0077)` before `Bind()`/`new NamedPipeServerStream()` and restores after.
  Closes the brief window where the socket was world-accessible between creation and
  `SetUnixFileMode()`. New `PosixUmask` helper in SeaShell.Common. Existing
  `SetUnixFileMode` calls retained as defense-in-depth.
- **Pipeline test** — `daemon socket permissions` test verifies the daemon socket is
  created with owner-only permissions (0600) on Linux. Passes on Debian and Alpine.

## v0.2.16 (2026-03-28)

**Alpine Linux (linux-musl-x64) support, pipeline test rework.**

- **Alpine / musl support** — NuGet resolver detects musl-based Linux via
  `RuntimeInformation.RuntimeIdentifier` and `/etc/alpine-release`, returns
  `linux-musl-x64` RID with fallback chain `linux-musl-x64` → `linux-x64` →
  `linux` → `any`. Daemon manager probes `runtimes/linux-musl-x64/` for musl
  apphosts and overwrites glibc variants during staging. Falls back to
  `dotnet exec` if musl apphost is missing.
- **Pipeline packaging** — Injects musl apphosts into `runtimes/linux-musl-x64/`
  in the tool nupkg alongside the existing glibc apphosts in `tools/net10.0/any/`.
  Conditional on musl artifacts being present.
- **GitHub Actions CI** — Alpine build via `mcr.microsoft.com/dotnet/sdk:10.0-alpine`
  container in the matrix. Release job downloads, injects, and validates musl
  apphosts.
- **Pipeline test rework** — Three-phase structure: contained tests first, then
  install/update for both current user and root/SYSTEM, then smoke tests.
  No rollback — develop forward. Clears all caches (compilation, daemon staging,
  elevator staging) and unregisters stale Task Scheduler tasks before install.

## v0.2.15 (2026-03-27)

**Daemon staging fix, .NET 10 dependency cleanup, cache hash includes direct NuGet versions.**

- **Recursive daemon staging** — `StageBinary()` now copies subdirectories (including
  `runtimes/`) when staging daemon and elevator binaries. Previously only top-level files
  were copied, causing `System.Diagnostics.EventLog.Messages.dll` (in `runtimes/win/lib/`)
  to be missing at runtime. Fixes daemon startup failure on machines where the NuGet cache
  doesn't have the package (e.g., installed as a dotnet tool, not built from source).
  `ComputeDirHash()` updated to include DLLs from subdirectories.
- **System.Diagnostics.EventLog upgraded to 10.0.5** — Overrides the transitive 8.0.0
  (from Serilog.Sinks.EventLog 4.0.0) via Directory.Build.props. The 8.0.0 package only
  ships `lib/net8.0/` and `runtimes/win/lib/net8.0/` assets; 10.0.5 has proper `net10.0`
  assets. Also upgrades ServiceHost from the preview version to stable.
- **System.IO.Pipes.AccessControl removed** — The 5.0.0 NuGet package (only `lib/net5.0/`
  assets) is unnecessary on .NET 10 where `PipeSecurity`, `PipeAccessRule`, and
  `NamedPipeServerStreamAcl` are inbox in the shared framework. Removed from Protocol.csproj
  along with the `NU1510` warning suppression.
- **Cache hash includes direct NuGet versions** — `CompilationCache.ComputeHash()` now
  includes resolved versions of direct `//sea_nuget` package references. For explicit
  versions this is zero-cost (already in source text). For versionless directives, a single
  `Directory.GetDirectories()` call resolves the latest version from the NuGet cache (~1-2ms).
  Transitive dependencies are not included (immutable by convention). This ensures versionless
  `//sea_nuget Foo` directives pick up new package versions without requiring a source change.

## v0.2.14 (2026-03-25)

**Self-contained compilation output. The output directory is now standalone like `dotnet publish`
— all DLLs copied at compile time, no runtime dependency on the NuGet cache. Re-runs skip
NuGet resolution entirely.**

- **Self-contained output** — All NuGet package DLLs (managed and native) are copied to the
  output dir at compile time. All `.deps.json` entries use type:"project" (resolved from app
  base dir). No `additionalProbingPaths` in `.runtimeconfig.json`. The NuGet cache is only
  needed during compilation, never at runtime. Eliminates file lock contention, cross-user
  cache path issues, and stale-cache runtime failures under service accounts.
- **Cache hash simplified** — Hash is now source content + engine fingerprint only. NuGet
  package versions removed (packages are immutable by convention). NuGet resolution skipped
  entirely on cache hits — re-runs are instant (~1ms include resolution + hash check).
- **Compilation pipeline reordered** — NuGet resolution moved after the cache check. On cache
  hit: resolve includes → hash → return cached artifacts. NuGet resolution (10-200ms) only
  runs on cache miss (first compilation or source change).
- **Snippet caching** — `ScriptHost.RunSnippetAsync` uses content-based filenames instead of
  random GUIDs. Identical code maps to the same cache key. Cache hit = zero disk I/O (no temp
  file creation, no NuGet resolution, no compilation).
- **StartupHook namespace fix** — `StartupHook` moved from `SeaShell` namespace to root
  namespace. `DOTNET_STARTUP_HOOKS` requires `Assembly.GetType("StartupHook")` which only
  matches types without a namespace. Fixes Sea context injection for pre-compiled binaries
  run via `sea myapp.dll`.
- **Binary deps.json merge** — `CompileBinary()` now merges SeaShell's bundled entries into
  companion `.deps.json` and adds probing paths to companion `.runtimeconfig.json`, instead of
  copying them verbatim. Pre-compiled binaries with their own NuGet dependencies get correct
  SeaShell runtime resolution alongside their own packages.
- **Test suite expanded** — 14 pipeline tests on both Windows and Linux (was 10):
  - `host-in-host-nuget`: nested ScriptHost where inner snippet uses NuGet
  - `engine-dir-nuget`: ScriptHost via `dotnet run` (NuGet cache layout regression guard)
  - `binary-deps`: pre-compiled binary with companion deps.json + Sea context via startup hook
  - `service-cwd`: ServiceHost with `//sea_nuget seashell.host` under SYSTEM/root
  - `service-identity`: NuGet cache isolation verified on service account switch
- **Unit tests expanded** — DepsJsonWriter tests for version skew, NuGet overlap with bundled
  DLLs, empty engine dir fallback. ArtifactWriter tests updated for self-contained output.

## v0.1.12 (2026-03-24)

**Bundled DLL resolution fix, engine dir probing, unit test suite.**

- **Engine dir in probing paths** — `ArtifactWriter.WriteRuntimeConfig` now includes
  the engine binary directory in `additionalProbingPaths`. Script subprocesses can find
  bundled DLLs (MessagePack, SeaShell.Ipc, SeaShell.Script) via the host's directory as
  a fallback when the copy to the output dir is skipped or fails. Fixes assembly
  resolution failures in host-embedded scenarios (e.g., service host bridge scripts).
- **Dynamic bundled DLL versions** — `DepsJsonWriter` reads actual assembly versions from
  the bundled DLLs via `AssemblyName.GetAssemblyName()` instead of hardcoding `"1.0.0"`.
  The old hardcoded versions caused the .NET host to fail resolving MessagePack from the
  NuGet cache.
- **DirectiveParser unit tests** — 42 tests covering whitespace, comments containing
  directives (the CS-Script bug), scanning boundaries, NuGet variants, VB support.
- **DepsJsonWriter + ArtifactWriter unit tests** — Verify bundled entries use correct
  versions, runtimeconfig includes engine dir and NuGet cache in probing paths.
- **Host-resolution integration test** — End-to-end test in the pipeline: ScriptHost
  compiles and runs a script that verifies MessagePack.dll and SeaShell.Script.dll
  resolve correctly. Catches the exact class of bug that hit host-embedded consumers.
- **Build attestation** — CI workflow attests all `.nupkg` files with
  `actions/attest-build-provenance` (Sigstore + SLSA provenance). Consumers verify with
  `gh attestation verify <file> --repo PLN/SeaShell`.
- **Conditional NuGet push** — Prerelease tags (e.g., `v0.1.12-rc1`) run the full CI
  pipeline but skip the nuget.org push. Clean version tags push as before.
- **InternalsVisibleTo** — Engine exposes internals to the EngineTest project.

## v0.1.10 (2026-03-23)

**Engine self-contained DLL resolution, Host packaging fixes, local CI/CD pipeline.**

- **Self-contained DLL resolution** — Engine locates bundled DLLs from its own assembly
  directory (`typeof(ScriptCompiler).Assembly.Location`), correct for both daemon and
  NuGet-hosted ScriptHost scenarios. Falls back to `AppContext.BaseDirectory`.
- **Host NuGet packaging** — `build/` and `buildTransitive/` directories in the Host
  package carry `.targets` files that inject SeaShell.Script as a compile-time reference
  and copy MessagePack/Ipc DLLs to consumers' output directories.
- **SeaShell.Script removed as standalone NuGet** — Script is now bundled as a binary
  inside Engine, Host, and the CLI tool packages. No standalone `SeaShell.Script` NuGet
  package. This eliminates version conflicts when multiple packages reference different
  Script versions.
- **Host-in-Host regression test** — Pipeline test that runs ScriptHost from inside a
  Host consumer. Catches CS1704 (duplicate assembly) regressions.
- **NuGet transitive dedup** — Engine deduplicates compile-time references when a script's
  NuGet dependencies transitively include assemblies already bundled by the Engine.
- **Local CI/CD pipeline** — Build, package, and test on both Windows and Linux build hosts
  via SSH. Shared storage for cross-platform artifact exchange. Convention-based directory
  layout with timestamp correlation IDs.

## v0.1.7 (2026-03-22)

**Daemon hash fix.**

- **Daemon hash mismatch on Task Scheduler** — Fixed: Task Scheduler launches used the
  tool store path directly, bypassing the staged daemon directory. The CLI's hash check
  always saw a mismatch and restarted the daemon on every script run. Now consistently
  launches from the staged directory.

## v0.1.6 (2026-03-22)

**Binary running, script-initiated reload, cross-platform service hosting, and daemon staging.**

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
- **`SeaShellPaths`** — New static class centralizing all data directory paths. Per-user
  `%LOCALAPPDATA%\seashell\` (Windows) or `~/.local/share/seashell/` (Linux). System accounts
  (SYSTEM/root) use `%ProgramData%\seashell\` or `/var/lib/seashell/`. Override with `SEASHELL_DATA`
  env var. Compilation cache moved from `%TEMP%` to persistent AppData.
- **Daemon staging** — `DaemonManager.StartDaemon()` copies daemon binaries to
  `{DataDir}/daemon/{hash}/` before launching. Eliminates DLL lock conflicts: `dotnet build`
  succeeds while daemon is running. Same for elevator via `{DataDir}/elevator/{hash}/`.
  `--install-daemon`/`--install-elevator` register staged paths with Task Scheduler.
- **Daemon version-check** — `PingResponse` gains `Pid` and `DaemonHash` fields. On script run,
  CLI compares running daemon's hash with staged hash. Mismatch triggers automatic restart.
  Last-resort PID kill if IPC stop fails.
- **`.runtimeconfig.dev.json` generation** — Staged binaries get a dev config with
  `additionalProbingPaths` pointing to the NuGet cache, so package dependencies (EventLog,
  Serilog sinks, etc.) resolve correctly from the staging directory.

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
