[CmdletBinding()]
param([switch]$Uninstall, [switch]$NoBuild, [switch]$Machine, [string]$Directory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Directory) { $Directory = Join-Path $repo 'src\PolyMount.Shell\bin' }
if (-not $Uninstall -and -not $NoBuild) { & (Join-Path $PSScriptRoot 'Build-Shell.ps1') -OutputDirectory $directory }
$dll = Join-Path $directory 'PolyMount.Shell.dll'
if (-not $Uninstall -and -not (Test-Path -LiteralPath $dll)) { throw "Missing shell extension: $dll" }
$overlayRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers'
if ($Machine) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) { throw 'Run as administrator to register Explorer overlay entries in HKLM.' }
    $overlayRoot = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers'
}
for($i=0; $i -lt 7; $i++) {
    $guid = '{7d619ac' + $i + '-dbe2-4f33-9a11-6cc2c8329001}'
    $classKey = "HKCU:\Software\Classes\CLSID\$guid"
    $overlayKey = "$overlayRoot\   PolyMount $i"
    if ($Uninstall) {
        Remove-Item -LiteralPath $overlayKey -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $classKey -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        New-Item -Path "$classKey\InprocServer32" -Force | Out-Null
        Set-Item -LiteralPath "$classKey\InprocServer32" -Value $dll
        New-ItemProperty -LiteralPath "$classKey\InprocServer32" -Name ThreadingModel -Value 'Both' -PropertyType String -Force | Out-Null
        New-Item -Path $overlayKey -Force | Out-Null
        Set-Item -LiteralPath $overlayKey -Value $guid
    }
}
Write-Host 'Shell registration updated. Restart Explorer (or sign out/in) to load the new overlay handlers.'
Write-Host 'Windows supports only about 15 overlay slots shared by all applications; dots use seven slots.'
