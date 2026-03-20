# History

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
