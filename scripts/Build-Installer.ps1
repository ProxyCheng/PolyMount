[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '0.2.0', [string]$Compiler)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repo 'artifacts'
$payload = Join-Path $artifacts 'installer-payload'
if (-not $Compiler) {
    foreach ($candidate in @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")) {
        if (Test-Path -LiteralPath $candidate) { $Compiler = $candidate; break }
    }
}
if (-not $Compiler) { throw 'Install Inno Setup 6 (winget install JRSoftware.InnoSetup) or pass -Compiler.' }
# Staged from empty every time: this directory is reused across builds, and a payload that is only
# added to ships whatever a previous build left behind -- a file dropped from the product would
# still be in the installer.
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null
foreach ($project in @('Service','Tray')) {
    $output = Join-Path $payload $project
    & dotnet publish (Join-Path $repo "src\PolyMount.$project\PolyMount.$project.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=$Version -o $output --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "$project publish failed." }
    # The Qt binding, its native glue and the Qt runtime DLLs arrive with the QtSharp.Api package,
    # which copies them into the publish output (buildTransitive targets) - no local QtSharp clone
    # is involved. The MSVC runtime is not part of it; the setup installs vc_redist.x64.exe below.
    # A distribution must not contain this workstation's repository config.
    Remove-Item -LiteralPath (Join-Path $output 'config.json') -Force -ErrorAction SilentlyContinue
}
& (Join-Path $PSScriptRoot 'Publish-Plugins.ps1') -Destination (Join-Path $payload 'Service\plugins') -Configuration Release
& (Join-Path $PSScriptRoot 'Build-Shell.ps1') -OutputDirectory (Join-Path $payload 'Shell')
$dependencies = Join-Path $payload 'Dependencies'
New-Item -ItemType Directory -Path $dependencies -Force | Out-Null
foreach ($download in @(
    @{ File = 'winfsp.msi'; Url = 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi' },
    @{ File = 'vc_redist.x64.exe'; Url = 'https://aka.ms/vs/17/release/vc_redist.x64.exe' }
)) {
    $destination = Join-Path $dependencies $download.File
    if (-not (Test-Path -LiteralPath $destination)) {
        & curl.exe --ssl-revoke-best-effort -fL --retry 3 --output $destination $download.Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $($download.Url)" }
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $destination
    if ($signature.Status -ne 'Valid') { throw "Dependency signature validation failed: $destination ($($signature.Status))" }
}
# The design notes are part of the working repository, not necessarily of the product: a tree
# published without them still has a README and the third-party notices.
$docs = Join-Path $repo 'docs'
if (Test-Path -LiteralPath $docs) {
    $docDir = Join-Path $payload 'docs'
    New-Item -ItemType Directory -Path $docDir -Force | Out-Null
    Copy-Item -Path (Join-Path $docs '*.md') -Destination $docDir
}
& $Compiler /Qp "/DAppVersion=$Version" "/DPayloadDir=$payload" "/DOutputDir=$artifacts" (Join-Path $repo 'installer\PolyMount.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $artifacts "PolyMount-$Version-win-x64-Setup.exe"
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath ($installer + '.sha256') -Encoding ASCII
Write-Host "Installer ready: $installer"
