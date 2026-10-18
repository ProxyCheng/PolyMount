# Changelog

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- The setup now names the bundled source plugin in the configuration it writes. An empty plugin
  list means the host loads no plugins, so a fresh installation had no SVN source to choose from
  at all — the plugin was installed and inert.
- The Explorer context menu is no longer added twice on a machine where the setup registered it:
  the tray only looked under `HKCU`, read the setup's machine-wide entry as "not installed", and
  wrote a second copy beside it.

### Added

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
- CI: build, unit tests, plugin tests, two self-contained mount smoke tests, and an installer
  artifact.

## [0.2.0] - 2026-10-13

First packaged release: layered virtual mounts, SVN and directory sources, per-instance processes,
tray UI with charts, performance instrumentation and recording bundles.
