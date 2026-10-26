[CmdletBinding()]
param([string]$Version = '0.2.0', [string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Destination) { $Destination = Join-Path $repo 'packages' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

# Neither package is on nuget.org: each release publishes the .nupkg files plus SHA256SUMS.txt as
# GitHub Release assets. nuget.config points a "qtsharp" source at this folder, so restore needs
# every package here under its exact name. Since 0.2.0 the binding is split the way PyQt6 is:
# QtSharp.Api (ten module assemblies) depends on QtSharp.Runtime (managed runtime + native glue)
# and QtSharp.Qt6 (the Qt runtime and platform plugin), and QtSharp.Charts depends on
# QtSharp.Charts.Qt6 (Qt6Charts.dll). Restore resolves all five, so all five must be here.
$base = "https://github.com/ProxyCheng/QtSharp/releases/download/v$Version"
$names = @('QtSharp.Api', 'QtSharp.Runtime', 'QtSharp.Qt6', 'QtSharp.Charts', 'QtSharp.Charts.Qt6')

# The checksums the release published. Pinning them here means a present package is verified
# without the network, and a download is verified against what the release actually claims.
$published = @{
    '0.2.0' = @{
        # The first split release: Api is now ten module assemblies and the Qt runtime moved to
        # QtSharp.Qt6, so a NuGet cache holding 0.1.0 is a different package graph entirely.
        'QtSharp.Api'         = 'c747093ab8cc25e55bb53e804b8bbe5a89d3b8754c336a34023a387029a21cd6'
        'QtSharp.Runtime'     = '876cff726026d2708d142c3b81918efd41fca1f7b74315b4a55c60263bd417a2'
        'QtSharp.Qt6'         = '1ae3c62044bcbcd4f75fd693d468bb6bd01528cbcf111605b2ba71e8336e31b9'
        'QtSharp.Charts'      = '96c46b9ff4baea53650d45ca67f09a67f3f3f94e5dbe3f38bd05543d43b4b641'
        'QtSharp.Charts.Qt6'  = '364f64fd85357923d37704498bd813f7f66ade7afae47ef177b89366c4d1d5fc'
    }
}

function Get-PublishedHashes([string]$version) {
    if ($published.ContainsKey($version)) { return $published[$version] }
    $sums = Join-Path $env:TEMP "QtSharp.SHA256SUMS.$version.txt"
    # curl.exe rather than Invoke-WebRequest: Schannel on this machine fails the revocation check
    # against these hosts, and -ssl-revoke-best-effort is the workaround (Build-Installer.ps1 reads
    # its own dependencies the same way).
    & curl.exe -sS --ssl-revoke-best-effort -fL --retry 3 --output $sums "$base/SHA256SUMS.txt"
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch $base/SHA256SUMS.txt" }
    $hashes = @{}
    foreach ($line in Get-Content -LiteralPath $sums) {
        if ($line -match '^([0-9a-f]{64})\s+(.+\.nupkg)$') { $hashes[$Matches[2]] = $Matches[1] }
    }
    Remove-Item -LiteralPath $sums -Force
    $result = @{}
    foreach ($name in $names) {
        $file = "$name.$version.nupkg"
        if (-not $hashes.ContainsKey($file)) { throw "SHA256SUMS.txt on the v$version release has no entry for $file" }
        $result[$name] = $hashes[$file]
    }
    return $result
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
    & curl.exe -sS --ssl-revoke-best-effort -fL --retry 3 --output $file $url
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch $url" }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Remove-Item -LiteralPath $file -Force
        throw "Checksum mismatch for $name - expected $expected, got $actual."
    }
    Write-Host "$name -> $file"
}

$hashes = Get-PublishedHashes $Version
foreach ($name in $names) {
    Get-Package "$name $Version" $hashes[$name] `
        "$base/$name.$Version.nupkg" (Join-Path $Destination "$name.$Version.nupkg")
}
