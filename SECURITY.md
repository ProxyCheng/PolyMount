# Security policy

PolyMount runs a Windows service that mounts virtual drives, and it loads plugin assemblies named
in its configuration. Treat both as privileged: the service account can read whatever the mounted
sources expose, and a plugin assembly runs with the service's rights.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting on this
repository (Security → Report a vulnerability), or contact the maintainer directly.

Include the version, the configuration (redact repository URLs and credentials), and the smallest
reproduction you can manage.

## In scope

- Elevation of privilege through the service, its named pipes, or the IPC protocol
- A mount escaping its mount point, or one instance reaching another's configuration or store
- Credential or data disclosure: logs, recording bundles, or the export format
- Unsafe plugin loading (a path that is resolved somewhere it should not be)

## Out of scope

- Anything that requires the ability to write the service's configuration already
- Missing code signing on the distributed binaries (documented; there is no certificate)
- Vulnerabilities in WinFsp, the Subversion client, Qt, or the .NET runtime — please report those
  upstream, though a note here is welcome if PolyMount's use of them is what triggers the issue

## The control plane

Every front-end talks to the service over named pipes (`PolyMount.Control` for the supervisor and
`PolyMount.Control.<instance-id>` per instance) with line-delimited JSON.

### Who may connect

`AppConfig.ControlUsers` names the accounts that may drive the service, by SID:

```jsonc
"ControlUsers": [
  { "Sid": "S-1-5-21-2505-...-1001", "Name": "WORKSTATION\\you" }
]
```

The pipe is created with a descriptor made from that list — the accounts named, plus SYSTEM,
Administrators and the account the service runs as. The operating system refuses every other
account at connect time, so a standard local user cannot issue `shutdown`, `saveConfig`,
`saveCredential` or any other command. A network logon does not carry any of those SIDs, so this
was never a remote boundary and still is not.

Where the list comes from:

- the installer (`installer/Install-Host.ps1`) seeds it from the account that ran setup;
- `scripts/Install-Service.ps1` and `scripts/Update-PolyMount.ps1` do the same for a development
  installation;
- a configuration created by a service started in session 0 cannot know who the user is, so it is
  created with an empty list.

**An empty list keeps the old behaviour** — every interactive session may connect — rather than
locking a working installation out after an upgrade. That state is not silent: the supervisor logs
it at start, and every `status` answer carries it in `SecurityWarning`.

Beyond the descriptor, the server identifies the caller by opening the process on the other end of
the connection (`GetNamedPipeClientProcessId` → process token → SID and administrator membership)
and refuses a caller the policy does not name. The descriptor is the enforcement; this is the same
decision made again per request, and it is what puts the caller's account in the log.

**When the caller cannot be read, the request is served and the log says so.** Reading a token needs
access to a process owned by another account, and the account the setup creates — `LocalService` —
does not have it, while impersonating the connection is not available to a `NamedPipeClientStream`
client (it connects anonymously, and asking for an impersonation level means writing the client by
hand). A product that refuses what it cannot name is a product that does not work after it is
installed, which is exactly what 0.8.0 did: every front-end was refused by its own service. The
descriptor already decided who may connect, from the same list this check uses; the check still
refuses whenever it can read the caller, which is the case the descriptor cannot cover — a policy
reloaded after the pipe was created. Serving the request without naming the caller is logged once
per process, and running the service as an account that can read other users' processes puts the
caller back in the log.

### A configuration leaves with its secrets taken out

`getConfig` answers with `Source.Password` and `Source.ClientCertificatePassword` replaced by
`[redacted]`, at the IPC boundary rather than in one front-end. A `saveConfig` that still carries
`[redacted]` puts the stored value back, matched by instance and layer identity; any other value is
written as given, which is how the tray sets one.

### Nothing is loaded before it is checked

A configuration is validated in two steps. Everything that can be decided without any plugin's code
— names, instance identities, stores, colors, the account list, plugin entry shape — runs first.
Only then is the plugin set loaded, and only then are the layer types those plugins provide checked.
A document that is refused puts the running plugin set back, so a failed save cannot leave the
process answering from a configuration nobody saved.

A plugin path is resolved against the host directory and refused if it lands outside it (after
canonicalisation, so `plugins/../../x.dll` is refused too). A plugin is code that runs with the
service's rights; the installation directory is writable only by an administrator, and that is the
same account that could replace the service itself.

### Squatting and impersonation

A pipe name is a first-come claim: a process that creates `PolyMount.Control` before the service
does shares the name with it, and a connection may land on either. Both ends check.

- The service logs an error when the name is already taken before it starts listening, so the
  situation is visible instead of showing up as odd answers.
- The client (`ControlClient.VerifyServer`) asks the pipe who served it and refuses a server whose
  image is not `PolyMount.Service.exe` (a server inside the same process — the tests — is exempt).

What this does not do: verify the image's signature or its path. A copy of the service binary run
by another account would pass. Closing that needs a signing policy or a per-boot secret; it is
listed under "Not covered" below rather than pretended.

### Bounded reading

The server caps what a connection can cost it: 32 concurrent requests, a 4 MiB request line, 30
seconds to send one, and errors from accept/create are logged and retried rather than allowed to
fault the host. A worker uses the same server, so the limits apply to both.

## The data plane

### Where a path may go

`VfsPath.Normalize` is the single canonicaliser: forward slashes, no leading or trailing slash, and
components that are `.`/`..`, carry a `:`, or are reserved device names (`NUL`, `COM1`, `CON.txt`, …)
are refused. A source listing containing one of those refuses that path rather than reaching a
device or a parent directory with the service's rights.

