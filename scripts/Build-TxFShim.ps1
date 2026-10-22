[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) {
    # The plugin ships these beside its assembly; Publish-Plugins.ps1 copies the folder as it is.
    $OutputDirectory = Join-Path $repo 'src\plugins\PolyMount.Plugin.TxFShim\native'
}
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ x64 build tools are required.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

$source = Join-Path $repo 'src\PolyMount.TxFShim.Native'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'probe') -Force | Out-Null
$dll = Join-Path $OutputDirectory 'PolyMount.TxFShim.dll'
$exe = Join-Path $OutputDirectory 'PolyMount.TxFShim.exe'
$probe = Join-Path $OutputDirectory 'probe\PolyMount.TxFShim.Probe.exe'

# /MT on purpose: the DLL runs inside a program that may carry its own MSVCP140 of any vintage, and
# the STL's mutex/condition-variable state is laid out by whichever CRT the host loaded. Nothing
# here may depend on matching it, so the CRT is linked in. For the same reason nothing in the DLL
# uses std::mutex or a std::thread - SRWLOCK and plain Win32 only.
$common = "/nologo /std:c++20 /EHsc /O2 /MT /W4 /WX /utf-8 /DUNICODE /D_UNICODE"
$steps = @(
    "cl.exe $common `"$source\TxFShim.cpp`" /Fo`"$OutputDirectory\TxFShim.obj`" /link /DLL /OUT:`"$dll`"",
    "cl.exe $common `"$source\Launcher.cpp`" /Fo`"$OutputDirectory\Launcher.obj`" /link /SUBSYSTEM:CONSOLE /OUT:`"$exe`"",
    "cl.exe $common `"$source\Probe.cpp`" /Fo`"$OutputDirectory\probe\Probe.obj`" /link /SUBSYSTEM:CONSOLE /OUT:`"$probe`""
)
foreach ($step in $steps) {
    & cmd.exe /d /c "call `"$vcvars`" >nul && $step"
    if ($LASTEXITCODE -ne 0) { throw "TxF shim compilation failed: $step" }
}
foreach ($file in @($dll, $exe, $probe)) {
    if (-not (Test-Path $file)) { throw "Expected '$file' was not produced." }
    '{0,-52} {1,8:N0} bytes' -f (Split-Path $file -Leaf), (Get-Item $file).Length
}
