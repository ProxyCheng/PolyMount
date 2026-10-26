<#
.SYNOPSIS
  Installs PolyMount.Service as a Windows service.

.DESCRIPTION
  The service runs under a user account (default: the current user) so it can reuse that
  user's cached SVN credentials. Requires an elevated (Administrator) PowerShell.

  The account's password is handed to the service provider as a credential object and never
  becomes a command line: while `sc.exe create ... password= <plain>` runs, the password is in
  the process inventory, in any command-line telemetry and in PowerShell transcription. The
  packaged installer (installer\Install-Host.ps1) already worked this way; this script is the
  other documented installation path and now matches it.

.EXAMPLE
  .\Install-Service.ps1
  .\Install-Service.ps1 -Configuration Release -Account ".\YourAccount"
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
$serviceDirectory = Join-Path $repo "src\PolyMount.Service\bin\$Configuration\net10.0-windows"
$exe = Join-Path $serviceDirectory "PolyMount.Service.exe"
if (-not (Test-Path $exe)) {
    throw "Service executable not found: $exe`nBuild first: dotnet build $repo\PolyMount.slnx -c $Configuration"
}

Write-Host "Service account: $Account"
# Prompted for, held as a SecureString, and given straight to the service provider: nothing in
# this script converts it to text.
$cred = Get-Credential -UserName $Account -Message "Enter the password for the PolyMount service account"
if (-not $cred) { throw "The service account credentials were cancelled." }

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
New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "PolyMount Virtual Mount Service" `
    -StartupType Automatic -Credential $cred | Out-Null
& sc.exe description $ServiceName "Mounts layered virtual drives (SVN/dir/overlay/cache) via WinFsp." | Out-Null
& sc.exe start $ServiceName
if ($LASTEXITCODE -ne 0) { throw "sc.exe start failed with exit code $LASTEXITCODE" }

# The control plane's allowlist: the account that ran this script, by SID. Without it the pipe
# keeps the interactive grant, and every standard local user can drive the service.
$config = Join-Path $serviceDirectory "config.json"
if (Test-Path -LiteralPath $config) {
    try {
        $parsed = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $users = @()
        foreach ($entry in @($parsed.ControlUsers)) { if ($entry -and $entry.Sid) { $users += $entry } }
        if (-not ($users | Where-Object { $_.Sid -eq $identity.User.Value })) {
            $users += [pscustomobject]@{ Sid = $identity.User.Value; Name = $identity.Name }
            $parsed | Add-Member -NotePropertyName ControlUsers -NotePropertyValue @($users) -Force
            [System.IO.File]::WriteAllText($config, ($parsed | ConvertTo-Json -Depth 20),
                (New-Object System.Text.UTF8Encoding($false)))
            Write-Host "Added $($identity.Name) to the configuration's ControlUsers." -ForegroundColor DarkGray
        }
    } catch {
        Write-Warning "The configuration's ControlUsers was left as it is: $($_.Exception.Message)"
    }
}

Write-Host "PolyMount service installed and started." -ForegroundColor Green
