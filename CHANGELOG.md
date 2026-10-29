# Changelog

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [0.10.0] - 2026-10-29

A plugin's own page unfolds inside the window, a mount says how busy it is, and a credential says
what kind of secret it is.

### Added

- **A plugin can offer a management page, and the row it belongs to unfolds.** A plugin that
  changes the machine — the transaction shim is the first — could put a program under itself and
  take it out again, but only through a control-plane tool or a registry key: the product knew how
  to do it and offered nowhere to see it, which is how a Visual Studio image stayed instrumented
  for a day before anybody noticed. `RegisterPanel` is the plugin contract's ninth extension point
  and the kind this product already uses elsewhere — the plugin hands back a view (a sentence,
  named facts, a list with per-row actions, buttons for the page; each button is a whole
  `ControlRequest`, and one that needs a file or a folder has the window ask) and the window draws
  it without reading inside. Two commands, split along the line the product already draws:
  `pluginPanels` lists what a process loaded, answered by the supervisor, and `pluginPanel` fills
  one in, answered by an instance's worker — a render may read its mount. A render that throws
  becomes the page's own error sentence, and disabling a plugin takes its page with it. The page
  lives inside the plugin manager by default, under the plugin's own row, which unfolds into
  everything about that plugin: what it calls itself, the file and the contract it was built
  against, what it adds, and the page it offers. A plugin whose page is opened daily can still ask
  for a destination of its own (`OwnPage`), and nothing ships one. Rows are rebuilt on a
  fingerprint rather than on every poll, because a rebuild would fold an unfolded row and throw
  away the page inside it. Contract 3.1, additive.

- **The instance list shows how busy each mount is.** The workspace could say which instances
  exist, which are mounted and how many layers each has — all configuration facts — and nothing
  about whether anybody was using them. The third column, which held the layer count, is now a
  sparkline: the requests each instance answered in the last minute, one point a second, the newest
  reading beside it. What a point is measured over is a ten-second window asked for once a second
  rather than a running total, so it moves every second and reaches its full shape after ten. A
  click selects the row, a double click opens that instance's performance page, and the counters
  are asked for only while the window is on screen — they fill only while somebody is watching, and
  a closed window is exactly the reason to let them go.

- **A credential row says what kind of secret it is.** The list named the kind in words, and for an
  account with a password those words are the account's own name, so a certificate, a cached
  Subversion password and a single sign-on token all read as the same kind of thing until one was
  opened. Every row now carries a mark in the tray's own line icons: a key for an account with a
  password, a shield with a check for a certificate, and for a kind a plugin describes the glyph
  that kind names — the credential that reads TortoiseSVN's own cache wears a tortoise. A record
  this service cannot read keeps its shape and takes the warning ink. The field in a layer's
  settings that names an account wears the same mark as the row it was chosen from, because
  choosing an account and reading the row back is one gesture. Which mark a kind wears is the
  kind's own answer (`CredentialKindDescriptor.Icon`, contract 3.2 — additive, so a plugin built
  against an earlier contract still loads and wears the generic card): a credential tied to another
  program can wear that program's glyph instead of a picture of "some account".

- **An automatic notice is carried out on sight, sign-in included.** A plugin whose credential expired asks to be put in front of a sign-in page by reporting an automatic notice; the tray sends its action at once — once per time the condition stands, forgotten when the notice stops being reported — and if the answer carries a `BrowserLogin` it opens the page in a WebView2 window, reads the named cookies out of that view's own store, and writes them into the credential the plugin named. It never learns what the values mean.


