# Security

SeaShell compiles and executes arbitrary code. This document describes the security
boundaries and mitigations in place.

## Threat Model

SeaShell is a **developer tool** — it compiles and runs scripts provided by the user.
It does not sandbox script execution. A script has full access to everything the user
(or elevated user) can access. The security focus is on:

1. Preventing other local users from executing code via your SeaShell instance
2. Preventing privilege escalation beyond what the user already has
3. Keeping the daemon and elevator attack surface minimal

## IPC Security

### Daemon pipe/socket

| Platform | Mechanism | Effect |
|---|---|---|
| Windows | Named pipe with per-user SID ACL | Only the creating user can connect |
| Linux | Unix domain socket with mode 0600 | Only the file owner can connect |

The daemon listens on a user-specific endpoint (`seashell-{username}` on Windows,
`seashell-{username}.sock` on Linux). Other users on the same machine cannot connect
to your daemon or execute code through it.

### Script pipe

Each script gets a unique pipe name (`seashell-{guid}`). The pipe server is created by
the script process with:

- **Windows**: Explicit ACL granting `FullControl` to the current user's SID only.
  The ACL works across integrity levels (so a non-elevated CLI can connect to an
  elevated script's pipe and vice versa).
- **Linux**: Owner-only file permissions (0600) on the socket file.

GUID-based naming prevents pipe squatting — an attacker cannot predict the pipe name
and create a server before the script does.

### Wire format

All IPC uses binary MessagePack with length-prefixed framing
(`[4-byte LE length][1-byte type tag][MessagePack payload]`). There is no
authentication token or encryption on the pipe — security relies on OS-level
access control (ACLs and file permissions). This is appropriate for local IPC
between processes owned by the same user.

## Elevation

### Windows Elevator

The Elevator is a pre-elevated process registered via Task Scheduler with
"Run with highest privileges." It connects **to** the daemon (not the other way around)
and only accepts `SpawnRequest` messages from the daemon connection.

**What the Elevator can do:**
- Start processes with elevated privileges
- Only processes using `dotnet exec` with daemon-compiled assemblies
- Only when explicitly requested by a `//sea_elevate` script

**What the Elevator cannot do:**
- Listen on any public endpoint (it only connects outward to the daemon)
- Execute arbitrary commands (it runs `dotnet exec` with specific arguments)
- Be contacted by anything other than the daemon it connected to

### Elevation cascade

1. CLI checks if it is already elevated → run directly
2. CLI asks daemon if Elevator is connected → delegate spawn
3. Neither → error with suggestion to use `gsudo` or install the Elevator

On Linux, `//sea_elevate` is silently ignored. Use `sudo sea script.cs` instead.

### Console attachment

Elevated scripts call `AttachConsole(cliPid)` to share the CLI's console. This
requires the CLI process to be running and accessible. The CLI PID is passed via
environment variable, not over any public channel.

## File System

### Compilation cache

Compiled artifacts are written to `%TEMP%/seashell/cache/` (per-user temp directory).
Other users cannot read or tamper with cached compilations.

### NuGet packages

Packages are resolved from the global NuGet cache (`~/.nuget/packages/`). SeaShell
trusts packages in this cache — it does not verify signatures. This is the same trust
model as `dotnet build` and `dotnet run`.

The background updater downloads packages from configured NuGet sources (including
private feeds from `nuget.config`). Downloads go through the standard NuGet HTTP API.

## Recommendations

- **Don't run untrusted scripts.** SeaShell executes arbitrary code with your privileges.
- **Use the Elevator sparingly.** Only install it if you regularly run `//sea_elevate`
  scripts. Without it, elevation requires explicit `gsudo` invocation.
- **Review `nuget.config` sources.** The daemon downloads from all configured sources.
  Malicious sources could supply trojanized packages.
- **Firewall the daemon.** Named pipes and Unix domain sockets are local-only by design.
  No network listener is created.
