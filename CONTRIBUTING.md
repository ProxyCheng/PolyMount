# Contributing to PolyMount

Thanks for taking the time. This is a Windows-only project: a .NET service, a Qt tray application,
a native Explorer extension and a Windows filesystem driver (WinFsp). The notes below are the
things that are easy to get wrong.

## Before you start

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Fetch-QtSharp.ps1   # once per clone
dotnet build .\PolyMount.slnx
dotnet test  .\PolyMount.slnx
```

`Fetch-QtSharp.ps1` is not optional: `QtSharp.Api` and `QtSharp.Charts` are not on nuget.org, and
the NuGet source named `qtsharp` is a local folder the script fills.

A build also needs WinFsp installed (`winfsp-msil.dll` is referenced at its installed path) and,
for the Explorer extension and the installer, Visual Studio C++ build tools and Inno Setup 6.

## What a change is expected to carry

- **No new warnings.** The solution builds with `Nullable` enabled and is expected to stay at
  0 warnings.
- **Tests where the behaviour is testable without a driver.** `PolyMount.Core.Tests` is the MSTest
  project; plugins carry their own tests through `PolyMount.Plugin.Testing`.
- **The reason, in the code.** Comments here say *why* a thing is the way it is — a decision, a
  measurement, a trap — not what the next line does. Match the surrounding style.
- **A check, when the change is in a path a unit test cannot reach.** The self-contained scripts
  under `scripts\` mount real WinFsp drives with a `dir` source and their own configuration; they
  are what CI runs.

## Work on a branch, merge into master

This tree is shared: more than one person or agent may have it open at the same time, and a
change committed straight onto `master` lands under whoever else is building from it. Keep
`master` as the integration point and do the work on a branch:

```powershell
git switch -c <scope>/<short-name> origin/master
# ... work, commit ...
dotnet build .\PolyMount.slnx
dotnet test  .\PolyMount.slnx
git switch master
git merge --no-ff <scope>/<short-name>
git branch -d <scope>/<short-name>
```

Rebase or merge the current `origin/master` into the branch before merging it back, and do not
force-push `master` to make a branch fit. A branch that is still being written never enters
`master`, so another session can keep building and committing there while it is in progress.

## Commits

One logical change per commit, with a message that says what changed and why. The subject line is
lowercase with a type and, where it helps, a scope:

```
fix(installer): the configuration the setup writes names the bundled plugin
feat(tray): the sidebar's pickers are one to a row
docs: say what the package actually contains
```

## Pull requests

Describe the problem before the patch, and say how you verified it — a command, a test, a
screenshot. If a change alters the configuration format, the IPC protocol or the installer's
behaviour, update `README.md` and `CHANGELOG.md` in the same change.

## Licensing

By contributing you agree that your contribution is licensed under the same terms as the project:
the GNU General Public License v3.0 or later. Note that the product links Qt Charts (GPLv3 or a
commercial Qt licence); see [installer/THIRD-PARTY-NOTICES.txt](installer/THIRD-PARTY-NOTICES.txt).