- **The accounts are a section of the workbench.** They were a dialog reached from a menu, which cost the reader the instance they were looking at; they now sit in the sidebar beside the mounts, the plugins and the about page, with a key of their own, and read the store again each time the section is opened — a sign-in can have added one while it was hidden.
- **A plugin can ask for a sign-in page.** The service has no window, so a credential that comes from signing in to a web page — an internal single sign-on, a cloud console — needs the tray to show the page. Two contract additions make that a plugin's decision and nobody else's: a notice may be marked `Automatic` (carry its action out on sight; the plugin is asking to be put in front of a page, not asking the reader to agree to something), and a command's answer may carry a `BrowserLogin` — the address, the credential record to fill, and which cookie fills which field. The front end shows the page, reads those cookies out of the view's own store (a token cookie is usually `HttpOnly`, which no script inside the page can see) and writes them into the record; it never learns what they mean. Contract 2.2, additive.
- **A sign-in window, for the one thing a service cannot do itself.** The service runs in session 0
  where no window can be shown, so a credential obtained by signing in to a web page — an internal
  single sign-on, the way CloudDisk and Sunshine do it — has to be obtained in the tray and handed
  back. `LoginWindow` is that window and nothing more: it loads the page it is given in a
  WebView2-backed widget, re-reads that view's cookie store while it is open, and closes once every
  cookie the caller named is there. It reads cookies rather than the page, because a token cookie
  is usually `HttpOnly` — the one thing script inside the page cannot see. The window is generic:
  which page and which cookie names come from whoever needs the login, and nothing in it knows
  what the values mean. It uses a WebView2 profile of this product's own
  (`%LOCALAPPDATA%\PolyMount\webview`), and says so plainly when the WebView2 runtime is missing.
  Not yet wired to a plugin — the contract for asking for a login is the next step.

### Changed

- **The icon set was redrawn, and it can be looked at.** An icon set is decided by looking at
  it, so `--render` now writes `icons.png` beside the chart cards: every glyph at the seven sizes
  the tray asks for, on one sheet. The tray's own checks also count the ink of every name, because
  a glyph is a handful of coordinates and one typed outside the box draws nothing at all — which
  nobody reports, since all a reader sees is that a mark is missing. What the sheet found, and what
  changed: the pencil cut down to a pencil (three hairline strokes beside it were noise at the
  sixteen pixels a layer row gives), the disk cache drawn as a cylinder instead of a square with a
  magnifier hanging off a corner, the memory chip with two rows of legs instead of a fringe on all
  four sides, a key whose ring and two unequal teeth still read at sixteen pixels, a floppy whose
  three parts stay apart, and a cog where a sun used to be. The Explorer badges keep their own
  filled shapes: a sixteen-pixel overlay has no room for a 1.7-unit stroke, so they are drawn a
  second time in `scripts\Build-Shell.ps1`, and the two stay the same *mark* rather than the same
  drawing.

- **The Qt binding moves to QtSharp 0.22.0** (from 0.20.1), which adds `QtSharp.WebView` — a
  `QWidget` that hosts a WebView2 view, with the view's `CoreWebView2` exposed (cookies, events,
  navigation). It is not Qt WebEngine: nothing of Qt's browser stack is shipped, and the runtime it
  needs is the one Windows already has. No API the product used changed in the move.

