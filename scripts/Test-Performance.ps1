[CmdletBinding()]
param([string]$Workers = '1,4,8,32,64', [int]$Repeats = 3, [int]$FileMB = 8, [int]$SmallFiles = 32, [string]$MountRoot,
    [int]$EngineDirectories = 128, [int]$EngineFiles = 16, [int]$EngineRounds = 2,
    [int]$RemoteDelayMs = 5, [switch]$EngineOnly, [switch]$EngineBreakdown)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$report = Join-Path $repo 'artifacts\performance'
$arguments = @('run','--project',(Join-Path $repo 'tests\PolyMount.Performance'),'--configuration','Release','--',
    '--workers',$Workers,'--repeats',"$Repeats",'--file-mb',"$FileMB",'--small-files',"$SmallFiles",'--report',$report,
    '--engine-directories',"$EngineDirectories",'--engine-files',"$EngineFiles",'--engine-rounds',"$EngineRounds",'--remote-delay-ms',"$RemoteDelayMs")
if ($EngineOnly) { $arguments += '--engine-only' }
if ($EngineBreakdown) { $arguments += '--engine-breakdown' }
if ($MountRoot) { $arguments += @('--mount-root',$MountRoot) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Performance run failed; see generated report.' }
