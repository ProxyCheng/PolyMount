[CmdletBinding()]
param([string]$Destination, [ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$Version)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$published = @()
# A plugin ships with the product, so a release build stamps it with the release number: that is
# what the plugin page shows, and what an installed plugin's file properties page shows. Without
# one, every assembly carries the shared identity's 1.0.0.0 and the list cannot tell two builds
# apart.
$versionArg = @()
if ($Version) { $versionArg = @('-p:Version=' + $Version) }
foreach ($project in Get-ChildItem -Path (Join-Path $repo 'src\plugins\*\*.csproj')) {
    & dotnet publish $project.FullName -c $Configuration --self-contained false -o $Destination --nologo -v minimal @versionArg
    if ($LASTEXITCODE -ne 0) { throw "Plugin publish failed: $($project.Name)" }
    $published += $project.BaseName
}

# What this deployment owns: the files of the plugins it just published, named after them. It is
# written beside them and read on the next run.
$recordPath = Join-Path $Destination '.installed'
$produced = @(Get-ChildItem -LiteralPath $Destination -File | Where-Object {
    $name = $_.Name
    @($published | Where-Object { $name -like ($_ + '.*') }).Count -gt 0
} | ForEach-Object { $_.Name })

# A plugin removed from the tree has to leave the deployment too, or the host keeps loading the
# copy it was never told to stop loading. What decides is the record of what an earlier run of
# *this* script put here — not the shape of the name. A deployment also holds plugins this build
# does not produce (a private plugin installed by hand); those were never in the record, so they
# are left exactly as they are, and said out loud rather than removed. That is why a private
# plugin no longer has to avoid a name this script would otherwise sweep.
$previous = @()
if (Test-Path -LiteralPath $recordPath) {
    $previous = @(Get-Content -LiteralPath $recordPath | Where-Object { $_.Trim() })
}
foreach ($file in $previous) {
    if ($produced -contains $file) { continue }
    $path = Join-Path $Destination $file
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
        Write-Host "  removed $file (this build no longer produces it)"
    }
}

# Shared contracts are supplied by the host. Test dependencies never come from src/plugins.
# PolyMount.Plugin.Abstractions is the namespace and assembly a plugin compiles against; the host
# deploys its own copy, and a plugin folder that carried a second one would shadow it.
Get-ChildItem -LiteralPath $Destination -File | Where-Object {
    $_.Name -eq 'PolyMount.Core.dll' -or $_.Name -eq 'PolyMount.Core.pdb' -or
    $_.Name -like 'PolyMount.Plugin.Abstractions.*' -or
    $_.Name -like '*.Tests.*' -or
    $_.Name -like 'PolyMount.Plugin.Testing.*'
} | Remove-Item -Force

Set-Content -LiteralPath $recordPath -Value $produced -Encoding ascii

# What is here that this build does not produce. A plugin says so in its version resource — the
# SDK stamps "PolyMount plugin; contract=x.y" into the description — which a stray dependency DLL
# does not have; a plugin published before the manifest moved inside the assembly still has the
# file beside it. Either way this names plugins and not the runtime files that happen to sit here.
$strangers = @(Get-ChildItem -LiteralPath $Destination -File -Filter '*.dll' | Where-Object {
    $_.Name -notlike '*.Tests.dll' -and
    ($produced -notcontains $_.Name) -and
    ( (([System.Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName)).Comments -like 'PolyMount plugin; contract=*') -or
      (Test-Path -LiteralPath (Join-Path $Destination ($_.BaseName + '.plugin.json'))) )
} | ForEach-Object { $_.Name })
if ($strangers.Count -gt 0) {
    Write-Host '  these plugins are not part of this build and were left in place:'
    foreach ($file in $strangers) { Write-Host "    $file" }
    Write-Host '  (remove one by taking it out of the folder and out of the configuration)'
}
