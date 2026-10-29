[CmdletBinding()]
param([string]$Version, [string]$Destination, [string]$Platform = 'win-x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Destination) { $Destination = Join-Path $repo 'packages' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

# Which version to fetch is not a decision this script makes: it is whatever the tray's project
# restores, read from its PackageReference. A default here is a second copy of the number, and it
# drifts - 0.9.4's CI fetched 0.20.0 into a tree that asked restore for 0.20.1 and failed with
# NU1102. An explicit -Version still wins, for trying a release the project does not name yet.
if (-not $Version) {
    $project = Join-Path $repo 'src\PolyMount.Tray\PolyMount.Tray.csproj'
    $pattern = 'PackageReference\s+Include="QtSharp\.Api\.' + [regex]::Escape($Platform) + '"\s+Version="([^"]+)"'
    $reference = Select-String -Path $project -Pattern $pattern
    if (-not $reference) { throw "no QtSharp.Api.$Platform PackageReference in $project; pass -Version" }
    $Version = $reference.Matches[0].Groups[1].Value
}

# The QtSharp packages are not on nuget.org: a release publishes one zip per platform, each holding
# that platform's .nupkg files, and nuget.config points a "qtsharp" source at this folder, so restore
# needs every package here under its exact name.
#
# Since 0.19.0 a package is per platform with the RID in its id (QtSharp.Api.win-x64,
# QtSharp.Qt6.win-x64, ...): the managed assemblies are the same in every variant, and only the
# matching Qt runtime and native glue are inside. That is why the release is fetched as a platform
# zip rather than as five loose packages - 0.18.0 and earlier put both payloads in one 96 MB
# package. The zip also carries variants this product does not use (Qt3D, Multimedia, Positioning,
# Sql, Charts.Qt6); they are extracted anyway, because the folder is a NuGet source and a package
# that is not there is a restore that has to find it later.
#
# A version is published once and never republished, so a zip that is already unpacked is the one
# that release served: the script fetches it only when something is missing.
$base = "https://github.com/ProxyCheng/QtSharp/releases/download/v$Version"
$zip = Join-Path $Destination "QtSharp-$Version-$Platform.zip"

$have = Get-ChildItem -LiteralPath $Destination -Filter "*.$Platform.$Version.nupkg" -File -ErrorAction SilentlyContinue
if ($have.Count -gt 0 -and (Test-Path -LiteralPath (Join-Path $Destination "QtSharp.Api.$Platform.$Version.nupkg"))) {
    Write-Host "QtSharp $Version for $Platform is already here: $($have.Count) package(s) in $Destination"
    return
}

# curl.exe rather than Invoke-WebRequest: Schannel on this machine fails the revocation check
# against these hosts, and -ssl-revoke-best-effort is the workaround (Build-Installer.ps1 reads
# its own dependencies the same way).
& curl.exe -sS --ssl-revoke-best-effort -fL --retry 3 --output $zip "$base/QtSharp-$Version-$Platform.zip"
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    throw "Could not fetch $base/QtSharp-$Version-$Platform.zip"
}

# The archive is data: its entries are checked to be plain .nupkg files at the root before anything
# is written, so a renamed or repacked release cannot place a file somewhere else.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $packages = @($archive.Entries | Where-Object {
        $_.Name.EndsWith('.nupkg', [StringComparison]::OrdinalIgnoreCase) -and
        $_.FullName -eq $_.Name
    })
    if ($packages.Count -eq 0) { throw "$($archive.Entries.Count) entries in $zip, none of them a .nupkg at the root" }
    foreach ($entry in $packages) {
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $Destination $entry.Name), $true)
        Write-Host "  $($entry.Name)"
    }
    Write-Host "QtSharp $Version for $Platform -> $Destination ($($packages.Count) package(s))"
}
finally { $archive.Dispose() }
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
