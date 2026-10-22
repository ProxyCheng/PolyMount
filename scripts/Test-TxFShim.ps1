[CmdletBinding()]
param(
    # A mounted instance to probe, for example D:\PolyMountInstances\demo. Defaults to the first
    # mounted instance found.
    [string]$Mount,
    [switch]$KeepProbeFile
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$native = Join-Path $repo 'src\plugins\PolyMount.Plugin.TxFShim\native'
$probe = Join-Path $native 'probe\PolyMount.TxFShim.Probe.exe'
$launcher = Join-Path $native 'PolyMount.TxFShim.exe'
if (-not (Test-Path $probe)) { throw "Build the shim first: scripts\Build-TxFShim.ps1 (missing $probe)" }

if (-not $Mount) {
    $Mount = Get-ChildItem 'D:\PolyMountInstances' -Directory -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Mount) { throw 'No mount to probe; pass -Mount <path>.' }

# The probe works in a folder and creates its own names inside it, one per process; that is what
# makes "which process did this" readable in the log.
$target = Join-Path $Mount ('txf-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $target | Out-Null
Write-Host "probe folder: $target"

# When the plugin has installed an Image File Execution Options value for the probe, every launch of
# it - direct included - already goes through the shim, so the interesting check is that one.
$ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PolyMount.TxFShim.Probe.exe'
$installed = Test-Path $ifeo
$failures = @()

if ($installed) {
    $byName = & $probe $target 2>&1 | Out-String
    Write-Host "by name: $($byName.Trim())"
    if ($LASTEXITCODE -ne 0) { $failures += "the run started through the debugger value failed: $($byName.Trim())" }
    if ($byName -notmatch 'shim: loaded') { $failures += 'the debugger value did not reach the shim' }
    if ($byName -notmatch 'all ok') { $failures += 'the run started through the debugger value did not complete the family' }
} else {
    # Direct: the mount does not advertise transactions, so this is the error the engine reports.
    $direct = & $probe $target 2>&1 | Out-String
    $directCode = $LASTEXITCODE
    Write-Host "direct: $($direct.Trim())"
    if ($directCode -eq 0) { $failures += 'the direct run succeeded: this mount already accepts transactional opens, so the probe proves nothing' }
    elseif ($direct -notmatch 'open failed: (\d+)') { $failures += "the direct run failed without an error code: $($direct.Trim())" }
    elseif ($Matches[1] -ne '6832') { $failures += "the direct run failed with $($Matches[1]); 6832 is what a mount without transaction support answers" }

    # Through the launcher: the same call, with the shim in place before the program runs.
    $shimmed = & $launcher $probe $target 2>&1 | Out-String
    Write-Host "shimmed: $($shimmed.Trim())"
    if ($LASTEXITCODE -ne 0) { $failures += "the shimmed run failed (exit $LASTEXITCODE): $($shimmed.Trim())" }
    if ($shimmed -notmatch 'shim: loaded') { $failures += 'the shimmed run did not have the shim loaded' }
    if ($shimmed -notmatch 'all ok') { $failures += 'the shimmed run did not complete the whole API family' }
}

# The log line is the evidence that the call was seen rather than merely accepted.
$log = Join-Path $env:ProgramData 'PolyMount\txf-shim\hits.log'
if (Test-Path $log) {
    $hit = Select-String -LiteralPath $log -Pattern $target -SimpleMatch -ErrorAction SilentlyContinue
    if (-not $hit) { $failures += "no hit for this probe in $log" }
    else { Write-Host "log: $($hit[-1].Line)" }
} else {
    Write-Warning "no hit log at $log; the shim ran but did not record the call (the folder may not be writable by this account)"
}

if (-not $KeepProbeFile) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue }

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "FAILED: $failure" -ForegroundColor Red }
    exit 1
}
Write-Host 'PASSED' -ForegroundColor Green
exit 0
