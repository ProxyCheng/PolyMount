# Changelog

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.9.1] - 2026-10-27

The service answers its own front-ends again, an update no longer waits for another checkout's
service, and the volume's numbers come from the driver's cache.

### Fixed

- **A control request the service cannot attribute is served rather than refused.** Who may connect
  is the pipe's descriptor, built from `ControlUsers`; the account read on each request is what puts
  a name in the log, and it cannot be read when the service runs as `LocalService` — an account with
  no grant in a process owned by the user who installed it. That case was refused, which refused
  every request from every front-end of an installed service; it is served now, with one warning per
  process. `SECURITY.md` says which two routes name a caller, and why reading the identity from the
  connection itself is not one of them.
- **A caller whose token cannot be opened is unidentified.** It used to be answered as an identity
  carrying an empty SID, which sent it down the "not one of the accounts in `ControlUsers`" branch —
  a refusal for a caller the server had just failed to read, with a reason about the wrong thing.
- **`Update.bat` stops this checkout's service.** The stop step waited on every process named
  `PolyMount.Service`, so a second checkout or a lab installation on the same machine never let it
  finish and the deployment was cancelled.

### Changed

- **The volume's size and serial are cached for a second** (`VolumeInfoTimeout`). While it was zero
  the driver asked the file system for them on every file-information query — one round trip for
  each `GetFileInformationByHandle`.

## [0.9.0] - 2026-10-27

A new instance is built from a preset instead of an empty layer stack, and walking a large tree
through a mount costs about a third less time.

### Added

- **New mount starts from a preset.** The dialog opens on a gallery of the presets the loaded
  plugins and the core offer — each card says what it connects to and which values it will want —
  and the second page asks only those: the source's address and account. The name and the mount
  point follow the address until they are typed over, everything else keeps the preset's defaults,
  and the last card opens the editor directly for a chain built by hand. Nothing is written until
  the dialog commits, and a refusal from the service is shown under the field it came from.

### Changed

- The Subversion plugin ships **one** preset. `svn-workspace` now carries the configuration a
  content-repository start-up was measured with — a 1 GiB memory cache, a 10,000-directory listing
  warm-up, a 256 MiB prediction layer running only `manifests`, and a 16 GiB durable read cache —
  rather than leaving a bare default beside a tuned variant. `game-engine-workspace` is gone; an
  existing mount is unaffected, because applying a preset writes its layers into the configuration.
- Controls show keyboard focus with the theme's accent colour instead of the native dotted
  rectangle; the editor's integer fields accept digits only and its colour field only `#RRGGBB`.
- The Qt binding moves to QtSharp 0.17.0, a build-time dependency (the setup carries the same Qt
  runtime as before).

### Fixed

- **Walking a large tree is faster.** Windows asks the file system about one name several times for
  a single operation — the security lookup, the open, and the parent directory the create resolves
  its path through — and every ask resolved the whole path again. The resolver now answers
  consecutive asks about one name from the first answer, and a write or a moved source still
  invalidates it. The cache layers also rebuilt a hash of the chain below them on every object
  read; it is computed once per change now. Measured on a 28,600-directory, 214,000-file
  Subversion-backed subtree: a warm full enumeration 20.2 s → 13.2 s, and a walk that queries
  attributes for every entry 0.57 → 0.35–0.43 ms per entry.
- A recording's timing is no longer rounded to whole milliseconds, so a request that takes less
  than one is counted as what it took rather than as zero — the totals and the per-path table now
  describe what happened.

## [0.8.1] - 2026-10-26

### Fixed

- A stored credential can be used at more than one origin. 0.8.0 bound a credential to the origin it
  was first used for and offered no way to name a second one, which refused a configuration where one
  account opens two repositories — the shape an installation with two servers of the same account
  has. `saveCredential` now carries an `Origins` list, validated as `http(s)://host[:port]`; a client
  that does not send it keeps what the record holds.
- The installer's data-directory hardening built its `icacls` rules inside an array literal, where
  `$sid + ':perm'` is split into two arguments; `icacls` answered 87 and the setup stopped after
  creating the service, leaving it installed and not started.

