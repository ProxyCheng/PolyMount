<#
.SYNOPSIS
  Enables or disables launching PolyMount.Tray at user logon (per-user, no admin required).

.EXAMPLE
  .\Set-TrayAutostart.ps1              # enable (Debug build)
  .\Set-TrayAutostart.ps1 -Configuration Release
  .\Set-TrayAutostart.ps1 -Disable
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$Disable
)

$ErrorActionPreference = "Stop"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$valueName = "PolyMountTray"

if ($Disable) {
    Remove-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue
    Write-Host "PolyMount tray autostart disabled." -ForegroundColor Green
    return
}

$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo "src\PolyMount.Tray\bin\$Configuration\net10.0-windows\PolyMount.Tray.exe"
if (-not (Test-Path $exe)) {
    throw "Tray executable not found: $exe`nBuild first: dotnet build $repo\PolyMount.slnx -c $Configuration"
}

New-ItemProperty -Path $runKey -Name $valueName -Value "`"$exe`"" -PropertyType String -Force | Out-Null
Write-Host "PolyMount tray will start at logon: $exe" -ForegroundColor Green
