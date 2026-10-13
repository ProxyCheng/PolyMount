<#
.SYNOPSIS
  Dev convenience: stop -> (build) -> start service -> start tray, and verify the control pipe.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\Dev-Restart.ps1
  .\Dev-Restart.ps1 -NoBuild            # just restart the running binaries
  .\Dev-Restart.ps1 -StopOnly           # stop everything, don't start
  .\Dev-Restart.ps1 -NoTray             # service only
  .\Dev-Restart.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$StopOnly,
    [switch]$NoTray
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$tfm = "net10.0-windows"
$svcExe = Join-Path $repo "src\PolyMount.Service\bin\$Configuration\$tfm\PolyMount.Service.exe"
$trayExe = Join-Path $repo "src\PolyMount.Tray\bin\$Configuration\$tfm\PolyMount.Tray.exe"

function Stop-Polymount {
    foreach ($n in 'PolyMount.Tray', 'PolyMount.Service') {
        Get-Process -Name $n -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host "stopping $n (pid $($_.Id))"
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Start-Sleep -Seconds 2  # let WinFsp tear down mounts
}

function Test-ControlPipe {
    param([string]$PipeName = "PolyMount.Control")
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(500)
        $w = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false))); $w.AutoFlush = $true
        $r = New-Object System.IO.StreamReader($pipe, [System.Text.Encoding]::UTF8)
        $w.WriteLine('{"Cmd":"status"}')
        $line = $r.ReadLine()
        $pipe.Dispose()
        return $line
    }
    catch { return $null }
}

Stop-Polymount
if ($StopOnly) { Write-Host "stopped." -ForegroundColor Green; return }

if (-not $NoBuild) {
    Write-Host "building ($Configuration)..."
    & dotnet build (Join-Path $repo "PolyMount.slnx") -c $Configuration -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

if (-not (Test-Path $svcExe)) { throw "service exe not found: $svcExe (build first)" }
Write-Host "starting service..."
Start-Process -FilePath $svcExe -WindowStyle Hidden | Out-Null

# Poll the control pipe until the service answers (mounts can take a few seconds).
$ok = $null
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 750
    $ok = Test-ControlPipe
    if ($ok) { break }
}
if ($ok) { Write-Host "service is up (control pipe answering)." -ForegroundColor Green }
else { Write-Warning "service did not answer the control pipe within ~15s; check its log." }

if (-not $NoTray) {
    if (-not (Test-Path $trayExe)) { throw "tray exe not found: $trayExe (build first)" }
    Write-Host "starting tray..."
    Start-Process -FilePath $trayExe | Out-Null
}

Write-Host "done." -ForegroundColor Green
