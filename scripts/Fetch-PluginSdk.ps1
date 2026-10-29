[CmdletBinding()]
param(
    # Where the packages land. It is the folder nuget.config names as the "polymount" source, so a
    # restore finds them without a network.
    [string]$Destination,
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Destination) { $Destination = Join-Path $repo 'packages' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

# The plugin SDK is not on nuget.org: each release publishes its packages as assets, the way the
# Qt binding does. This downloads the two a plugin needs into the local feed.
$packages = @('PolyMount.Plugin.Abstractions', 'PolyMount.Plugin.Sdk')
$repository = if ($env:POLYMOUNT_REPOSITORY) { $env:POLYMOUNT_REPOSITORY } else { 'ProxyCheng/PolyMount' }
$tag = if ($env:POLYMOUNT_PLUGIN_SDK_TAG) { $env:POLYMOUNT_PLUGIN_SDK_TAG } else { "plugin-sdk-v$Version" }

foreach ($package in $packages) {
    $file = "$package.$Version.nupkg"
    $target = Join-Path $Destination $file
    if (Test-Path $target) { Write-Host "$file already present"; continue }
    $url = "https://github.com/$repository/releases/download/$tag/$file"
    Write-Host "download $url"
    Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
    if (-not (Test-Path $target)) { throw "$file did not download" }
}
Write-Host "plugin SDK packages are in $Destination"
