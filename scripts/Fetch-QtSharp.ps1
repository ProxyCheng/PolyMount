[CmdletBinding()]
param([string]$Version = '0.1.0', [string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Destination) { $Destination = Join-Path $repo 'packages' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

# QtSharp.Api is not on nuget.org: each release publishes the .nupkg plus SHA256SUMS.txt as GitHub
# Release assets. nuget.config points a "qtsharp" source at this folder, so restore needs the file
# to be here under its exact package name.
$base = "https://github.com/ProxyCheng/QtSharp/releases/download/v$Version"
$package = Join-Path $Destination "QtSharp.Api.$Version.nupkg"

# The checksums the release published. Pinning them here means a present package is verified
# without the network, and a download is verified against what the release actually claims.
$published = @{
    '0.1.0' = '5b07d37a15c150b5443813fa59c46b224e62415c8d399630188484eb952ad08d'
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

$expected = Get-PublishedHash $Version
if (Test-Path -LiteralPath $package) {
    $actual = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -eq $expected) {
        Write-Host "QtSharp.Api $Version is already here: $package"
        return
    }
    Write-Host "QtSharp.Api $Version does not match the published checksum - fetching it again."
}
& curl.exe --ssl-revoke-best-effort -fL --retry 3 --output $package "$base/QtSharp.Api.$Version.nupkg"
if ($LASTEXITCODE -ne 0) { throw "Could not fetch $base/QtSharp.Api.$Version.nupkg" }
$actual = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) {
    Remove-Item -LiteralPath $package -Force
    throw "Checksum mismatch for QtSharp.Api.$Version.nupkg - expected $expected, got $actual."
}
Write-Host "QtSharp.Api $Version -> $package"
