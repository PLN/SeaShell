# Security

SeaShell compiles and executes arbitrary code. This document describes the security
boundaries and mitigations in place.

## Threat Model

SeaShell is a **developer tool** — it compiles and runs scripts provided by the user.
It does not sandbox script execution. A script has full access to everything the user
(or elevated user) can access. The security focus is on:

1. Preventing other local users from executing code via your SeaShell instance
2. Making silent elevation an explicit opt-in
3. Keeping the daemon and elevator attack surface minimal

## IPC Security

### Daemon pipe/socket

| Platform | Mechanism | Effect |
|---|---|---|
| Windows | Named pipe with SID ACL | Owner and elevated Administrators can connect |
| Linux | Unix domain socket with mode 0600 | Only the file owner can connect |

The daemon uses `seashell-{version}-{identity}`; the transport also defines
`seashell-elevated-{version}-{identity}` (Unix socket names add `.sock`). The identity
is the lowercase Windows SID or numeric Unix uid. Linux sockets are created under
umask `0077`, then chmod `0600`, in `$XDG_RUNTIME_DIR` when set and existing,
otherwise in `Path.GetTempPath()` (usually `/tmp`).

Windows daemon pipes grant the owner SID `FullControl` and
`BUILTIN\Administrators` `ReadWrite` (effective only when elevated). Remote clients
are not rejected: authenticated SMB clients that pass the ACL can connect.

### Script pipe

Each script gets a unique pipe name (`seashell-{guid}`). The pipe server is created by
the script process with:

- **Windows**: Explicit ACL granting `FullControl` to the current user's SID only.
  The ACL works across integrity levels (so a non-elevated CLI can connect to an
  elevated script's pipe and vice versa).
- **Linux**: Owner-only file permissions (0600) on the socket file.

GUID-based naming makes script pipe names difficult to predict before launch.

### Wire format

All IPC uses binary MessagePack with length-prefixed framing
(`[4-byte LE length][1-byte type tag][MessagePack payload]`). There is no
authentication token or encryption on the pipe — security relies on OS-level
access control (ACLs and file permissions). Clients, including the Elevator, do not
authenticate the endpoint owner's identity.

## Elevation

### Windows Elevator

The Elevator is a pre-elevated process registered via Task Scheduler with
"Run with highest privileges." It connects **to** the daemon (not the other way around)
and only accepts `SpawnRequest` messages from the daemon connection.

**What the Elevator can do:**
- Start processes with elevated privileges
- Launch caller-supplied executables (`DirectExe`) or assemblies via `dotnet exec`
- Use caller-supplied paths, arguments, working directory, and environment

**What the Elevator cannot do:**
- Listen on any public endpoint (it only connects outward to the daemon)

Any same-user process that can open the daemon pipe can send `SpawnRequest` to a
connected Elevator and start an arbitrary executable elevated. Installing the
Elevator therefore grants that user's unelevated processes silent elevation — a
deliberate convenience trade-off, enabled by an elevated install.

### Elevation cascade

1. CLI checks if it is already elevated → run directly
2. CLI asks daemon if Elevator is connected → delegate spawn
3. Elevator task registered but not running → start it and wait up to 15s, then retry
4. Otherwise → elevation error; no automatic `gsudo` fallback

On Linux, `//sea_elevate` is silently ignored. Use `sudo sea script.cs` instead.

### Console relay

Elevated scripts relay console I/O and control operations over the script pipe to
the CLI. The Elevator sets `SEASHELL_CLI_PID` in the child environment to enable
the relay.

## Mutex & Attach

### Script mutex

`//sea_mutex` uses OS-level synchronization primitives:

- **Windows**: Named kernel mutexes. System and user scopes use `Global\`; user scope
  includes the SID. Session scope uses `Local\` with a session ID. Mutexes use
  default OS security.
- **Linux**: File locks in scope-appropriate directories. System scope uses the temp
  directory, user scope uses `$XDG_DATA_HOME/seashell/mutex/` (or
  `~/.local/share/seashell/mutex/`), and session scope uses `$XDG_RUNTIME_DIR/seashell/`
  (or the temp directory's `runtime-{username}/seashell/`). Directory permissions
  control access.

### Attach pipe

`//sea_mutex_attach` creates a named pipe (`seashell-attach-{identity}`) with the same
per-user ACL/permissions as the script pipe (see above). Only the same user can connect.
The identity is a deterministic hash of the script path — not secret, but the ACL
prevents unauthorized connections.

## File System

### Compilation cache

Compiled artifacts are written to `{DataDir}/v{version}/cache/{script}_{hash8}/`.
The data directory defaults to `%LOCALAPPDATA%\seashell\` on Windows and
`$XDG_DATA_HOME/seashell/` (or `~/.local/share/seashell/`) on Linux; root uses
`/var/lib/seashell/`. `SEASHELL_DATA` overrides it. Access follows file-system
permissions; the compiler attempts to set the Unix data root to mode `0700`.

### NuGet packages

Packages are resolved from the global NuGet cache (`~/.nuget/packages/`). SeaShell
trusts packages in this cache — it does not verify signatures. This is the same trust
model as `dotnet build` and `dotnet run`.

The background updater downloads packages from configured NuGet sources (including
private feeds from `nuget.config`). Downloads go through the standard NuGet HTTP API.

## Recommendations

- **Don't run untrusted scripts.** SeaShell executes arbitrary code with your privileges.
- **Use the Elevator sparingly.** Install it only if you accept silent elevation
  for same-user processes with daemon pipe access.
- **Review `nuget.config` sources.** The daemon downloads from all configured sources.
  Malicious sources could supply trojanized packages.
- **Restrict SMB access.** SeaShell creates no TCP listener, but Windows named pipes
  can be reached over SMB by authenticated clients allowed by the ACL.
