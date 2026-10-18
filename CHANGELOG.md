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
- The performance page's layer cards ranked layers by how many requests they had served, so a
  band moved whenever a busier layer overtook it and the picture disagreed with the mount's own
  layer order. The trend cards, the layer table and the access circle now all read the layering
  the mount reports: the layer on top of the stack is the one on top of the chart.
- A performance chart that was scrolled out of view was left empty when it came back until the
  next sample arrived — up to a whole refresh interval. A card that returns to the viewport, or
  that a folded section opens over, is now drawn from the readings the page already holds.

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
- Credentials are one named list (name, id, user name, password, stored DPAPI-protected for the
  service account), and a layer names the credential it uses: the SVN layer has a picker, with a
  Manage button that opens the same list. Accounts an earlier version remembered by address are
  migrated into the list when the file is first read.
- Third-party notices, including the Qt Charts licensing consequence.
- CI: build, unit tests, plugin tests, three self-contained smoke tests (one instance worker, the
  object mount, the MCP server over stdio), and an installer artifact that the install test
  exercises.

## [0.2.0] - 2026-10-13

First packaged release: layered virtual mounts, SVN and directory sources, per-instance processes,
tray UI with charts, performance instrumentation and recording bundles.
