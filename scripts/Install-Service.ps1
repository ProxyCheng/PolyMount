<#
.SYNOPSIS
  Installs PolyMount.Service as a Windows service.

.DESCRIPTION
  The service runs under a user account (default: the current user) so it can reuse that
  user's cached SVN credentials. Requires an elevated (Administrator) PowerShell.

.EXAMPLE
  .\Install-Service.ps1
  .\Install-Service.ps1 -Configuration Release -Account ".\chengziqi"
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "PolyMount",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$Account = "$env:USERDOMAIN\$env:USERNAME"
)

$ErrorActionPreference = "Stop"

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
        throw "This script must be run from an elevated (Administrator) PowerShell."
    }
}

Assert-Admin

$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo "src\PolyMount.Service\bin\$Configuration\net10.0-windows\PolyMount.Service.exe"
if (-not (Test-Path $exe)) {
    throw "Service executable not found: $exe`nBuild first: dotnet build $repo\PolyMount.slnx -c $Configuration"
}

Write-Host "Service account: $Account"
$cred = Get-Credential -UserName $Account -Message "Enter the password for the PolyMount service account"
$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($cred.Password))

# Remove any existing instance first.
sc.exe query $ServiceName *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Existing service found; stopping and deleting it."
    sc.exe stop $ServiceName *> $null
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

Write-Host "Creating service '$ServiceName'..."
# NOTE: sc.exe requires a space after each '=' option.
& sc.exe create $ServiceName binPath= "`"$exe`"" obj= "$($cred.UserName)" password= "$plain" start= auto DisplayName= "PolyMount Virtual Mount Service"
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE" }

& sc.exe description $ServiceName "Mounts layered virtual drives (SVN/dir/overlay/cache) via WinFsp." | Out-Null
& sc.exe start $ServiceName
if ($LASTEXITCODE -ne 0) { throw "sc.exe start failed with exit code $LASTEXITCODE" }

Write-Host "PolyMount service installed and started." -ForegroundColor Green
