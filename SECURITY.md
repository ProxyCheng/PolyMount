# Security policy

PolyMount runs a Windows service that mounts virtual drives, and it loads plugin assemblies named
in its configuration. Treat both as privileged: the service account can read whatever the mounted
sources expose, and a plugin assembly runs with the service's rights.

## The MCP server

`PolyMount.Mcp.exe` is an MCP front-end to the same named pipes the tray uses. It listens on
nothing and authenticates nobody: it inherits the trust boundary the pipes already have, so a local
process able to open `PolyMount.Control` gains no authority by going through it. Two deliberate
restrictions apply to what it offers: stored passwords are redacted from `get_config` and refused as
tool arguments, and `authenticate` has no tool at all — a password typed into a conversation is a
password in a transcript.

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

## A note on credentials

The Subversion source can hold a username and password in `config.json`, under
`%PROGRAMDATA%\PolyMount`. Recording bundles deliberately exclude that file. Do not attach it to an
issue.
