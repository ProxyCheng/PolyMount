# PolyMount

**Mount a remote repository as a drive letter — on demand, without a full checkout.**

[![CI](https://github.com/ProxyCheng/PolyMount/actions/workflows/ci.yml/badge.svg)](https://github.com/ProxyCheng/PolyMount/actions/workflows/ci.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

Large repositories are painful to browse: a full checkout is slow, it eats the disk, and most of
it is never opened. PolyMount presents a repository as a normal Windows drive instead — Explorer
opens it, your editor opens it, `grep` runs over it — and every file is fetched the first time
something reads it. What you have read is cached; what you write stays local, layered on top of
the remote view without touching it.

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
overlay  (writable)  ── local changes, copy-on-write
   ↓
cache    (read-through, on disk, LRU)  ── what has been read, keyed by content generation
   ↓
source   (plugin: SVN, local directory, …)  ── the thing that actually has the bytes
```

- **Reads** walk down until a layer has the path. A hit below any cache layer is copied back up
  into it, so the next read is local.
- **Listings** come from whichever layer holds the directory, as a complete manifest — the layers
  are never unioned. A deleted file is simply absent from its parent's manifest, so it stays gone
  without leaving a tombstone behind.
- **Writes** go to the first writable layer, doing a *copy-up* from below first if the file only
  exists remotely.
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
| Runtime | None. The installer ships a self-contained .NET runtime and Qt. |
| SVN source | the Subversion command-line client (`svn.exe`) on the machine `PATH` |
| Building | .NET 10 SDK, Visual Studio C++ build tools (the Explorer extension), Inno Setup 6 |

## Install

Download `PolyMount-<version>-win-x64-Setup.exe` from
[Releases](https://github.com/ProxyCheng/PolyMount/releases) and run it. The wizard asks which
plugins to enable, whether the tray should start at sign-in, and whether Explorer should show
source-colour dots.

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
.\Build-Installer.bat 0.3.0
```

### Checks worth running while changing something

| Script | What it proves |
|---|---|
| `scripts\Test-InstanceWorker.ps1` | one instance mounts and answers on its own pipe; self-contained (`dir` source, temp dirs) |
| `scripts\Test-ObjectMount.ps1` | copy-up, delete, rename, persistence and compression, with the source left untouched |
| `PolyMount.Tray.exe --ui-smoke` | the windows, the language switch, the chart cards, the recording bar |
| `PolyMount.Tray.exe --render <dir>` | draws the chart fixtures and prints the plot-area geometry; no service needed |

## Configuration

Instances live in `%PROGRAMDATA%\PolyMount\config.json`. The tray's editor writes this file; it is
ordinary JSON and can be edited by hand.

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
        { "Id": "overlay", "Type": "overlay", "Enabled": true },
        { "Id": "cache",   "Type": "cache",   "Enabled": true,
          "CacheCapacityBytes": 4294967296 },
        { "Id": "svn",     "Type": "svn",     "Enabled": true,
          "Source": { "Type": "svn", "Url": "https://svn.example.com/repo/trunk" } }
      ]
    }
  ]
}
```

A mount is legal with no source, with only a source, or with several sources; a cache layer
crossed by a writable layer above it is not backfilled, because local edits must not be written
into a cache shared with the remote.

## Plugins

A data source is a plugin: an assembly implementing the `ILayer` contracts from `PolyMount.Core`
and registering itself. The service loads **only** what its configuration names — nothing is
discovered by scanning a directory — so an installation starts with exactly the sources its
operator chose.

The bundled SVN plugin adds the `svn` source type and a ready-made layer preset. Local directories
are built in (`dir`). Writing another source means referencing `PolyMount.Core`, implementing
`ILayer`, and shipping a DLL — the test contract package (`PolyMount.Plugin.Testing`) lets a plugin
bring its own tests without depending on any test framework.

## Architecture

```
PolyMount.Core      the layering engine, WinFsp adapter, IPC contracts, plugin loading
PolyMount.Service   supervisor (one per machine) + one worker process per mount instance
PolyMount.Tray      Qt tray and windows, drawn charts, config editor, recording UI
PolyMount.Shell     native Explorer overlay handler (source-colour dots)
plugins/Svn         the Subversion source plugin
```

- The service and the tray talk over **named pipes**, line-delimited JSON. Front-ends ask the
  supervisor where each instance is served and then connect to that instance's own pipe.
- Each mount's state — pin, cache, overlay — lives under one store directory, named by the
  instance's stable `Id`, so renaming an instance keeps its cache.
- The tray draws its charts with **Qt Charts** (through the `QtSharp` bindings). See the licensing
  note below.

## Status

Early, but functional end to end: layered reads and writes, SVN and directory sources, pinning,
cache management, per-instance processes, a tray UI, performance instrumentation, and a recording
bundle for diagnosing slow reads. Interfaces may still move.

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
