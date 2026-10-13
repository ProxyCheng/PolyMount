[CmdletBinding()]
param([string]$Workers = '1,4,8', [int]$Repeats = 3, [int]$FileMB = 8, [int]$SmallFiles = 32, [string]$MountRoot)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$report = Join-Path $repo 'artifacts\performance'
$arguments = @('run','--project',(Join-Path $repo 'tests\PolyMount.Performance'),'--configuration','Release','--',
    '--workers',$Workers,'--repeats',"$Repeats",'--file-mb',"$FileMB",'--small-files',"$SmallFiles",'--report',$report)
if ($MountRoot) { $arguments += @('--mount-root',$MountRoot) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Performance run failed; see generated report.' }
