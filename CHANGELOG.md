# Changelog

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- A path under a mount point nested inside another was routed to the outer instance: the directory
  took the first mount point that matched instead of the longest. The Explorer overlay already used
  the longest match; the directory now does too.
- The setup now names the bundled source plugin in the configuration it writes. An empty plugin
  list means the host loads no plugins, so a fresh installation had no SVN source to choose from
  at all — the plugin was installed and inert.
- The Explorer context menu is no longer added twice on a machine where the setup registered it:
  the tray only looked under `HKCU`, read the setup's machine-wide entry as "not installed", and
  wrote a second copy beside it.

### Added

- **MCP server** (`PolyMount.Mcp.exe`): the control plane as a stdio MCP server, so an agent can
  list instances, probe paths, read metrics, mount and unmount, and manage recordings. The
  operation surface is defined once in `ControlOperations` — 22 operations with JSON schemas and
  read/write/destructive annotations — and is meant to be shared by later front-ends;
  `--read-only` offers the 8 read operations alone. Stored passwords are redacted from
  `get_config` and refused as tool arguments; `authenticate` is not exposed. See `docs/mcp.md`.
- The wizard lets the operator choose which bundled plugins to enable, and reconciles the
  configuration to that choice. Other plugin entries are left alone.
- One application icon, shared by the tray, the service and the setup, instead of none.
- The setup offers to restart Explorer when the source-colour dots were requested, and asks before
  deleting configuration and cached data on uninstall.
- The SVN source reads repositories over HTTP/WebDAV in-process; the `svn.exe` dependency is gone.
  Cached Subversion credentials are read directly (DPAPI), and `svn://` repositories are no longer
  supported.
- When no credential is available, the dashboard asks the reader to sign in once, checks the account
  against the server and keeps it (DPAPI, for the service account). The prompt is generic — a Git or
  SSH plugin can reuse the store, the prompt and the resolution order.
- Third-party notices, including the Qt Charts licensing consequence.
- CI: build, unit tests, plugin tests, three self-contained smoke tests (one instance worker, the
  object mount, the MCP server over stdio), and an installer artifact that the install test
  exercises.

## [0.2.0] - 2026-10-13

First packaged release: layered virtual mounts, SVN and directory sources, per-instance processes,
tray UI with charts, performance instrumentation and recording bundles.
