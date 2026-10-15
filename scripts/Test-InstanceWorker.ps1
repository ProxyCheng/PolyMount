<#
.SYNOPSIS
  Runs one instance as a worker process and drives it over its own pipe.
.DESCRIPTION
  Step 2 of "one process per instance": PolyMount.Service.exe --instance <id> must mount exactly
  that instance, answer for it on PipeMount.Control.<id>, refuse everything that belongs to the
  supervisor or to a different instance, and stop cleanly when it is told to.

  Entirely self-contained: its own configuration, its own store, a "dir" source rather than SVN,
  and a mount point under %TEMP%. Nothing it touches belongs to the installed service.
#>
[CmdletBinding()]
param([string]$ServiceExecutable)

$ErrorActionPreference = 'Stop'
if (-not $ServiceExecutable) {
    $ServiceExecutable = Join-Path $PSScriptRoot '..\src\PolyMount.Service\bin\Debug\net10.0-windows\PolyMount.Service.exe'
}
$ServiceExecutable = (Resolve-Path -LiteralPath $ServiceExecutable).Path

$root = Join-Path $env:TEMP ('PolyMount-worker-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$mount = Join-Path $root 'mount'
$store = Join-Path $root 'store'
New-Item -ItemType Directory -Path $source -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $source 'hello.txt'), 'from-source')

$id = [Guid]::NewGuid().ToString('N')
$pipeBase = 'PolyMount.WorkerTest'
$pipeName = "$pipeBase.$id"
$config = Join-Path $root 'test.json'
@{ ControlPipe = $pipeBase; Mounts = @(@{ Id = $id; Name = 'WorkerSmoke'; MountPoint = $mount; StoreRoot = $store; Layers = @(
    @{Id='overlay';Type='overlay';Compression=$true}, @{Id='dir';Type='dir';Source=@{Type='dir';Path=$source}}
)}) } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $config -Encoding UTF8

function Send([string]$cmd, [hashtable]$fields = @{}) {
    $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(1000)
        $writer = New-Object IO.StreamWriter($pipe); $writer.AutoFlush = $true
        $reader = New-Object IO.StreamReader($pipe)
        $writer.WriteLine(((@{ Cmd = $cmd } + $fields) | ConvertTo-Json -Compress))
        return ($reader.ReadLine() | ConvertFrom-Json)
    }
    finally { $pipe.Dispose() }
}

$process = $null
try {
    $process = Start-Process -FilePath $ServiceExecutable `
        -ArgumentList @('--config', ('"' + $config + '"'), '--instance', $id) -PassThru -WindowStyle Hidden

    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        if ($process.HasExited) { throw "The worker exited at once with code $($process.ExitCode)" }
        try { $status = Send 'status'; if ($status.Mounts[0].Mounted) { $ready = $true; break } } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'The worker did not mount within 10 seconds' }
    if ($status.Mounts.Count -ne 1) { throw "A worker reported $($status.Mounts.Count) instances instead of one" }
    if ($status.Mounts[0].Name -ne 'WorkerSmoke') { throw "The worker reported '$($status.Mounts[0].Name)'" }
    if ($status.Mounts[0].Error) { throw "The worker reported an error: $($status.Mounts[0].Error)" }

    # The mount is real: a write lands in the overlay and reads back through the layers.
    [IO.File]::WriteAllText((Join-Path $mount 'new.txt'), 'written-through-the-worker')
    if ([IO.File]::ReadAllText((Join-Path $mount 'new.txt')) -ne 'written-through-the-worker') { throw 'A file written through the worker did not read back' }
    if ([IO.File]::ReadAllText((Join-Path $mount 'hello.txt')) -ne 'from-source') { throw 'The source file did not read through the worker' }
    if (-not [IO.File]::Exists((Join-Path $source 'hello.txt'))) { throw 'The read-only source was modified' }

    $probe = Send 'probe' @{ Path = (Join-Path $mount 'hello.txt') }
    if (-not $probe.Ok -or -not $probe.Probe.Found) { throw "Probing through the worker failed: $($probe.Error)" }
    if ($probe.Probe.Mount -ne 'WorkerSmoke') { throw "The probe answered for '$($probe.Probe.Mount)'" }
    if ($probe.LayerDescriptors.Count -eq 0 -or $probe.SourceTypes.Count -eq 0) { throw 'The worker did not report the plugins it loaded' }

    $metrics = Send 'metrics' @{ WindowSeconds = 5 }
    if (-not $metrics.Ok -or $metrics.Metrics.Count -ne 1) { throw "The worker did not answer for its own metrics: $($metrics.Error)" }

    # An instance answers for itself and nothing else.
    $wrongName = Send 'status' @{ Name = 'SomeoneElse' }
    if ($wrongName.Ok) { throw 'A worker answered a request that named a different instance' }
    $foreign = Send 'getConfig'
    if ($foreign.Ok) { throw 'A worker answered a command that belongs to the supervisor' }
    if ((Send 'shutdown').Ok -ne $true) { throw 'The worker refused to be told to stop' }
    if (-not $process.WaitForExit(15000)) { throw 'The worker did not exit within 15 seconds of being told to stop' }
    if ($process.ExitCode -ne 0) { throw "The worker exited with code $($process.ExitCode)" }

    'Instance worker smoke passed: mounts one instance on its own pipe, serves reads/writes/probe/metrics through it, refuses another instance and the supervisor, and stops cleanly.'
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(10000) | Out-Null }
    # The mount point is a WinFsp junction: removing it with rmdir takes the link and not the store.
    if (Test-Path -LiteralPath $mount) { cmd /c "rmdir `"$mount`"" 2>$null | Out-Null }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
