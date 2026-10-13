[CmdletBinding()]
param([string]$Destination, [ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
foreach ($project in Get-ChildItem -Path (Join-Path $repo 'src\plugins\*\*.csproj')) {
    & dotnet publish $project.FullName -c $Configuration --self-contained false -o $Destination --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Plugin publish failed: $($project.Name)" }
}
# Shared contracts are supplied by the host. Test dependencies never come from src/plugins.
Get-ChildItem -LiteralPath $Destination -File | Where-Object {
    $_.Name -eq 'PolyMount.Core.dll' -or $_.Name -eq 'PolyMount.Core.pdb' -or $_.Name -like '*.Tests.*' -or $_.Name -like 'PolyMount.Plugin.Testing.*'
} | Remove-Item -Force