## [0.8.0] - 2026-10-26

A security pass over the whole tree, answering the `[Security]` issues filed against this
repository. Two of the changes are visible to an existing installation: the control plane now
carries an account list, and a TLS default that accepted any certificate is now strict.

### Security

- **The control plane names its accounts.** Every control pipe used to grant read-write to the
  whole interactive session and never asked who was calling, so any standard local user could stop
  the service, replace the configuration, list the stored accounts or point a layer at any path.
  `ControlUsers` (accounts by SID) now builds the pipe's descriptor, and the caller is identified
  from the connection and checked again on every request. The installer, `Install-Service.ps1` and
  `Update-PolyMount.ps1` seed it from the account that ran them; a configuration with no list keeps
  the old behaviour rather than locking an upgrade out, and says so in the log and in every status
  answer.
- A client checks who served the connection before sending it anything: a pipe name is a
  first-come claim, and the tray sends credentials over it.
- The pipe server bounds what a stranger can cost it — request length, concurrent requests, idle
  time — and reopens after a failure instead of faulting the host.
- `getConfig` redacts stored passwords at the IPC boundary; the MCP front-end was the only surface
  that did. A `saveConfig` that still says `[redacted]` keeps the stored value.
- A configuration is validated before any plugin it names is loaded, a plugin path must resolve
  inside the installation directory, and a refused document restores the running plugin set.
- Certificate validation is strict by default: `TrustServerCertFailures` is empty, a server that
  presents no certificate is never accepted, and a plain `http://` source needs
  `AllowInsecureTransport`. A named credential is bound to the origin it was first used for, and
  the repository root the server advertises has to stay on the configured origin.
- Network bodies are bounded (120 s including the body, 32 Mi characters per document, 32 GiB per
  file) and downloads land beside the store rather than in the system temp directory.
- A local directory source canonicalises against its root, checks the handle's final path on every
  open, and neither lists nor resolves reparse points unless asked to.
- A store opens only the blob names it wrote and bounds decompression by the record's declared
  size; store directories are locked down when an instance mounts, and the installer breaks
  inheritance on the data directory.
- Paths refuse `.`, `..`, `:` and reserved device names; `POLYMOUNT_PIPE` and
  `POLYMOUNT_CRASH_TRACER` are validated; the update check opens only an https link on a trusted
  host; `Install-Service.ps1` no longer puts the service account's password on a command line.
- `SECURITY.md` states the model, what is enforced where, and what is not covered. The mounted
  volume itself still exposes no per-file ACLs, which is a deliberate limit: the product is
  single-user by design.

### Added

- A program that needs Transactional NTFS can run on a mount. WinFsp's volume does not advertise
  transactions, so an open through `CreateFileTransacted` is refused with 6832 before any file
  system sees it. The new transaction-shim plugin starts the executables an administrator names
  through a native launcher that replaces those calls inside the process, before they reach the
  kernel, and records each one it intercepted. Enabling the plugin installs nothing: the directory
  is named with `install_txf_shim`, and processes already running keep their behaviour until
  restarted. A shimmed open succeeds and its writes take effect; a rollback no longer undoes them.
  The setup offers the plugin as a third bundled choice, and an uninstall removes the registry
  values it wrote.
- A folder on another Windows machine is a source: the `smb` plugin maps a UNC path
  (`\\host\share\folder`, or an administrative share such as `\\host\d$`) and installs nothing on
  the far side. It presents the configured credential in the worker's session, keys the cache on
  the mapped folder's write time, and answers "not found" only for a real not-found — a dropped
  connection is an error, not a deletion. The `smb-workspace` preset and `get_smb_source`
  operation ship with it.

### Changed

- The setup always shows the destination page, an upgrade included (Inno hides it by default when
  it recognizes an earlier installation), so the installation directory is a choice.
