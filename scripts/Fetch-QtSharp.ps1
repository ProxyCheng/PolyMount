[CmdletBinding()]
param([string]$Version = '0.17.0', [string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Destination) { $Destination = Join-Path $repo 'packages' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

# The QtSharp packages are not on nuget.org: each release publishes the .nupkg files as GitHub
# Release assets, and nuget.config points a "qtsharp" source at this folder, so restore needs
# every package here under its exact name. The binding is split the way PyQt6 is: QtSharp.Api
# (nine module assemblies since 0.13.0 dropped QtXml) depends on QtSharp.Runtime (managed runtime
# and native glue) and QtSharp.Qt6 (the Qt 6.12 runtime and platform plugin), and QtSharp.Charts
# depends on QtSharp.Charts.Qt6 (Qt6Charts.dll). Restore resolves all five, so all five must be
# here.
#
# A version is published once and never republished, so a package that is already here is the one
# that release served: the script downloads what is missing and leaves the rest alone.
$base = "https://github.com/ProxyCheng/QtSharp/releases/download/v$Version"
$names = @('QtSharp.Api', 'QtSharp.Runtime', 'QtSharp.Qt6', 'QtSharp.Charts', 'QtSharp.Charts.Qt6')

foreach ($name in $names) {
    $file = Join-Path $Destination "$name.$Version.nupkg"
    if (Test-Path -LiteralPath $file) {
        Write-Host "$name $Version is already here: $file"
        continue
    }
    # curl.exe rather than Invoke-WebRequest: Schannel on this machine fails the revocation check
    # against these hosts, and -ssl-revoke-best-effort is the workaround (Build-Installer.ps1 reads
    # its own dependencies the same way).
    & curl.exe -sS --ssl-revoke-best-effort -fL --retry 3 --output $file "$base/$name.$Version.nupkg"
    if ($LASTEXITCODE -ne 0) {
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        throw "Could not fetch $base/$name.$Version.nupkg"
    }
    Write-Host "$name -> $file"
}
