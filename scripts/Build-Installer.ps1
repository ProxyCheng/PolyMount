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
New-Item -ItemType Directory -Path $payload -Force | Out-Null
foreach ($project in @('Service','Tray')) {
    $output = Join-Path $payload $project
    & dotnet publish (Join-Path $repo "src\PolyMount.$project\PolyMount.$project.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=$Version -o $output --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "$project publish failed." }
    if ($project -eq 'Tray') {
        $qt = 'D:\QtSharp'
        Copy-Item -LiteralPath (Join-Path $qt 'out\api\QtSharp.Api.dll') -Destination $output
        Copy-Item -Path (Join-Path $qt 'Qt\6.11.0\runtime\*.dll') -Destination $output
        New-Item -ItemType Directory -Path (Join-Path $output 'platforms') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $qt 'Qt\6.11.0\runtime\platforms\qwindows.dll') -Destination (Join-Path $output 'platforms')
    }
    # A distribution must not contain this workstation's repository config.
    Remove-Item -LiteralPath (Join-Path $output 'config.json') -Force -ErrorAction SilentlyContinue
}
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
$docDir = Join-Path $payload 'docs'
New-Item -ItemType Directory -Path $docDir -Force | Out-Null
Copy-Item -Path (Join-Path $repo 'docs\*.md') -Destination $docDir
& $Compiler /Qp "/DAppVersion=$Version" "/DPayloadDir=$payload" "/DOutputDir=$artifacts" (Join-Path $repo 'installer\PolyMount.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $artifacts "PolyMount-$Version-win-x64-Setup.exe"
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath ($installer + '.sha256') -Encoding ASCII
Write-Host "Installer ready: $installer"
