[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'src\PolyMount.Shell\bin' }
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ x64 build tools are required.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$source = Join-Path $repo 'src\PolyMount.Shell\Overlay.cpp'
$dll = Join-Path $OutputDirectory 'PolyMount.Shell.dll'
$obj = Join-Path $OutputDirectory 'Overlay.obj'
$exports = Join-Path $repo 'src\PolyMount.Shell\Overlay.def'
$importLib = Join-Path $OutputDirectory 'PolyMount.Shell.lib'
$command = "call `"$vcvars`" >nul && cl.exe /nologo /std:c++17 /EHsc /O2 /MD /LD `"$source`" /Fo`"$obj`" /link /DEF:`"$exports`" /IMPLIB:`"$importLib`" /OUT:`"$dll`" shell32.lib ole32.lib uuid.lib"
& cmd.exe /d /c $command
if ($LASTEXITCODE -ne 0) { throw 'Shell extension compilation failed.' }
# A shell extension runs inside whatever process draws folder icons, and that host may carry an
# older app-local MSVCP140 next to its exe (Rider 2025.3 ships 14.29 while this toolset is 14.51).
# The STL's mutex/condition_variable keep their state inside the object, so the host's older
# MSVCP140 misreads them and crashes the host in _Mtx_lock. The overlay therefore locks with
# SRWLOCK/CONDITION_VARIABLE and starts its worker with _beginthreadex; this guard keeps the
# version-coupled entry points from creeping back in.
$image = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($dll))
foreach ($entry in @('_Mtx_','_Cnd_','_Thrd_','_Query_perf_')) {
    if ($image.Contains($entry)) {
        throw "PolyMount.Shell.dll imports $entry from MSVCP140: the host's CRT version would have to match this build's exactly, which a shell host does not guarantee."
    }
}
Add-Type -AssemblyName System.Drawing
$icons = Join-Path $OutputDirectory 'icons'
New-Item -ItemType Directory -Path $icons -Force | Out-Null
$paletteSource = [System.IO.File]::ReadAllText((Join-Path $repo 'src\PolyMount.Core\Core\OriginPalette.inc'))
$palette = @([regex]::Matches($paletteSource,'0x([0-9A-F]{6})') | ForEach-Object { '#' + $_.Groups[1].Value })
for ($i=0; $i -lt $palette.Count; $i++) {
    # PNG-backed ICO frames retain exact RGB/alpha, avoiding GetHicon's mask conversion
    # and low-resolution palette quantization by Explorer at different DPI settings.
    $frames = @()
    foreach ($size in @(16,24,32,48,64,256)) {
        $bitmap = New-Object System.Drawing.Bitmap($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($palette[$i]))
        $diameter = $size / 2.0
        $graphics.FillEllipse($brush,0,$size-$diameter,$diameter,$diameter)
        $memory = New-Object System.IO.MemoryStream
        $bitmap.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
        $frames += @{ Size = $size; Data = $memory.ToArray() }
        $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $brush.Dispose()
    }
    $stream = [System.IO.File]::Create((Join-Path $icons "palette-dot-$i.ico"))
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Data.Length); $writer.Write([uint32]$offset)
            $offset += $frame.Data.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
    } finally { $writer.Dispose(); $stream.Dispose() }
}
Write-Host "Built: $dll"
