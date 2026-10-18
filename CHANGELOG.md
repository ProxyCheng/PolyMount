# Changelog

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.4.8] - 2026-10-18

### Fixed

- The workspace's buttons stayed live while the state they had just changed was still on its way
  back, so "disable this instance" could be pressed again and again, each press opening its own
  confirmation. A command's answer says only that it was accepted; the window now asks for the
  status at once and keeps every action flat — and its waiting line shown — until that status
  arrives and the panel shows what actually happened.
- Setup flashed a console window several times on its way through: every helper call was made
  visible so the first install's service-account prompt could be read. Only that prompt shows a
  window now; stopping the old service, checking the files and the Explorer restart run hidden.
- An upgrade could stop with "Setup was unable to automatically close all applications" while
  an agent's MCP server or a probe window was still running out of the installation directory:
  the setup stopped the service and the tray only, and Setup's own close-applications step then
  gave up on the rest. Everything running from that directory is stopped before the files are
  replaced.
- An SVN repository behind mutual TLS could not be reached at all: the server asks for a client
  certificate in the handshake, the Subversion command line answered with one from the user's
  personal store on its own, and the in-process client offered none — so the handshake failed with
  a bare "handshake failure" before any credential was asked for. The HTTP transport now offers
  the personal store's client-authentication certificates and lets Schannel answer the server's
  request. A service account has a store of its own, so a certificate the signed-in user owns is
  visible only when the service runs as that user, and the error says so.
- "Refresh HEAD" against such a server failed with "needs an account": the youngest revision was
  read from the repository root's `version-name`, and demo answers 403 there. It is read from the
  baseline the version-controlled configuration points at (`!svn/bln/<rev>`), which is where the
  youngest revision actually lives.
- A layer action that failed — refresh HEAD, clear cache — reported only in the status line at the
  bottom of a scrolling editor, so a reader who clicked a button beside a field near the top saw
  nothing happen. A failed action now also says so in a dialog. A save that failed reported "load
  failed" because the branch read the flag after clearing it, and clicking an action that could
  not run (unsaved instance, disabled layer) now says why instead of returning silently.
- The dashboard's "open folder" started the path whenever the instance said it was mounted; a
  worker that had not got its mount up left nothing there and the answer was the raw process
  error. It now says the instance is not mounted.
- The instance editor asked for nearly six layers' worth of height in its list before showing one,
  and for more height around the page than a laptop window has — its Save button sat below the
  fold. The list shows four layers and scrolls for the rest, its rows are shorter, and the page's
  furniture is tighter: the whole editor fits in a 700-pixel-tall window, and a check keeps it so.
- The setup dropped "start the tray at sign-in" and "show source-colour dots" on every upgrade:
  Inno's `checkedonce` flag starts a task unchecked when it finds an earlier copy of the program,
  while the registry entries the earlier install wrote stayed in place, so the checkbox
  contradicted what the machine does. Both start checked on every install now.
- The setup copied the game-engine plugin into `Service\plugins` but never named it in the
  configuration, so its source types and its `game-engine-workspace` preset never appeared —
  the host only loads the plugins a configuration lists. Both bundled plugins are now named by
  default, and the plugin choice has a wizard page of its own instead of sharing the tasks page
  with start-up and Explorer options.
- The installed tray could not talk to its own service. A named pipe created without a security
  descriptor grants the signed-in user read access only, while a control client opens it for both
  directions, so every call failed with "Access to the path is denied." against a service that was
  running and answering — the default setup runs the service as LocalService and the tray as the
  user. Control pipes now carry a descriptor that names the account that listens (full control,
  including creating the next pipe instance) and the interactive session that connects.
- The dashboard reported a supervisor it could not reach as connected: the merged status answers
  "unreachable" as a response rather than throwing, so the new-instance button stayed enabled while
  every call failed. It now shows the service as offline, and the editor's add-layer and preset
  buttons say the layer list is unavailable instead of doing nothing when the load came back empty.
- Every page was laid out as tall as its wrapped labels would like rather than as tall as the
  window — a layout reports a height for a given width, and the scroll area around the page took
  it — so a window shorter than that scrolled the whole page past blank space of the page's own
  making. The workspace had it worst: the sidebar's own column was part of the minimum, and the
  instance list and the detail panel, the things a reader opens the window for, were pushed out of
  sight behind it. The pages now take the window's height and scroll only for what they cannot do
  without; the workspace's two panels take every pixel that is left, and its sidebar scrolls
  inside itself.
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

- The installer build signs what it produces when a code-signing certificate is supplied: the
  service, tray, MCP server, plugins and shell extension before Inno packs them, then the setup
  itself, with SHA-256 and an RFC 3161 timestamp. An OV certificate is given as a PFX (imported
  for the length of the build so its password never reaches a command line); an EV certificate is
  named by thumbprint, which is how its token installs it. The CI package job signs the same way
  when `SIGNING_PFX_B64` and `SIGNING_PFX_PASSWORD` are repository secrets, and ships the
  certificate beside the setup for the self-signed case. Without a certificate the build is
  unchanged and says nothing about signing.
- A window waiting for the service — mounting, saving an instance, starting or stopping the
  service — now shows a thin line travelling beneath its status message, and stops it when the
  answer arrives. The buttons already went flat and the status line already said what was
  happening; neither of those moves, so a long save and a hung window looked exactly the same.
  Automatic refreshes do not show it, since nobody asked for them.
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

### Changed

- The instance editor no longer carries its own "enable this instance" checkbox. Whether an
  instance runs is the workspace detail panel's one button over its three states — switched off in
  the configuration, enabled but not running, mounted — and the checkbox was a third way to set the
  same field, and the only one that needed a whole configuration save to do it.

## [0.2.0] - 2026-10-13

First packaged release: layered virtual mounts, SVN and directory sources, per-instance processes,
tray UI with charts, performance instrumentation and recording bundles.
