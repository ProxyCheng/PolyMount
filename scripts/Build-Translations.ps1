[CmdletBinding()]
param([string]$LRelease)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if (-not $LRelease) {
    $command=Get-Command lrelease.exe -ErrorAction SilentlyContinue
    if ($command) { $LRelease=$command.Source }
}
if (-not $LRelease) { throw 'Pass -LRelease with the Qt Linguist lrelease.exe path.' }
foreach($source in Get-ChildItem -Path (Join-Path $repo 'src\PolyMount.Tray\translations\*.ts')) {
    & $LRelease $source.FullName -qm ([IO.Path]::ChangeExtension($source.FullName,'.qm'))
    if ($LASTEXITCODE -ne 0) { throw "Translation compilation failed: $($source.Name)" }
}
