[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '0.2.0',
    [string]$Compiler,
    # Code signing. A certificate in the current user's store is named by thumbprint (an EV
    # token installs one); a PFX is imported for the duration of the build so its password never
    # reaches a command line. The environment variables are how the batch file passes them.
    [string]$SignThumbprint = $env:POLYMOUNT_SIGN_THUMBPRINT,
    [string]$SignPfx = $env:POLYMOUNT_SIGN_PFX,
    [string]$SignPassword = $env:POLYMOUNT_SIGN_PASSWORD,
    [string]$TimestampUrl = $(if ($env:POLYMOUNT_TIMESTAMP_URL) { $env:POLYMOUNT_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }),
    [string]$SignToolPath
)
$ErrorActionPreference = 'Stop'
# The default above reads POLYMOUNT_SIGN_PASSWORD, which is the documented path: a password passed
# as a parameter is on this process's command line while the build runs, where the process
# inventory and any command-line telemetry can see it.
if ($PSBoundParameters.ContainsKey('SignPassword') -and $SignPassword) {
    Write-Warning 'The signing password was passed as a parameter and is visible on this command line; prefer POLYMOUNT_SIGN_PASSWORD.'
}
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
foreach ($project in @('Service','Tray','Mcp')) {
    $output = Join-Path $payload $project
    & dotnet publish (Join-Path $repo "src\PolyMount.$project\PolyMount.$project.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=$Version -o $output --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "$project publish failed." }
    # The Qt binding, its native glue and the Qt runtime DLLs arrive with the QtSharp.Api package,
    # which copies them into the publish output (buildTransitive targets) - no local QtSharp clone
    # is involved. The MSVC runtime is not part of it; the setup installs vc_redist.x64.exe below.
    # A distribution must not contain this workstation's repository config.
    Remove-Item -LiteralPath (Join-Path $output 'config.json') -Force -ErrorAction SilentlyContinue
}
# The TxF shim's native half is compiled rather than checked in (it links its CRT statically so it
# can run inside a program carrying any MSVC runtime of its own). A machine without the C++ tools
# still produces a setup: the plugin then ships without the payload it needs and refuses to
# instrument anything, which is a visible refusal rather than a shim that cannot start a process.
try {
    & (Join-Path $PSScriptRoot 'Build-TxFShim.ps1')
} catch {
    Write-Warning "The TxF shim's native half was not built: $($_.Exception.Message)"
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
        # -sS: no progress meter (Windows PowerShell 5.1 turns native stderr into a terminating
        # error under $ErrorActionPreference='Stop'), but a real failure still prints and exits
        # non-zero, which the check below reports.
        & curl.exe --ssl-revoke-best-effort -sS -fL --retry 3 --output $destination $download.Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $($download.Url)" }
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $destination
    if ($signature.Status -ne 'Valid') { throw "Dependency signature validation failed: $destination ($($signature.Status))" }
}
# The design notes are part of the working repository, not necessarily of the product: a tree
# published without them still has a README and the third-party notices. The installer script
# carries the matching Source only when there is something to copy.
$docs = Join-Path $repo 'docs'

# ---- code signing --------------------------------------------------------------
# An OV/EV certificate is what stops SmartScreen and application control from asking who built
# this. Two ways in, because that is how the certificates are shipped: an EV token installs its
# certificate into the user's store (name it by thumbprint), and an OV certificate arrives as a
# PFX (import it for the length of this build, so its password never reaches a command line).
function Find-SignTool {
    if ($SignToolPath) { return (Resolve-Path -LiteralPath $SignToolPath).Path }
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $kits)) { return $null }
    return Get-ChildItem -LiteralPath $kits -Directory |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}

function Sign-ProductFile([string]$Path) {
    $arguments = @('sign', '/fd', 'SHA256')
    if ($TimestampUrl) { $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    $arguments += @('/sha1', $signingThumbprint, $Path)
    & $signTool @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $Path (signtool exit $LASTEXITCODE)." }
}

$signTool = $null
# Not $signThumbprint: PowerShell variable names are case-insensitive and that would be the
# parameter itself, which this line clears before the test below reads it.
$signingThumbprint = $null
$temporaryCertificate = $null
if ($SignPfx -or $SignThumbprint) {
    $signTool = Find-SignTool
    if (-not $signTool) { throw 'Code signing was asked for, but signtool.exe was not found (install the Windows SDK or pass -SignToolPath).' }
    if ($SignPfx) {
        if (-not (Test-Path -LiteralPath $SignPfx)) { throw "Signing certificate not found: $SignPfx" }
        $import = @{ FilePath = $SignPfx; CertStoreLocation = 'Cert:\CurrentUser\My' }
        if ($SignPassword) { $import.Password = ConvertTo-SecureString $SignPassword -AsPlainText -Force }
        $temporaryCertificate = (Import-PfxCertificate @import).Thumbprint
        $signingThumbprint = $temporaryCertificate
    }
    else { $signingThumbprint = $SignThumbprint }
}

try {
    # The payload carries the executables the setup installs, so they are signed before Inno
    # packs them. Third-party binaries (Qt, WinFsp, SQLite, Serilog) are theirs to sign; only
    # this product's own files are named here.
    if ($signTool) {
        $signed = 0
        foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File |
            Where-Object { $_.Name -like 'PolyMount*' -and $_.Extension -in '.exe', '.dll' }) {
            Sign-ProductFile $file.FullName
            $signed++
        }
        Write-Host "Signed $signed payload file(s) with $signingThumbprint."
    }

    $compilerArgs = @('/Qp', "/DAppVersion=$Version", "/DPayloadDir=$payload", "/DOutputDir=$artifacts")
    if (Test-Path -LiteralPath $docs) {
        $docDir = Join-Path $payload 'docs'
        New-Item -ItemType Directory -Path $docDir -Force | Out-Null
        Copy-Item -Path (Join-Path $docs '*.md') -Destination $docDir
        $compilerArgs += '/DDocsPresent=1'
    }
    & $Compiler @compilerArgs (Join-Path $repo 'installer\PolyMount.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Join-Path $artifacts "PolyMount-$Version-win-x64-Setup.exe"
    if ($signTool) {
        Sign-ProductFile $installer
        & $signTool verify /pa $installer | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "The setup's signature did not verify (signtool exit $LASTEXITCODE). A self-signed certificate must be in this machine's Root and TrustedPublisher stores before it verifies; see scripts/Trust-SigningCertificate.ps1."
        }
    }
    # After signing: a signature is part of the file, so a hash taken before it describes
    # something else.
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    "$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath ($installer + '.sha256') -Encoding ASCII
    Write-Host "Installer ready: $installer"
}
finally {
    # The imported OV certificate is a copy for this build; the PFX is the operator's.
    if ($temporaryCertificate) {
        try { Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $temporaryCertificate) -Force -ErrorAction Stop }
        catch { Write-Warning "Could not remove the imported signing certificate $temporaryCertificate from the user store." }
    }
}
