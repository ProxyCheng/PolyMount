[CmdletBinding()]
param([string]$Version = '0.1.0', [string]$Destination, [string]$ChartsVersion = '0.1.0')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Destination) { $Destination = Join-Path $repo 'packages' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

# Neither package is on nuget.org: each release publishes the .nupkg plus SHA256SUMS.txt as GitHub
# Release assets. nuget.config points a "qtsharp" source at this folder, so restore needs both
# files here under their exact package names - the binding, and the charts module beside it.
$base = "https://github.com/ProxyCheng/QtSharp/releases/download/v$Version"
$package = Join-Path $Destination "QtSharp.Api.$Version.nupkg"

# The checksums the release published. Pinning them here means a present package is verified
# without the network, and a download is verified against what the release actually claims.
$published = @{
    # QtSharp.Api 0.1.0 was published more than once under the same version, and the release
    # asset is what a fresh clone downloads: this is the one it currently serves (its Qt kit
    # carries Qt6Charts.dll, which the charts package links against). A machine whose NuGet
    # cache still holds an earlier build of the same version keeps those bytes until the cache
    # entry is cleared - `dotnet nuget locals global-packages --list` says where.
    '0.1.0' = 'a7173247a5a765513db619ffa3419dd56c30dce0bf3ef43eb7f413ecf0cfee55'
}

# QtSharp.Charts is a separate release repository with its own checksums file.
$chartsPublished = @{
    '0.1.0' = 'c8211b94e6066e522fa90c057e3ef9df55489a7bac9de5fd689a5d3e0d0dbe40'   # repackaged 2026-10-16 with the legend and scene-item interfaces
}

function Get-PublishedHash([string]$version) {
    if ($published.ContainsKey($version)) { return $published[$version] }
    $sums = Join-Path $env:TEMP "QtSharp.SHA256SUMS.$version.txt"
    # curl.exe rather than Invoke-WebRequest: Schannel on this machine fails the revocation check
    # against these hosts, and -ssl-revoke-best-effort is the workaround (Build-Installer.ps1 reads
    # its own dependencies the same way).
    & curl.exe --ssl-revoke-best-effort -fL --retry 3 --output $sums "$base/SHA256SUMS.txt"
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch $base/SHA256SUMS.txt" }
    $line = Select-String -LiteralPath $sums -Pattern "  QtSharp\.Api\.$([regex]::Escape($version))\.nupkg$" | Select-Object -First 1
    Remove-Item -LiteralPath $sums -Force
    if (-not $line) { throw "SHA256SUMS.txt on the v$version release has no entry for QtSharp.Api.$version.nupkg" }
    return ($line.Line -split '\s+')[0].ToLowerInvariant()
}

function Get-Package([string]$name, [string]$expected, [string]$url, [string]$file) {
    if (Test-Path -LiteralPath $file) {
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -eq $expected) {
            Write-Host "$name is already here: $file"
            return
        }
        Write-Host "$name does not match the published checksum - fetching it again."
    }
    & curl.exe --ssl-revoke-best-effort -fL --retry 3 --output $file $url
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch $url" }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Remove-Item -LiteralPath $file -Force
        throw "Checksum mismatch for $name - expected $expected, got $actual."
    }
    Write-Host "$name -> $file"
}

Get-Package "QtSharp.Api $Version" (Get-PublishedHash $Version) `
    "$base/QtSharp.Api.$Version.nupkg" $package
Get-Package "QtSharp.Charts $ChartsVersion" $chartsPublished[$ChartsVersion] `
    "https://github.com/ProxyCheng/QtSharp.Charts/releases/download/v$ChartsVersion/QtSharp.Charts.$ChartsVersion.nupkg" `
    (Join-Path $Destination "QtSharp.Charts.$ChartsVersion.nupkg")
