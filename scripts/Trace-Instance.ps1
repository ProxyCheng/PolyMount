<#
.SYNOPSIS
  Watch or profile one instance from outside its process, with the tools Microsoft ships.
.DESCRIPTION
  PolyMount publishes its numbers as an EventSource named "PolyMount", and its per-layer CPU comes
  from the runtime's own sampling profiler. Neither needs anything switched on inside the process:
  a counter poll pulls, and a sampling profile reads. That is the point of doing it this way — the
  mount pays nothing for being measurable, which is the opposite of measuring it from within.

  -Counters  live numbers, once a second, until Ctrl+C (dotnet-counters)
  -Profile   N seconds of sampled .NET thread stacks into a .nettrace, then the top methods by CPU
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Instance,
    [string]$PipeName = 'PolyMount.Control',
    [switch]$Counters,
    [switch]$Profile,
    [int]$Seconds = 10,
    [string]$OutputDirectory = (Join-Path $env:TEMP 'polymount-traces')
)

$ErrorActionPreference = 'Stop'
if (-not $Counters -and -not $Profile) { throw 'Choose -Counters or -Profile.' }

function Send-Control([string]$Command, [string]$TargetPipe = $PipeName) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $TargetPipe, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(1000)
        $writer = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($pipe, [System.Text.Encoding]::UTF8)
        $writer.WriteLine((@{ Cmd = $Command } | ConvertTo-Json -Compress))
        $reply = $reader.ReadLineAsync()
        if (-not $reply.Wait(5000)) { throw 'Control pipe response timed out.' }
        return ($reply.Result | ConvertFrom-Json)
    }
    finally { $pipe.Dispose() }
}

# An instance is a process of its own, so the pid to watch is the one the supervisor reports for it:
# there is nothing to attribute afterwards, which is the whole reason for the split.
$served = @(Send-Control 'instances').Instances
$endpoint = @($served | Where-Object { $_.Name -eq $Instance })
if ($endpoint.Count -ne 1) {
    $seen = (($served | ForEach-Object { $_.Name }) -join ', ')
    throw "The service is not running an instance named '$Instance'. It reports: $seen"
}
$worker = $endpoint[0].WorkerPid
Write-Host "$Instance is served by pid $worker on $($endpoint[0].Pipe)"

if ($Counters) {
    Write-Host 'Live counters (Ctrl+C to stop). Nothing is collected until this asks; asking is what turns it on.'
    & dotnet-counters monitor --counters PolyMount -p $worker
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }
$trace = Join-Path $OutputDirectory ("$Instance-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.nettrace')
Write-Host "Sampling CPU for $Seconds s into $trace ..."
# --show-child-io is only for launching the process; attaching to one refuses it.
& dotnet-trace collect --profile dotnet-sampled-thread-time --duration "00:00:$('{0:00}' -f $Seconds)" -p $worker -o $trace
if ($LASTEXITCODE -ne 0) { throw 'dotnet-trace failed.' }

# The answer to "what does each layer cost" without timing a single layer call: the samples land in
# the methods themselves, so the layer that is spending the CPU names itself.
#
# The profile name is `dotnet-sampled-thread-time` on Windows - the runtime's own stack sampler at
# about 100 Hz. (`cpu-sampling` is the Linux kernel's, and `dotnet-trace` refuses it here.)
Write-Host ''
Write-Host 'Top methods by CPU (exclusive), which is where each layer shows up:'
& dotnet-trace report $trace topN -n 25
Write-Host ''
Write-Host "For a flame graph: dotnet-trace convert $trace --format Speedscope"
