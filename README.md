# PolyMount

**Mount a remote repository as a drive letter — on demand, without a full checkout.**

[![CI](https://github.com/ProxyCheng/PolyMount/actions/workflows/ci.yml/badge.svg)](https://github.com/ProxyCheng/PolyMount/actions/workflows/ci.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

Large repositories are painful to browse: a full checkout is slow, it eats the disk, and most of
it is never opened. PolyMount presents a repository as a normal Windows drive instead — Explorer
opens it, your editor opens it, `grep` runs over it — and every file is fetched the first time
something reads it. What you have read is cached; what you write stays local, layered over the
remote view without touching it.

```
D:\                                  mounted from a source of your choosing
  └── project\                       listings come from the repository
        ├── src\      (fetched on first listing, cached after)
        └── build.log (read-through cached)
```

## Why it is built this way

A drive is not one filesystem. PolyMount is a **stack of layers**, and each path is resolved from
the top down:

```
memory cache
listing warm-up   ┐  layers above the write layer. Reads are answered by the
prediction        │  caches in this group; warm-up owns this instance's
disk cache        ┘  directory listings and fills them in the background;
                     a write invalidates the paths it touched.
   ↓
overlay  (writable)  ── local changes, copy-on-write
   ↓
source   (plugin: SVN, remote folder, local directory, …)  ── the thing that actually has the bytes
```

In the bundled presets the layers above the overlay are optional and read-only. In the SVN preset
the overlay sits directly above the source, and the durable cache sits directly above the overlay
with `Writable = false`, so a write never stops in it. The local-directory preset has no durable
cache.

Each layer has one job. The memory cache answers from RAM, the warm-up layer keeps the directory
namespace on disk, the prediction layer holds what a reader is about to ask for, the disk cache
keeps file content, and only the overlay takes writes.

- **Reads** walk down until a layer has the path. A hit below a cache layer is copied back up
  into it, so the next read is local. A cache that cannot follow writes is not filled when a
  writable layer sits between it and the layer that answered. The memory and disk caches can
  follow writes, so they are filled and then told which paths changed.
- **Listings** come from whichever layer holds the directory, as a complete manifest — the layers
  are never unioned. A deleted file is simply absent from its parent's manifest, so it stays gone
  without leaving a tombstone behind. Resolving a child by name reads that same manifest: the
  child's header is already in it, so the source is not asked again, and the name matches
  regardless of case the way NTFS does. Only layers above the manifest's provider can override
  the listed header. One layer owns the listings: the warm-up layer keeps them in a store of its
  own and never evicts one — a directory is the ancestor chain of everything beneath it, so a
  capacity-ordered eviction would throw away exactly the entries whose loss costs the most — and
  the content caches below it are not handed a second copy. A source that moves (a new revision)
  answers by fetching, and the store of the old version is emptied rather than carried forward.
- **Writes** go to the first writable layer, doing a *copy-up* from below first if the file only
  exists remotely. After a successful write, the caches above it drop the paths that changed.
- **Links** created inside the mount — symbolic links and junctions — are kept by the writable
  layer as the reparse data Windows wrote, and resolved by Windows itself. A link whose target is
  inside the mount is followed by opening, launching and deleting through it, which is what a tool
  whose workspace is a tree of junctions needs.
- **The volume is a volume**: when the service is elevated it mounts through the Windows Mount
  Manager, so AppLocker and the UAC consent path accept it and a program stored on the mount can
  be started. An unprivileged process mounts the ordinary way, which reads and writes the same but
  cannot launch from the mount.
- **Generations**: a source's content generation (for SVN, the revision) is part of every cache
  key, so a new revision invalidates by itself instead of being flushed by hand.
- **Pinning**: a mount can stay on a fixed revision even as the repository moves.

Each mount instance runs as **its own worker process** under a small supervisor, so one instance
restarting, crashing, or being reconfigured cannot take another with it. A worker that dies for the
same reason three times over a minute is left down with that reason on its row — the supervisor log
says what it was, the dashboard shows it beside the instance, and mounting the instance again (or
saving a credential, or writing the configuration) tries once more. A process that dies without a
reason is a crash and is still restarted indefinitely.

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11, x64 |
| Driver | [WinFsp](https://winfsp.dev/) 2.x — the installer installs it if it is missing |
| Runtime | None to install by hand. The setup ships a self-contained .NET runtime, the Qt runtime and the Visual C++ runtime. |
| SVN source | an `http(s)` repository (served over WebDAV); no client is needed. Add a credential in the tray's credential manager and choose it on the layer. Secrets are protected with Windows DPAPI for the account that saved them, so run the service under that account (the setup offers this). A repository behind mutual TLS uses a **certificate credential** — a `.pfx`/`.p12` file and the password that opens it, kept in the credential store — which works even under a service account that cannot see the user's personal store or export the user's key. |
| Remote folder source | an SMB share on another Windows machine — `\\host\share\folder`, or an administrative share such as `\\host\d$`. **Nothing is installed on that machine**: a share the chosen account may read is the whole setup. Choose a credential on the layer; leave it unchosen to use the account the service runs as. Port 445 has to be reachable, and an administrative share needs an account that is an administrator over there. |
| Building | .NET 10 SDK, Visual Studio C++ build tools (the Explorer extension and the transaction shim), Inno Setup 6 |

## Install

Download `PolyMount-<version>-win-x64-Setup.exe` from
[Releases](https://github.com/ProxyCheng/PolyMount/releases) and run it. The wizard asks where to
install it, which bundled plugins to enable (the SVN source, the remote-folder source, and the
transaction shim), whether the tray should start at sign-in, whether Explorer should show
source badges, and optionally whether to run the service under your Windows account so it can
use the SVN sign-ins saved there.

An upgrade replaces the Explorer extension only when what it was built from changed — the package
carries a build fingerprint for that. When it did change, the tray asks whether to restart Explorer
(the extension is loaded only when Explorer starts); when it did not, nothing about Explorer is
touched.

Uninstalling asks before deleting your configuration and cached data; answering **No** keeps them
for a later installation.

## Build from source

```powershell
# QtSharp is not on nuget.org; this fetches the binding, runtime, Qt and charts packages into .\packages
powershell -ExecutionPolicy Bypass -File .\scripts\Fetch-QtSharp.ps1

dotnet build .\PolyMount.slnx
dotnet test  .\PolyMount.slnx
```

The build also needs `winfsp-msil.dll` from a WinFsp installation; the projects reference it at
`C:\Program Files (x86)\WinFsp\bin`.

Run the service and the tray from a build:

```powershell
.\src\PolyMount.Service\bin\Debug\net10.0-windows\PolyMount.Service.exe
.\src\PolyMount.Tray\bin\Debug\net10.0-windows\PolyMount.Tray.exe
```

Build the installer (needs Inno Setup 6):

```powershell
.\Build-Installer.bat 0.5.0
```

The version becomes the setup's file name and the app version.

### Checks worth running while changing something

| Script | What it proves |
|---|---|
| `scripts\Test-InstanceWorker.ps1` | one instance mounts and answers on its own pipe; self-contained (`dir` source, temp dirs) |
| `scripts\Test-InstanceIsolation.ps1` | killing one instance's worker leaves the other instance mounted, and the supervisor restarts the dead one |
| `scripts\Test-ObjectMount.ps1` | copy-up, delete, rename, persistence and compression, with the source left untouched |
| `scripts\Test-Mcp.ps1` | the stdio MCP handshake, tool catalog, read calls and error paths; `-Full` also enables, mounts and unmounts an instance |
| `PolyMount.Tray.exe --ui-smoke` | the windows, the language switch, the chart cards, the recording bar |
| `PolyMount.Tray.exe --render <dir>` | draws the chart fixtures and prints the plot-area geometry; no service needed |

## Configuration

Installed instances live in `%PROGRAMDATA%\PolyMount\config.json`. The tray's editor writes this
file; it is ordinary JSON and can be edited by hand. A service started from a build uses
`config.json` beside its executable unless `--config` names another file.

```jsonc
{
  "WinFspBin": "C:\\Program Files (x86)\\WinFsp\\bin",
  "ControlPipe": "PolyMount.Control",
  "ControlUsers": [
    { "Sid": "S-1-5-21-…-1001", "Name": "WORKSTATION\\you" }   // who may drive the service; see Security
  ],
  "Plugins": [
    { "AssemblyPath": "plugins/PolyMount.Plugin.Svn.dll", "Enabled": true }
  ],
  "Mounts": [
    {
      "Id": "…",                          // created once, names the instance's store
      "Name": "project",
      "Enabled": true,
      "MountPoint": "D:\\project",
      "StoreRoot": "",                    // empty = %PROGRAMDATA%\PolyMount\stores\<Id>
      "Layers": [
        { "Id": "memory-cache", "Type": "memory-cache", "Enabled": true },
        { "Id": "warmup",       "Type": "warmup",       "Enabled": true },
        { "Id": "predict",      "Type": "prediction-cache", "Enabled": true },
        { "Id": "cache",        "Type": "cache",        "Enabled": true,
          "CacheCapacityBytes": 4294967296,
          "Parameters": { "Writable": false } },
        { "Id": "overlay",      "Type": "overlay",      "Enabled": true },
        { "Id": "svn",          "Type": "svn",          "Enabled": true,
          "Source": {
            "Type": "svn",
            "Url": "https://svn.example.com/repo/trunk",
            // Certificate failures this source accepts; empty means the certificate must verify.
            // A self-signed internal server lists them: unknown-ca, cn-mismatch, expired,
            // not-yet-valid, other.
            "TrustServerCertFailures": ""
          } }
      ]
    }
  ]
}
```

The chain above is the layer order of the `svn-workspace` preset; applying the preset also brings
its capacities and read-ahead settings, which the example leaves at defaults. A mount is legal
with no source, with only a source, or with several sources.

**New mount** builds an instance from one of those presets: the dialog first shows what each preset
mounts and which values it will ask for, then asks only those — the source's address and account —
while the name and the mount point follow the address until they are typed over. Everything else
keeps the preset's defaults and is adjusted afterwards in the editor, which the dialog's last card
opens directly for a chain built by hand. Nothing is written until the dialog commits, and the
service's refusal (an address the source will not take) is shown under the field it came from.

The tray's **Plugins** section — beside the mount overview in the sidebar, in the same page —
shows what each configured plugin adds and keeps that `Plugins` list. **While the service runs it
is read-only**: changing the set restarts every worker and remounts every instance, and an
imported DLL cannot replace one the service holds open. Stop the service with the sidebar's
button, and the section reads and writes the configuration itself — enable or disable a plugin,
remove an entry (the DLL stays), or import a DLL from anywhere; the changes take effect when the
service starts. A DLL copied by hand into the service's `plugins` folder appears there among the
files that can be added — the loader still reads the configuration rather than the folder, so
copying a file alone does not load it. Writing the configuration may need administrator rights.

## Plugins

Plugins are the extension point, and a data source is one thing they can add. A plugin is an
assembly with a class implementing `IPolyMountPlugin`; it may also implement the optional
`IPluginMetadata` to give the tray a localized title and description and the name of one of its
line icons, so the plugin page shows what the plugin calls itself rather than a file name.
The `IPluginContext` it is given registers
layer types (ordinary and cache), source types, presets, prediction strategies, control commands
and control operations. A layer can also implement `ILayerActionHandler` to answer the actions its
own descriptor declares. The service loads **only** the assemblies its configuration names —
nothing is discovered by scanning a directory — so an installation starts with exactly the
plugins its operator chose. A named assembly has to resolve inside the installation directory: a
plugin is code that runs with the service's rights, and the installation directory is writable only
by an administrator. The tray's Plugins section is the front end for that list: it reports
which files are in the folder but not named, and adding one writes the entry. Assemblies built
against the older `ILayerPlugin` still load: that name remains as an obsolete alias.

The bundled SVN plugin adds the `svn` source type, the `svn-workspace` preset, and the `manifests`
prediction strategy — a package-family prefetcher for content repositories, so one plugin covers a
Subversion source whether or not it is a game project. There is one Subversion preset, not two: it
carries the configuration a content-repository start-up was measured with (a 1 GiB memory cache, a
10k listing warm-up, a 256 MiB prediction layer running only `manifests`, and a 16 GiB durable read
cache) instead of leaving a bare default beside a tuned variant. The
remote-folder plugin adds the `smb` source type and the `smb-workspace` preset — a folder shared by
another machine, read through the Windows redirector, with nothing installed over there. Local
directories are built in (`dir`). The transaction-shim plugin answers a different need: some
programs open their files through the Transactional NTFS APIs, which a WinFsp volume may refuse
with `ERROR_TRANSACTIONAL_OPEN_NOT_ALLOWED` (6832). Rather than claiming transaction support the
volume does not have, the plugin starts chosen programs through a native launcher that replaces
those calls inside the process before they reach the kernel. Enabling it installs nothing: an
administrator names the directory whose programs should be instrumented (`install_txf_shim`), and
only processes started afterwards are affected — a shimmed open succeeds, and a rollback no longer
undoes the writes the program made. Writing another source means writing a plugin that registers a
source type: reference `PolyMount.Core`, implement `IPolyMountPlugin`, and ship a DLL.
`PolyMount.Plugin.Testing` is the test contract a plugin can ship its own tests against; the test
projects live under `tests/plugins` and are not shipped with the production plugin.

## Architecture

```
PolyMount.Core                        layering engine, WinFsp adapter, IPC contracts, plugin loading
PolyMount.Service                     supervisor (one per machine) + one worker process per mount instance
PolyMount.Tray                        Qt tray and windows, charts, config editor, recording UI
PolyMount.Mcp                         stdio MCP server over the control pipe
PolyMount.Shell                       native Explorer overlay handler (source badges)
plugins/PolyMount.Plugin.Svn          Subversion source, manifest prefetcher, and the svn-workspace preset
plugins/PolyMount.Plugin.Smb          folder on another machine (SMB) and smb-workspace preset
plugins/PolyMount.Plugin.TxFShim      transaction-free opens for chosen programs (native shim + launcher)
PolyMount.Plugin.Testing              test contract for plugin assemblies
```

- The service and the tray talk over **named pipes**, line-delimited JSON. Front-ends ask the
  supervisor where each instance is served and then connect to that instance's own pipe.
- Each mount's state — pin, cache, overlay — lives under one store directory, named by the
  instance's stable `Id`, so renaming an instance keeps its cache.
- The tray draws its charts with **Qt Charts**, through the `QtSharp.Charts` bindings. See the
  licensing note below.

## MCP

`PolyMount.Mcp.exe` is a stdio MCP server an agent starts. It speaks the control protocol over the
same named pipe the tray uses, so it adds a caller, not a listening port: an agent can list
instances, inspect a path across the layers, read metrics, mount or unmount, pin a revision, start
a recording, and edit the configuration. The setup installs it under `{app}\Mcp`; nothing starts
it automatically.

The catalog is `PolyMount.Core`'s control operations: 26 built in, plus whatever enabled plugins
contribute (the SVN plugin adds `get_svn_source`, `list_svn_lanes` and `list_manifest_families`,
the remote-folder plugin adds `get_smb_source`, and the transaction-shim plugin adds
`get_txf_shim`, `list_txf_shim_targets`, `install_txf_shim`, `remove_txf_shim` and
`read_txf_shim_log`). `--describe` prints it without a service.

- `--pipe <name>` or `POLYMOUNT_PIPE` selects the control pipe.
- `--read-only` registers only operations with `effect: read` (nine built in, plus plugin reads).
- Destructive operations (`clear_cache`, `clear_local_changes`, `apply_layer_action`,
  `release_above`, `shutdown_service`) carry `destructiveHint`.
- `get_config` replaces stored passwords with `[redacted]`; `save_config` refuses a plaintext
  password, so it cannot be used to set one.
- `--config <path>` and `--plugin <dll>` load the same plugins as the service, so their operations
  become tools.

`scripts\Test-Mcp.ps1` drives the handshake and the error paths against a supervisor of its own;
`-Full` also exercises enable → mount → unmount.

## Security

The service holds credentials and reads what the signed-in user may not, while the tray and the
MCP server run as that user — so what each boundary enforces is written down in
[SECURITY.md](SECURITY.md). The short version:

- **Who may drive the service** is the `ControlUsers` list, by SID. The installer seeds it from the
  account that ran setup; an empty list keeps the older behaviour (every interactive local user) and
  is reported in the service log and in every `status` answer.
- **A plugin is code from the installation directory.** A path that resolves outside it is refused
  before anything is loaded, and a configuration that is refused leaves the running plugin set
  alone.
- **Certificates must verify** by default. A self-signed internal server names the failures it
  accepts in its own configuration; a plain `http://` source needs `AllowInsecureTransport`.
- **A stored credential is bound to the origin it was first used for**, and the repository root the
  server advertises must stay on the configured origin. The credential's **Servers** field — in the
  tray's credential manager — is where a second server sharing that account is named; a mount that
  was refused because of the binding says so, with both addresses, and points there.
- **Secrets do not travel with the configuration.** `getConfig` answers with
  `[redacted]` in the password fields, and a save that still says `[redacted]` keeps the stored
  value.
- **The stores are the service's.** Their directory ACL is restricted when an instance mounts, a
  tampered store catalog cannot name a file outside the store, and a compressed object is decoded
  no further than its record says.
- **The mounted volume itself has no per-file ACLs.** The product is single-user by design; a
  machine shared between accounts should not share one PolyMount installation.

## Update checks

The tray asks its release feed at most once a day. A newer version produces a notification and two
extra menu items — **Download PolyMount x.y.z** and **Do not tell me about x.y.z again**. The
second silences that version only: a later one is announced again, and the download stays in the
menu either way. **Check for updates** is always available, including after a dismissal.

The feed defaults to
`https://api.github.com/repos/ProxyCheng/PolyMount/releases/latest`. A repository that is not
public answers that without credentials with a 404 — an automatic check then stays silent, and a
manual one says it could not check rather than claiming to be up to date. Both the feed and an
optional token can be set in `%LOCALAPPDATA%\PolyMount\ui.json`:

```json
{
  "update.feed": "https://example.com/polymount/latest.json",
  "update.token": "<token, if the feed needs one>"
}
```

The feed may use GitHub's shape (`tag_name`, `html_url`) or a plain one (`version`, `url`), so
pointing it at a file server needs no code change. Tags are compared as versions, not as text, and
`v1.2.3` and `1.2.3+stamp` both read as `1.2.3`.

## Status

Early, but functional end to end: layered reads and writes, SVN and directory sources, pinning,
cache management, links inside a writable mount and volumes Windows accepts as volumes,
per-instance processes, a plugin API, a tray UI, performance instrumentation, a recording bundle
for diagnosing slow reads, and an MCP server for agents. Interfaces may still move.

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). Build before you
send: `dotnet build` is expected to finish with **0 warnings**, and the tests to pass.

## License

PolyMount is free software under the **GNU General Public License v3.0 or later** — see
[LICENSE](LICENSE).

One dependency decides that: the charts are drawn with **Qt Charts, which is GPLv3-or-commercial**
(unlike the rest of Qt, which is LGPLv3). Distributing a build of PolyMount therefore means
distributing Qt Charts, and the GPLv3 governs that distribution unless you hold a commercial Qt
licence. [`installer/THIRD-PARTY-NOTICES.txt`](installer/THIRD-PARTY-NOTICES.txt) lists the
components and their terms.