- **A plugin is one file.** The build published a plugin as four files — the assembly, its
  `plugin.json`, a `.plugin.api` the setup read for the contract version, and a `.deps.json` — and
  handing a private plugin to somebody meant handing over the set and hoping none of it was left
  behind. The manifest is now embedded in the assembly as a resource (`PolyMount.Plugin.Manifest`,
  read by the host out of the file's metadata, without loading it), the contract version is
  stamped into the assembly's version resource as `PolyMount plugin; contract=<version>` — the
  description Explorer shows in a file's properties — and the dependency file is not generated.
  What `dotnet publish` produces is a single DLL that is copied into `plugins\`. `plugin.json`
  stays in the source tree as what its author edits; a plugin published with the manifest beside
  it is still read that way. The setup recognises a plugin file, and the contract it was built
  against, from that version resource — it cannot load an assembly, and now does not need to.
  One thing a single file cannot carry for the setup's page is the plugin's display name: the
  wizard lists an extra plugin by file name where a sidecar manifest let it show the plugin's own.

### Added

- **An incompatible plugin is listed and named, and one page decides what to do about it.** A
  plugin built against another major of the plugin contract cannot run, and neither place said so:
  the setup's plugin page listed it as an ordinary installed plugin, and the tray's page said
  nothing until the service had tried and failed — and for a disabled plugin nothing at all,
  because a disabled plugin is never loaded and so was never asked. The contract version is now
  read without loading the assembly (out of the DLL's metadata in the host, and out of a
  one-line `.plugin.api` beside it for the setup, whose wizard runs Windows PowerShell and cannot
  load a .NET 10 assembly at all). **The two pages now answer different questions.** The setup's
  page decides whether the file is kept: the row keeps its box, checked by default and clearable,
  and only gains a note saying which contract it was built against. The tray's page decides
  whether it runs: the row says **Incompatible**, its text is greyed, its reason is written on the
  row and in the tooltip, and its switch is disabled in both directions — removing the entry is
  what clears a configuration the host would refuse to start with. The same reading is used when
  the tray's section is offline, so a fresh install whose service has not started yet still shows
  which plugins this build will refuse.

- **A credential says how it authenticates as a capability, not as a password on the base class.**
  `Credential` carried `Username` and `Password`, so every kind — a certificate, a token a plugin
  adds — looked like a password with extra fields, and every consumer asked "which class is this?"
  before it could use one: the SVN source tested for a certificate and otherwise fell back to the
  pair, and the remote-folder source read the two fields straight off whatever it was given, which
  turns a token credential into an empty account and password. A credential now declares what it
  can do by implementing `IPasswordAuthentication`, `ICertificateAuthentication` or
  `IBearerAuthentication`, and a source asks for the capability it needs. A credential that offers
  neither of the two SVN or SMB can present is refused **while the instance is built**, by name,
  instead of being sent as an anonymous request nobody can explain. `Username` stays on the base:
  it is not a secret and not a mechanism, it is who the record is for. Breaking for plugin authors
  — the plugin contract is now **2.0**, so a 1.x plugin is refused with that reason rather than
  failing on a missing member; rebuild private plugins against `PolyMount.Plugin.Abstractions`
  2.x.

- **A plugin declares which processes run it.** `plugin.json` takes `"roles": ["backend"]`
  (servers — layers, sources, commands) and `["frontend"]` (front-ends — control operations);
  absent means both, which is what every existing plugin means. A mount's worker now loads only
  the server half and the MCP server only the tool half, so neither carries an assembly it can
  never call into; the service still loads everything, because the plugin list is answered from
  its report. A role the host does not know is refused with the manifest rather than ignored.
  `poly verify` fails when the declaration contradicts what `Register` registers.

- **The control surface gained what a caller was missing and lost what it did not need.** Two
  reads that had no tool: `list_credentials` reports the stored accounts — name, user name, kind,
  the origins each may be presented to, and whether the service can still read the secret — which
  is the first question when a mount fails on an account; `list_notices` reports what a plugin
  wants a person told about one mount, which is usually a program that cannot save on it and the
  tool that would fix it. The tray showed both and an agent could see neither. `list_instances`
  now carries the service's `securityWarning` too, so the first call an agent makes already says
  whether this control plane admits callers it cannot identify. `reload_config` is gone: the file
  watcher already reloads an edit made outside these tools, and `save_config` reloads what it
  writes, so the tool only offered a way to re-read what nothing had changed. The four ways of
  discarding cached or local data now say in their descriptions which one a caller wants — whole
  layer, whole overlay, one path, or a path through several layers.

- **An agent can ask which build is running.** The About page answers that for the tray, but the
  service is a different process, installed and updated on its own schedule — so an agent had no
  way to tell an installed 0.9.4 service from the development host it had just started. One
  read-only operation, `get_version`, now answers for the process that answers it: version and
  commit, installed or development host, the program, configuration and plugin folders, the .NET
  and WinFsp versions, the system, the account the service runs under, and when that process
  started. The tray's About page and the service read the same fields from the same reader
  (`HostEnvironment`), so the two answers cannot drift apart in wording. The contract went to
  1.1 — additive, so a plugin built against 1.0 still loads — and its public surface baseline was
  rewritten with it.

- **The version is on screen, and a page behind it.** A build knows what it is and never said so:
  answering "which version are you running" took a look at a file's properties sheet. The bottom
  of the sidebar now carries the release, with the whole informational version — commit included —
  in its tooltip, and beside the mount overview and the plugins there is a third destination,
  **About**: the commit this build came from, whether it was put here by the setup or is a
  development tree, the .NET, Qt and WinFsp versions, the service's own version and program, where
  the configuration and the program live, and the licence line — GPL v3 for PolyMount, LGPL v3 for
  the bundled Qt, GPL v3 or commercial for the Qt Charts the charts are drawn with — with the
  third-party notices and the project's pages one click away. **Copy diagnostics** puts the same
  facts on the clipboard as `name: value` lines in English, which is the form a report wants. The
  service and the tray are installed and updated separately, so the two versions are listed side by
  side rather than assumed equal. Paths too long for the window are cut in the middle and kept
  whole in the tooltip; `--ui-smoke --checks dashboard` now asserts that the page opens, that it
  says the version this build was stamped with, and that the sidebar line agrees.

- **A plugin can add a kind of credential.** The product ships two — an account with a password,
  and a PKCS#12 certificate for mutual TLS — and both were written into the store, the payload,
  the tray's form and the picker separately, so a source that authenticates some other way (an
  internal single sign-on token, a cloud identity) could not be added without changing all four
  together. A plugin now registers a kind once (`RegisterCredentialKind`): the fields its form
  shows, the field that names the account a reader recognises the record by, and the code that
  turns the stored values into the credential its source presents. The values live in the same
  DPAPI-protected store, are bound to the same origins, are chosen on a layer the same way, and
  the tray draws the fields from the descriptor — a kind added after the tray shipped still gets a
  form that looks native. A layer names the kinds it accepts in its descriptor's `CredentialKinds`,
  as the built-in two already did. Disabling the plugin leaves its records intact and marked
  unusable rather than deleting them; a password record keeps the empty kind on disk that earlier
  files were written with, so nothing is migrated.

- `poly` (`PolyMount.Plugin.Cli`): `new`, `verify`, `dev` and `pack`. `verify` calls the plugin's
  own `Register` against a recording context and reports the manifest, the contract version and
  what was registered.
- A development host: `PolyMount.Service.exe --dev --plugin <path>` runs a second supervisor on its
  own pipe (`PolyMount.Dev.*`), store and logs, loading the plugin where it was built. The
  containment rule (a plugin must live inside the installation) is lifted only for that host, and
  only that host may not use the installed service's pipe. `poly dev` starts it.
- The contract and the SDK ship as NuGet packages with each release, and
  `scripts/Fetch-PluginSdk.ps1` fills the repository's local feed from them.

### Changed

- **Plugins are compiled against a contract, not against the product.** The layer and source
  contracts, the configuration and descriptor models, the plugin entry point and the control plane
  are now one package, `PolyMount.Plugin.Abstractions` (namespace `PolyMount.Plugins`), and
  `PolyMount.Core` references it instead of defining it. Engine types that used to appear in plugin
  signatures have been replaced by three interfaces a plugin can be written against: `ILowerChain`
  replaces `VirtualFileSystem`, `IPluginInstance` replaces `MountInstance`, and
  `IRequestTracing`/`IRequestScope` replace `RequestDiagnostics`. **This is a break for any plugin
  built before it** — including private ones — which must be rebuilt against the contract.

- **Every plugin declares itself in a manifest.** `<assembly>.plugin.json` sits beside the plugin
  and is read before anything is loaded, so a disabled plugin is named in the list, and a manifest
  that cannot be used is a refusal. It carries the identifier, the localized name and description,
  the icon and the permissions the plugin declares (a declaration, not a sandbox). A plugin without
  one still loads, as a legacy plugin.

- **The contract has a version, and it is checked.** The build stamps
  `[assembly: PluginApi("1.0")]` into every plugin; the host reads it before it touches a type and
  refuses another major — or a minor it has not reached — with both numbers and the package to
  rebuild against, instead of a "could not load type" from somewhere deeper. A checked-in baseline
  (`tests/PolyMount.Core.Tests/PublicApi.txt`) holds the contract's public surface.

- **A plugin installed by hand survives an update, and the setup asks about it.** Two paths could
  lose one. The update script swept every file named like a plugin that the build did not produce,
  which is why a private plugin had to avoid a name the product uses; the setup copied its own
  plugins over the top and never mentioned the rest. The update script now deletes only what an
  earlier run of itself recorded in `plugins/.installed` — a plugin removed from the tree is still
  cleaned up — and says out loud what it found that it does not own. The setup's plugin page lists
  the plugins found in an existing installation that this setup does not ship, after its own, by
  the name each one gives in its manifest, **checked**: clearing a box is what removes it, and a
  silent install removes nothing. `scripts/Test-PluginUpgrade.ps1` checks all of it against a real
  plugin (`tests/plugins/PolyMount.Plugin.Puppet`).

### Fixed

- **A plugin's version in the list is its release number.** Every assembly here shares one assembly
  identity on purpose, so the version shown was always `1.0.0.0`; it is now read from the file,
  and a release build stamps the plugins with the release number.

- **Callers are named on an installed service.** A machine set up with the installer showed a row
  per caller in the performance window's layer table as `PID <id>` instead of a program name. The
  name was being discarded along with the rest of the answer: a name is read out of a system-wide
  snapshot, which any account may ask for, while the process' start time and image path mean
  opening the process — and the account the setup creates, `LocalService`, is in no descriptor of a
  program in a user's session. One refused read dropped the whole answer, so every caller was
  nameless while nothing was actually wrong with it. The start time is now asked separately and an
  answer without it is kept; what it carried, the check that a reused pid is not answered with the
  old process' name, is already covered by the minute an answer is remembered for. The
  transaction-shim plugin stays blind on such a machine, because the image path it acts on is one
  of the reads that do need the process to be opened.

## [0.9.4] - 2026-10-29

The tray's own checks run without drawing a window, and the Qt binding moves to QtSharp 0.20.1.

### Changed

- **The Qt binding moves to QtSharp 0.20.1** (from 0.17.0). Nothing the product does changes; what
  it is built against does. Since 0.19 a package is per platform with the RID in its id
  (`QtSharp.Api.win-x64`), so a Windows build restores only the Windows payload, and
  `scripts/Fetch-QtSharp.ps1` fetches the release's platform zip and unpacks it into `packages\`
  instead of five loose packages. `QVariant` is gone from the API — item and model data cross as a
  plain `object`, so the six `SetData(role, new QVariant(...))` call sites pass the value itself.
  `QColor`, `QModelIndex`, `QSizePolicy`, `QTransform` and the rest of the trivially copyable Qt
  types are C# structs rather than native handles: the twelve `using`/`Dispose()` sites on them are
  gone, and the per-pixel colour loops on the repaint paths no longer allocate a wrapper and a
  native object per pixel. Two behaviour notes: `Data(role)` is `object?` now (a caller that stored
  a string still gets one back), and `QColor` equality is Qt's own, which compares across colour
  specs.

### Fixed

- **The self-check draws no window.** `--ui-smoke` builds real windows, shows them and measures
  them — that is the point of the checks and also why nobody wants to run them while working. The
  run now selects Qt's offscreen platform before the `QApplication` exists, writing it through the C
  runtime (the copy Qt reads) and pointing `QT_QPA_FONTDIR` at the system fonts, because the
  offscreen platform has no font database of its own and every layout check would measure synthetic
  rectangles. `set QT_QPA_PLATFORM=windows` still runs them on the desktop, and a build whose
  binding has no offscreen plugin falls back to windows and says so. This needed a QtSharp fix as
  well: its Windows package shipped `platforms/qwindows.dll` alone, so asking for offscreen found
  no plugin and Qt aborted the process instead of drawing nothing (QtSharp 0.20.1).

## [0.9.3] - 2026-10-29

A program that cannot save on a mount is mentioned before it fails, and the transaction shim can no
longer stop a program from starting.

### Added

- **A program that cannot save on a mount is mentioned before it fails.** A transactional open is
  refused by the kernel before any file-system callback runs, so it leaves no trace in a request
  log, a recording or a metric: all a person sees is an engine that will not save, and the action
  that fixes it lives in another window, applied to a directory they were never told about. A mount
  now reports which programs opened something on it lately — a small always-on table beside the
  counters that only fill while somebody is watching, because the question has to be answerable
  when no window is open — and a plugin can raise a notice about one of them: a title, a body, one
  action label and the control request that action sends. Core carries it and the tray shows it as
  a corner notification; neither learns what it is about, so another plugin can advise about
  something else without a window changing. "Don't ask again" is remembered per user, and pressing
  the action asks the plugin again at once, which is how the second sentence ("restart it") comes
  back. The transaction-shim plugin is the first to use it, with the same rule its install uses,
  and `install_txf_shim` now also takes a single executable — a running program is named by its own
  image, and its folder's other executables were never asked about.

### Changed

- The installer build takes its platform as a parameter (`-Rid` and `-Platform` in
  `Build-Installer.ps1`, one matrix entry per platform in CI). Today's output is named and built
  exactly as before; a second architecture can be published beside it without touching the script.

### Fixed

- **The transaction shim cannot keep a program from starting.** Its launcher — the Image File
  Execution Options debugger the plugin installs — killed the child when it could not instrument it,
  and reported the wrong error when it could not start one: the code it read had already been
  overwritten by the launcher's own write to its log. It also assumed its first argument was an
  image path, which is only true when the caller's command line starts with one — a program spawned
  with `lpApplicationName` and an arguments-only command line arrived as a bare argument and was
  never started at all (an engine's shader compiler, whose absence left the editor rendering
  without shaders). The launcher now tries the command line as well as the first token, starts the
  program without the shim rather than not at all, records the command line it was handed, and a
  payload that changed is replaced when the plugin is installed again instead of being left in
  place.
- **A plugin that fails to load because its host is older says so.** A plugin is compiled against a
  particular `PolyMount.Core` and the host loads its own copy, so a host that was not redeployed
  with its plugins fails with "Could not load type …" — which reads like a broken plugin. The
  deployment leaves the MCP server alone while a session holds it open, so this is the state it
  lands in. The load result now names the mismatch and what to do about it.
- **A click on a corner notification opens the notification it was about.** The queue is ordered by
  who used the mount most recently and a click took the last entry, so with two programs waiting
  the balloon for the newer one opened the older one — the one thing a click should never do. What
  the last notification was about is remembered now, and the tray menu entry opens the same one.
- **A notice's box is on top.** The click that opens one lands on the shell's notification, so
  Windows never makes this process the foreground one and the box could open behind everything the
  reader had in front of them. It is shown topmost instead, the taskbar entry flashes while it is
  up, and both halves of that failure — a click that never arrives, a box opened out of sight — are
  logged, because from a chair they look the same.
- **An unanswered notice is said once more, a minute later**, at most twice: a corner notification
  lasts ten seconds and Focus Assist can swallow it whole. After that it is the tray menu's job,
  which keeps it for as long as the condition stands.
- **A notice is announced when its scan ends**, not on the poll after it: a program that touched
  the mount waited up to a minute to be mentioned.

## [0.9.2] - 2026-10-27

An instance that cannot start stops being restarted and says why, and a credential's servers can
be edited where the account lives.

### Added

- **The servers a credential may be presented to are shown and edited where the account lives.**
  A record has carried its origins since the binding was introduced, and the first source to use it
  writes one down — but nothing displayed them and nothing could add one, so a second repository
  sharing an account was refused at start-up with advice ("choose another credential") and the one
  act that repairs it was reachable only by hand-written control calls. The credential form now has
  a `Servers` field (comma separated, empty meaning "bind on first use"), the list row counts them
  and the tooltip spells them out. Saving a credential also lets the instances that gave up on it
  try again.

### Fixed

- **An instance that cannot start stops being restarted, and the reason is written down.** A worker
  that died for the same reason three times over at least a minute was started again every thirty
  seconds for as long as the service ran, and the service log said only "exited with code 2": the
  sentence that identified the failure was in the worker's own log, which is a file nobody knows to
  open. It is on the supervisor's line now, the instance is left in an explicit failed state with
  that reason on it, and mounting it again, writing the configuration, or saving a credential is
  what clears it.
- **A failure's kind reaches the reader.** The tray read the message but never the kind, so a mount
  that needed an account and one whose credential was bound to another server both read as "error"
  with no way forward. The detail panel now puts the repairing sentence beside the reason, the list
  says "start failed" once the supervisor has given up, and `list_instances` carries `errorKind` and
  `failed` so an agent can tell "wait for it" from "this needs a change".

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
