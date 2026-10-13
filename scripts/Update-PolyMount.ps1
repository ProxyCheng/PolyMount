<#
.SYNOPSIS
  Stop -> publish in place -> restart PolyMount. Invoked by ..\Update.bat.
.DESCRIPTION
  Preserves config.json and layer data. Supports interactive hosting and the installed
  PolyMount Windows service. Run as administrator when updating an installed service.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$PipeName = 'PolyMount.Control'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$serviceProject = Join-Path $repo 'src\PolyMount.Service\PolyMount.Service.csproj'
$trayProject = Join-Path $repo 'src\PolyMount.Tray\PolyMount.Tray.csproj'
$serviceDir = Join-Path $repo "src\PolyMount.Service\bin\$Configuration\net10.0-windows"
$trayDir = Join-Path $repo "src\PolyMount.Tray\bin\$Configuration\net10.0-windows"
$serviceExe = Join-Path $serviceDir 'PolyMount.Service.exe'
$trayExe = Join-Path $trayDir 'PolyMount.Tray.exe'
$installed = Get-Service -Name 'PolyMount' -ErrorAction SilentlyContinue

function Send-Control([string]$Command) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(1000)
        $writer = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($pipe, [System.Text.Encoding]::UTF8)
        $writer.WriteLine((@{ Cmd = $Command } | ConvertTo-Json -Compress))
        $reply = $reader.ReadLineAsync()
        if (-not $reply.Wait(3000)) { throw 'Control pipe response timed out.' }
        $response = $reply.Result | ConvertFrom-Json
        if (-not $response.Ok) { throw $response.Error }
        return $response
    }
    finally { $pipe.Dispose() }
}

function Start-PolyMount {
    if ($installed) {
        Start-Service -Name 'PolyMount'
    }
    else {
        Start-Process -FilePath $serviceExe -WorkingDirectory $serviceDir -WindowStyle Hidden | Out-Null
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $response = $null
    $ready = $false
    do {
        try {
            $response = Send-Control 'status'
            $waiting = @($response.Mounts | Where-Object { $_.Enabled -and -not $_.Mounted -and -not $_.Error })
            $ready = $waiting.Count -eq 0
        }
        catch { $ready = $false }
        if (-not $ready) { Start-Sleep -Milliseconds 500 }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    if (-not $response) { throw 'Service did not become ready within 60 seconds. Check its log.' }
    if (-not $ready) { throw 'Service is responding, but mounts did not become ready within 60 seconds. Check its log.' }
    Start-Process -FilePath $trayExe -WorkingDirectory $trayDir | Out-Null
    foreach ($mount in $response.Mounts) {
        if ($mount.Error) { Write-Warning "Mount '$($mount.Name)': $($mount.Error)" }
        else { Write-Host "  $($mount.Name): mounted=$($mount.Mounted), $($mount.MountPoint)" }
    }
}

try {
    # Perform preflight before stopping a running instance.
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK was not found in PATH.' }
    if (-not (Test-Path -LiteralPath $serviceProject) -or -not (Test-Path -LiteralPath $trayProject)) {
        throw "PolyMount projects were not found under $repo"
    }
    if ($installed) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
            throw 'An installed Windows service was detected. Run Update.bat as administrator.'
        }
        $registration = Get-CimInstance Win32_Service -Filter "Name='PolyMount'"
        $registeredPath = $registration.PathName.Trim().Trim('"')
        if (-not [string]::Equals($registeredPath, $serviceExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Installed service points to $registeredPath. Select its matching build configuration instead of $Configuration."
        }
    }

    Write-Host "[1/3] Stopping PolyMount ($Configuration)..."
    $running = @(Get-Process -Name 'PolyMount.Service' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        try { Send-Control 'shutdown' | Out-Null }
        catch {
            if ($installed) { Stop-Service -Name 'PolyMount' }
            else { throw 'Service did not accept graceful shutdown; update cancelled. Check the control pipe.' }
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ((Get-Process -Name 'PolyMount.Service' -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 250
        }
        if (Get-Process -Name 'PolyMount.Service' -ErrorAction SilentlyContinue) {
            throw 'Service did not exit within 30 seconds; refusing to overwrite locked binaries.'
        }
    }
    Get-Process -Name 'PolyMount.Tray' -ErrorAction SilentlyContinue | Stop-Process

    Write-Host '[2/3] Publishing service and tray (configuration and layer data are preserved)...'
    foreach ($target in @(@{ Project = $serviceProject; Directory = $serviceDir }, @{ Project = $trayProject; Directory = $trayDir })) {
        if (-not (Test-Path -LiteralPath (Split-Path $target.Directory -Parent))) {
            New-Item -ItemType Directory -Path (Split-Path $target.Directory -Parent) -Force | Out-Null
        }
        & dotnet publish $target.Project -c $Configuration --self-contained false -o $target.Directory --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($target.Project). Service remains stopped." }
    }
    if (-not (Test-Path -LiteralPath $serviceExe) -or -not (Test-Path -LiteralPath $trayExe)) {
        throw 'Published executables were not found.'
    }
    & (Join-Path $PSScriptRoot 'Publish-Plugins.ps1') -Destination (Join-Path $serviceDir 'plugins') -Configuration $Configuration
    Write-Host '[3/3] Restarting service and tray...'
    Start-PolyMount
    Write-Host 'Update complete.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
