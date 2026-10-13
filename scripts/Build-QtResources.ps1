[CmdletBinding()]
param([string]$Rcc, [string]$LRelease)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($LRelease) { & (Join-Path $PSScriptRoot 'Build-Translations.ps1') -LRelease $LRelease }
if (-not $Rcc) { $tool = Get-Command rcc.exe -ErrorAction SilentlyContinue; if ($tool) { $Rcc = $tool.Source } }
if (-not $Rcc) { throw 'Pass -Rcc with the Qt resource compiler path.' }
$resources = Join-Path $repo 'src\PolyMount.Tray\resources'
& $Rcc --binary --no-compress (Join-Path $resources 'polymount.qrc') -o (Join-Path $resources 'polymount.rcc')
if ($LASTEXITCODE -ne 0) { throw 'Qt resource compilation failed.' }
