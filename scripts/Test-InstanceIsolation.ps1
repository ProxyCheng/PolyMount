<#
.SYNOPSIS
  Checks that one instance's worker cannot take another instance down with it.
.DESCRIPTION
  The promise of one process per instance: killing the process behind one mount must not disturb
  another mount, and the supervisor must notice, say so, and bring it back. This drives the running
  service — it needs at least two mounted instances — and writes only under the mounts themselves.
#>
[CmdletBinding()]
param(
    [string]$PipeName = 'PolyMount.Control',
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'

function Send-Control([string]$Command, [string]$TargetPipe = $PipeName) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $TargetPipe, [System.IO.Pipes.PipeDirection]::InOut)
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

function Get-Status([string]$Name) {
    $status = Send-Control 'status'
    $row = @($status.Mounts | Where-Object { $_.Name -eq $Name })
    if ($row.Count -ne 1) { throw "No instance named '$Name' in the status." }
    return $row[0]
}

$instances = @(Send-Control 'instances').Instances
if ($instances.Count -lt 2) { throw "This check needs two mounted instances; the service is running $($instances.Count)." }
$survivor = $instances[0]
$victim = $instances[1]
Write-Output "Survivor: $($survivor.Name) at $($survivor.MountPoint) (pid $($survivor.WorkerPid))"
Write-Output "Victim:   $($victim.Name) at $($victim.MountPoint) (pid $($victim.WorkerPid))"

# Both mounts serve reads before anything is killed, so a later failure is what the kill caused.
foreach ($instance in @($survivor, $victim)) {
    $entries = @(Get-ChildItem -LiteralPath $instance.MountPoint -ErrorAction Stop)
    if ($entries.Count -eq 0) { throw "$($instance.Name) lists nothing before the kill." }
}

$marker = Join-Path $survivor.MountPoint 'polymount-isolation.txt'
$expected = 'alive-' + [Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($marker, $expected)
if ([IO.File]::ReadAllText($marker) -ne $expected) { throw 'The survivor could not be written to before the kill.' }

Write-Output "Killing $($victim.Name)'s worker (pid $($victim.WorkerPid))..."
Stop-Process -Id $victim.WorkerPid -Force

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$sawDown = $false
$sawRestart = $false
$rounds = 0
while ([DateTime]::UtcNow -lt $deadline -and -not $sawRestart) {
    $rounds++

    # The survivor is read and written on every round: the point is that it keeps serving while the
    # other instance's process is gone and being replaced.
    $value = "round-$rounds"
    [IO.File]::WriteAllText($marker, $value)
    if ([IO.File]::ReadAllText($marker) -ne $value) { throw "The survivor's mount stopped serving in round $rounds." }

    $survivorNow = Get-Status $survivor.Name
    if ($survivorNow.WorkerPid -ne $survivor.WorkerPid) {
        throw "The survivor was restarted too: pid $($survivor.WorkerPid) became $($survivorNow.WorkerPid)."
    }

    $victimNow = Get-Status $victim.Name
    if (-not $victimNow.Mounted -or $victimNow.Error) {
        $sawDown = $true
        if (-not $sawDown) { Write-Output 'down' }
    }
    if ($victimNow.Restarts -gt 0 -and $victimNow.Mounted -and $victimNow.WorkerPid -ne $victim.WorkerPid) {
        $sawRestart = $true
        Write-Output "The supervisor reported $($victim.Name) down ($($victimNow.Error)) and started it again as pid $($victimNow.WorkerPid) after $rounds round(s)."
    }
    elseif (-not $sawDown -and $victimNow.Restarts -gt 0) {
        $sawDown = $true
    }

    if (-not $sawRestart) { Start-Sleep -Milliseconds 250 }
}

if (-not $sawDown) { throw "The supervisor never reported $($victim.Name) as down after its worker was killed." }
if (-not $sawRestart) { throw "$($victim.Name)'s worker was not started again within $TimeoutSeconds seconds." }

# And the instance that was killed is serving again, on its own mount.
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$serving = $false
while ([DateTime]::UtcNow -lt $deadline -and -not $serving) {
    try { $serving = @(Get-ChildItem -LiteralPath $victim.MountPoint -ErrorAction Stop).Count -gt 0 }
    catch { $serving = $false }
    if (-not $serving) { Start-Sleep -Milliseconds 500 }
}
if (-not $serving) { throw "$($victim.Name) is running again but its mount serves nothing." }

Remove-Item -LiteralPath $marker -Force
Write-Output "Isolation passed: $($victim.Name) was killed, reported down and restarted as pid $((Get-Status $victim.Name).WorkerPid); $($survivor.Name) served reads and writes throughout on pid $($survivor.WorkerPid)."
