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
prediction        │  caches in this group; warm-up fills listings in the
disk cache        ┘  background; a write invalidates the paths it touched.
   ↓
overlay  (writable)  ── local changes, copy-on-write
   ↓
source   (plugin: SVN, local directory, …)  ── the thing that actually has the bytes
```

In the bundled presets the layers above the overlay are optional and read-only. In the SVN preset
the overlay sits directly above the source, and the durable cache sits directly above the overlay
with `Writable = false`, so a write never stops in it. The local-directory preset has no durable
cache.

- **Reads** walk down until a layer has the path. A hit below a cache layer is copied back up
  into it, so the next read is local. A cache that cannot follow writes is not filled when a
  writable layer sits between it and the layer that answered. The memory and disk caches can
  follow writes, so they are filled and then told which paths changed.
- **Listings** come from whichever layer holds the directory, as a complete manifest — the layers
  are never unioned. A deleted file is simply absent from its parent's manifest, so it stays gone
  without leaving a tombstone behind. Resolving a child by name reads that same manifest: the
  child's header is already in it, so the source is not asked again, and the name matches
  regardless of case the way NTFS does. Only layers above the manifest's provider can override
  the listed header.
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
restarting, crashing, or being reconfigured cannot take another with it.

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11, x64 |
| Driver | [WinFsp](https://winfsp.dev/) 2.x — the installer installs it if it is missing |
| Runtime | None to install by hand. The setup ships a self-contained .NET runtime, the Qt runtime and the Visual C++ runtime. |
| SVN source | an `http(s)` repository (served over WebDAV); no client is needed. Add a credential in the tray's credential manager and choose it on the layer. Secrets are protected with Windows DPAPI for the account that saved them, so run the service under that account (the setup offers this). A repository behind mutual TLS uses a **certificate credential** — a `.pfx`/`.p12` file and the password that opens it, kept in the credential store — which works even under a service account that cannot see the user's personal store or export the user's key. |
| Building | .NET 10 SDK, Visual Studio C++ build tools (the Explorer extension), Inno Setup 6 |

## Install

Download `PolyMount-<version>-win-x64-Setup.exe` from
[Releases](https://github.com/ProxyCheng/PolyMount/releases) and run it. The wizard asks which
bundled plugins to enable (the SVN source and the game-engine preset), whether the tray should
start at sign-in, whether Explorer should show source-colour dots, and optionally whether to run
the service under your Windows account so it can use the SVN sign-ins saved there.

Uninstalling asks before deleting your configuration and cached data; answering **No** keeps them
for a later installation.

## Build from source

```powershell
# QtSharp is not on nuget.org; this fetches the two pinned packages into .\packages
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
          "Source": { "Type": "svn", "Url": "https://svn.example.com/repo/trunk" } }
      ]
    }
  ]
}
```

The chain above is the layer order of the `svn-workspace` preset. A mount is legal with no source,
with only a source, or with several sources.

## Plugins

Plugins are the extension point, and a data source is one thing they can add. A plugin is an
assembly with a class implementing `IPolyMountPlugin`; the `IPluginContext` it is given registers
layer types (ordinary and cache), source types, presets, prediction strategies, control commands
and control operations. A layer can also implement `ILayerActionHandler` to answer the actions its
own descriptor declares. The service loads **only** the assemblies its configuration names —
nothing is discovered by scanning a directory — so an installation starts with exactly the
plugins its operator chose. Assemblies built against the older `ILayerPlugin` still load: that
name remains as an obsolete alias.

The bundled SVN plugin adds the `svn` source type and the `svn-workspace` preset. The game-engine
plugin adds a `manifests` prediction strategy and the `game-engine-workspace` preset. Local
directories are built in (`dir`). Writing another source means writing a plugin that registers a
source type: reference `PolyMount.Core`, implement `IPolyMountPlugin`, and ship a DLL.
`PolyMount.Plugin.Testing` is the test contract a plugin can ship its own tests against; the test
projects live under `tests/plugins` and are not shipped with the production plugin.

## Architecture

```
PolyMount.Core                        layering engine, WinFsp adapter, IPC contracts, plugin loading
PolyMount.Service                     supervisor (one per machine) + one worker process per mount instance
PolyMount.Tray                        Qt tray and windows, charts, config editor, recording UI
PolyMount.Mcp                         stdio MCP server over the control pipe
PolyMount.Shell                       native Explorer overlay handler (source-colour dots)
plugins/PolyMount.Plugin.Svn          Subversion source and svn-workspace preset
plugins/PolyMount.Plugin.GameEngine   manifests prediction and game-engine-workspace preset
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

The catalog is `PolyMount.Core`'s control operations: 22 built in, plus whatever enabled plugins
contribute (the SVN plugin adds `get_svn_source` and `list_svn_lanes`, the game-engine plugin adds
`list_manifest_families`). `--describe` prints it without a service.

- `--pipe <name>` or `POLYMOUNT_PIPE` selects the control pipe.
- `--read-only` registers only operations with `effect: read` (eight built in, plus plugin reads).
- Destructive operations (`clear_cache`, `clear_local_changes`, `apply_layer_action`,
  `release_above`, `shutdown_service`) carry `destructiveHint`.
- `get_config` replaces stored passwords with `[redacted]`; `save_config` refuses a plaintext
  password, so it cannot be used to set one.
- `--config <path>` and `--plugin <dll>` load the same plugins as the service, so their operations
  become tools.

`scripts\Test-Mcp.ps1` drives the handshake and the error paths against a supervisor of its own;
`-Full` also exercises enable → mount → unmount.

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