- An upgrade replaces the Explorer extension only when what it was built from changed, tracked by a
  build fingerprint the package carries (the DLL's own bytes cannot say this: the PE timestamp and
  the CI's timestamped signature differ on every build). An unchanged extension is not copied at
  all — a loaded image cannot be overwritten, identical or not — no restart marker is written, and
  one an earlier install left behind (its tray never ran) is cleared: the common upgrade no longer
  touches Explorer. When the extension did change, the tray asks whether to restart Explorer now
  instead of doing it silently.
- The performance page measures writes the way it measures reads: requests, bytes and latency,
  in two columns with one row per question, and the recording bundle carries the same six charts.
  Write latency comes from where a layer applies a create or a commit; bytes written into a
  staging handle are counted as bytes, not as writes of their own.
- The layers' memory holdings are drawn inside the process's own working set, with the part no
  layer claims (the runtime, SQLite's native buffers, the pipe, GC slack) as one grey band — so
  the card's total is what the operating system says the worker holds. The total is read by the
  worker itself, because an unelevated tray cannot open a LocalService worker's handle. Disk has
  no such total and stays the layers' own sum. The process section now reads memory | disk,
  CPU | threads, handles | I/O.
- The listing warm-up layer owns its own store. Complete directory listings used to be written
  into whichever caches were below it, where the "never evict a listing" rule took capacity the
  content cache could never reclaim, and warm-up hits were reported as somebody else's. The
  `warmup` layer now keeps a path-keyed store that never evicts a listing, is emptied when the
  source's generation moves, and answers the resolver itself — so its effect is visible in the
  performance window.
- The game-engine plugin is gone: its `manifests` strategy, `game-engine-workspace` preset and
  `list_manifest_families` operation moved into the SVN plugin, where they are useful. The
  deployment sweep removes a plugin DLL the source tree no longer publishes.

### Fixed

- A write's commit is charged to the process that wrote the file. The commit happens when the
  staging handle is flushed or cleaned up, and those callbacks cannot name the caller, so the
  write-source card listed the layer's slice but no process — every commit read as "no process
  to attribute it to". The boundary now carries the caller the handle remembered into the commit,
  and into a rename and a reparse-point write as well.
- The warm-up pass resolves through its own store, so a directory's ancestors are not fetched
  from the source again on every depth (a tree of depth *d* cost the source *d* round trips per
  listing).
- The update script pins the build tools and console to one language, and skips publishing the
  MCP server while a running copy holds it, instead of retrying ten times with a warning each.

## [0.5.1] - 2026-10-20

### Fixed

- Stopping an instance could lose the last writes through a mount. Small writes are coalesced
  into a short transaction, and the worker unmounted and exited without closing its stores, so
  anything still waiting for that transaction went with the process. A stop now disposes the
  instance, which commits what it accepted before the mount goes.
- The self-contained mount checks could start working before the mount was up: an elevated
  service creates the mount point directory and attaches the volume to it a moment later, and
  "a directory of that name exists" is true of the placeholder. They wait for the mount point
  itself now.

## [0.5.0] - 2026-10-20

### Added

- Symbolic links and junctions work inside a writable mount. A link is a reparse point, and the
  mount now keeps the bytes a client writes when it creates one, reports the attribute, and lets
  Windows resolve the target — so `mklink` succeeds where it used to answer "local volumes are
  required", and a directory link lists its target. A link in the middle of a path is resolved
  too, which is what a tool whose workspace is a tree of junctions needs: opening, launching and
  deleting through one now work, and a link that leaves the mount still goes back to Windows.
- Programs stored on the volume can be started. A file system mounted the ordinary way is not a
  volume Windows accepts behind a path: AppLocker and the UAC consent path reject it with
  `ERROR_UNRECOGNIZED_VOLUME`, so an executable on the mount could be read but not run. An
  elevated process now mounts through the Mount Manager, attaches the configured directory to that
  volume, and releases the transient drive letter; an unprivileged process keeps the previous
  behaviour.
- A repository behind mutual TLS can be given a **certificate credential** — a `.pfx`/`.p12` file
  and the password that opens it — instead of relying on the personal certificate stores, which a
  service account cannot see and a user's non-exportable key cannot fill. The editor grows a
  credential type picker, a certificate file picker and a password box, and the layer names the
  credential it uses. The password is a secret like the repository password: redacted by the
  configuration API, set from the tray.
- The service can run under an account of its own. Credentials are protected so that the account
  which reads them is the one that can open them, with existing entries migrated as they are read,
  and the service offers the machine's personal certificate store as well as the account's.

### Changed

- Small writes through the mount are coalesced: a write publishes to the resident index at once and
  a short window (100 ms, or 256 changes) runs one transaction for everything a burst produced,
  with the blob fsyncs on the commit thread. Writing a 4 KiB file through an overlay over a local
  directory measured 33.5 ms/file, then 6.9 ms/file. A reader in the same process still sees its
  own writes immediately; only durability moves, and an orderly unmount, a clear, an eviction and a
  management action all commit what is pending first.
- A durable cache stages a large object by its own size rather than a fixed 256 MiB: the budget
  scales with the cache (a quarter of its capacity, floor 256 MiB, cap 4 GiB), so a package larger
  than the old floor is no longer downloaded again on every start. The game-engine plugin's
  manifest strategy can also stay a few names ahead of a reader that walks the repository in
  listing order.

### Fixed

- A path the repository does not have is a miss, not an I/O error. The transport reported a 404 by
  throwing and the layer let it escape, so the mount answered a probe for an absent file with
  `STATUS_IO_DEVICE_ERROR` — a program whose DLL lives only in `System32` then refused to start.
- A path spelled in a different case finds the repository's spelling. The mount advertises
  case-insensitive names and Subversion's URLs are case-sensitive, so a lower-case spelling that
  differed from the server's answered 404 even though the parent listing named the child.
- A child's header is answered from its parent's listing instead of a round trip per name, and an
  SVN listing is parsed without comparing the collection against every child: opening a directory
  of 3,310 entries no longer costs 3,310 requests, and a start-up that used to make thousands of
  them now reads the listings it already has. Paths in a listing are compared as decoded paths, so
  a name such as `直接进入游戏.bat` resolves regardless of how the server escaped it.
- Clearing an instance's cache on a very large store failed with "too many SQL variables"; the
  blob names are read in batches and the table is emptied with one statement.
- An elevated control pipe no longer admits every interactive user: the read/write rule is added
  only when the server is not elevated, where the tray, an agent's MCP server and the Explorer
  overlay need it.
- Starting the service from the tray asks the SCM first and never launches a second, unprivileged
  supervisor beside an installed service.
- The Explorer overlay no longer crashes the process that loads it when that process carries an
  older Visual C++ runtime: it uses primitives whose layout is not shared with the C++ standard
  library, and the build refuses to produce a DLL that imports the coupled entry points.
- An exception raised inside a Qt callback is written to the tray's log instead of leaving the
  control that raised it looking like a dead button.
- The credential form builds its rows after the label they report into, so adding or editing a
  credential no longer does nothing.

## [0.4.10] - 2026-10-18

### Changed

- A plugin implements `IPolyMountPlugin` and registers through it. `ILayerPlugin` remains as an
  obsolete alias that derives from the new interface, so a plugin assembly built against an
  earlier release still loads; the name changed because a plugin contributes layers, sources,
  presets, prediction strategies, commands and control operations, any of which may be none of
  the layer ones.

## [0.4.9] - 2026-10-18

### Fixed

- A directory mount point could be unusable in Explorer and in the tray right after installing:
  Inno Setup 6.3+ runs with RedirectionGuard, and every process Setup starts inherits that
  mitigation — measured, whatever the documentation says. A junction created by a
  non-administrator (the service account) counts as untrusted to an enforcing process, so the
  tray Setup launched, and the Explorer Setup restarted, answered "The path cannot be traversed
  because it contains an untrusted mount point" until the user signed out. The tray is now
  started through Explorer — a process of its own, not a child of Setup — and Explorer is
  restarted only when it actually holds the old shell-extension DLL: Setup renames the loaded
  file so the new one can be written, and the tray, which is not mitigated, restarts Explorer so
  the new overlay loads. Otherwise nothing restarts the desktop, and the finished page says how
  to make the source colours appear.

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