`DirectoryLayer` compares the canonicalised path against its root and, for every open, asks the
file system where the handle actually points (`GetFinalPathNameByHandle`), which is the only answer
that accounts for a link in the middle of a path. Reparse points inside the root are not listed, not
resolved and not opened unless the source sets `Source.Parameters.FollowReparsePoints` — a junction
is one line to create and can name any directory on the machine.

### The network

- **Certificates must verify.** `Source.TrustServerCertFailures` defaults to empty. A self-signed
  internal server names the failures it accepts (`unknown-ca`, `cn-mismatch`, `expired`,
  `not-yet-valid`, `other`), exactly like `svn --trust-server-cert-failures`. A server that
  presents no certificate at all is never accepted, whatever the list says. Configurations written
  before this changed keep their explicit value.
- **Plain http needs a decision.** A `http://` repository is refused unless the source sets
  `AllowInsecureTransport`; the listing becomes the mounted files and the account travels with the
  request.
- **The repository root must stay on the configured origin.** The WebDAV root is the server's
  answer, and every later request — and the credential offered on a challenge — is built from it.
  A root on another scheme, host or port is refused rather than followed.
- **A credential is bound to where it was first used.** `CredentialStore.Use` records the origin
  the first time a credential opens a source and refuses a layer that names the same credential for
  a different origin. One account usually opens several repositories, so the binding is a recorded
  first use rather than a per-address store — and a second server that shares the same account is
  named explicitly: `saveCredential` carries an `Origins` list that replaces the record's
  (`http(s)://host[:port]`). The tray's credential form does not offer that field yet; it is a
  follow-up, and until then the list is written by whoever can already change the configuration.
- **Bodies are bounded.** Each exchange has a 120-second deadline that covers the body, WebDAV
  documents are parsed through a reader capped at 32 Mi characters with DTDs prohibited, and a file
  body is capped at 32 GiB. Downloads go beside the store, not to the system temp directory.

### The store

The store holds cached repository content and every local write. It is treated as an untrusted
input: a record's blob name must be the shape this database writes (32 hex digits and `.blob`), so
a tampered catalog cannot make the service open or delete a file outside the store, and a
compressed object is decoded no further than the size its record declares.

The store directory is restricted when an instance mounts: inheritance is broken and the account
that runs the service, SYSTEM and administrators keep full control. The installer does the same for
the data directory (`%PROGRAMDATA%\PolyMount`) and gives the account that installed it — the one
that drives the control plane — access to the configuration, while `stores` stays the service's
alone.

## The MCP server

`PolyMount.Mcp.exe` is an MCP front-end to the same named pipes the tray uses. It listens on
nothing and authenticates nobody: it inherits the trust boundary the pipes already have, so an
agent can do exactly what the account it runs from could do with the tray, and no more. `--read-only`
narrows what the process offers; it is not a boundary against a local process. Two deliberate
restrictions apply to what it offers: stored passwords are redacted from `get_config` and refused as
tool arguments, and `authenticate` has no tool at all — a password typed into a conversation is a
password in a transcript.

`POLYMOUNT_PIPE` is checked to be one of this product's pipe names (`PolyMount.Control` or
`PolyMount.Control.<instance>`); anything else is refused at startup rather than dialled.

## Not covered, and why

- **Per-file ACLs on the mounted volume.** The mount is created with a null security descriptor,
  which WinFsp treats as "no access check", and the effective gate is the mount point's directory
  ACL. Every local account can read and write whatever the mount exposes. Closing this means
  passing an explicit descriptor at mount and returning one from `GetSecurity`, which changes what
  every application on the machine may do with the drive — including the engine builds this product
  exists for. **The product is single-user by design**: one installation is one person's drive.
  Machines shared between accounts should not share a PolyMount installation.
- **Delete and rename through a link in the middle of a path.** Opens are checked against the
  handle's final path; delete and rename check the path itself. An attacker who can write the source
  tree could in principle redirect a deletion. The precondition is write access to the source tree,
  which is the same access that lets them plant the link in the first place.
- **The client's server check is by image name.** See "Squatting and impersonation".
- **Cached content is not encrypted at rest.** The store's ACL is the protection; a store on a
  share or a volume without ACLs is readable by whoever can read that volume.
- **Plaintext `Source.Password` in `config.json`.** The field is kept for hand-edited and legacy
  configurations, redacted on the way out of the service. The credential store (DPAPI, per service
  account) is the place for a password, and the layer editors offer it.
- **CPU and network are not attributed per layer.** Layer-level accounting covers wall time, bytes
  and hit counts; CPU would need sampling around every layer call, which slows down the thing being
  measured.

## A note on credentials

The Subversion source can hold a username and password in `config.json`, under
`%PROGRAMDATA%\PolyMount`. Recording bundles deliberately exclude that file. Do not attach it to an
issue.

## Checking a machine

```powershell
# What the control plane accepts — the list, or the warning that there is none
Get-Content "$env:ProgramData\PolyMount\config.json" | ConvertFrom-Json | Select-Object -ExpandProperty ControlUsers

# Who can read the data directory and the stores
icacls "$env:ProgramData\PolyMount"
icacls "$env:ProgramData\PolyMount\stores"
```

The tests that pin the behaviour described here are in
`tests/PolyMount.Core.Tests/SecurityHardeningTests.cs`, `tests/PolyMount.Core.Tests/ControlPipeTests.cs`
and the SVN plugin's `certificate-trust-mapping` check, which runs through
`tests/PolyMount.Plugin.Tests`.
