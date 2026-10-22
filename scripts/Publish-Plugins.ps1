[CmdletBinding()]
param([string]$Destination, [ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$published = @()
foreach ($project in Get-ChildItem -Path (Join-Path $repo 'src\plugins\*\*.csproj')) {
    & dotnet publish $project.FullName -c $Configuration --self-contained false -o $Destination --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Plugin publish failed: $($project.Name)" }
    $published += $project.BaseName
}
# A plugin removed from the tree has to leave the deployment too, or the host keeps loading the
# copy it was never told to stop loading. Only the plugin family this script owns is swept, and
# only after the publish succeeded, so a failed build cannot take the working set with it.
Get-ChildItem -LiteralPath $Destination -File -Filter 'PolyMount.Plugin.*' | Where-Object {
    $name = $_.Name
    -not ($published | Where-Object { $name -like ($_ + '.*') })
} | Remove-Item -Force
# Shared contracts are supplied by the host. Test dependencies never come from src/plugins.
Get-ChildItem -LiteralPath $Destination -File | Where-Object {
    $_.Name -eq 'PolyMount.Core.dll' -or $_.Name -eq 'PolyMount.Core.pdb' -or $_.Name -like '*.Tests.*' -or $_.Name -like 'PolyMount.Plugin.Testing.*'
} | Remove-Item -Force
