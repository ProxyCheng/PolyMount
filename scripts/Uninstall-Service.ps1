<#
.SYNOPSIS
  Stops and removes the PolyMount Windows service. Requires an elevated PowerShell.
#>
[CmdletBinding()]
param([string]$ServiceName = "PolyMount")

$ErrorActionPreference = "Stop"

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw "This script must be run from an elevated (Administrator) PowerShell."
}

sc.exe query $ServiceName *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Service '$ServiceName' is not installed."
    return
}

Write-Host "Stopping '$ServiceName'..."
sc.exe stop $ServiceName *> $null
Start-Sleep -Seconds 2
sc.exe delete $ServiceName | Out-Null
Write-Host "PolyMount service removed." -ForegroundColor Green
