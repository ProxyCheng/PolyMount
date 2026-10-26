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
$glyphSource = [System.IO.File]::ReadAllText((Join-Path $repo 'src\PolyMount.Core\Core\OverlayGlyphs.inc'))
$glyphNames = @([regex]::Matches($glyphSource,'PM_GLYPH\("([a-z0-9-]+)"\)') | ForEach-Object { $_.Groups[1].Value })
if ($glyphNames.Count -ne $palette.Count) {
    throw "OriginPalette.inc has $($palette.Count) colours but OverlayGlyphs.inc has $($glyphNames.Count) glyphs; a badge needs one of each."
}

# The badges, in a 24-unit square that is scaled to the frame -- the same grid the tray's line
# icons use (TrayIcons.cs), so a file and its layer row carry the same mark. They are filled
# wherever they can be: a 16-pixel overlay has no room for the tray's 1.7-unit strokes.
$shapes = @{
    'edit-layer' = @(
        @{ K='poly'; Fill=$true; P=@(4,21, 4,16, 17,3, 21,7, 8,20) }
        @{ K='poly'; Fill=$false; P=@(4.6,15.6, 8.4,19.4); W=1.5; Knock=$true }
        @{ K='poly'; Fill=$false; P=@(13,6.5, 17.5,11); W=1.5; Knock=$true })
    'disk-cache' = @(
        @{ K='ellipse'; Fill=$true; C=@(12,6,9,3.4) }
        @{ K='rect'; Fill=$true; R=@(3,6,18,12) }
        @{ K='ellipse'; Fill=$true; C=@(12,18,9,3.4) }
        @{ K='ellipse'; Fill=$false; C=@(12,12.6,9,3.4); W=1.4; Knock=$true })
    'repository' = @(
        @{ K='poly'; Fill=$false; P=@(5,7.6, 5,16.4); W=2.6 }
        @{ K='poly'; Fill=$false; P=@(19,7.6, 19,12, 6.4,15.4); W=2.6 }
        @{ K='ellipse'; Fill=$true; C=@(5,5,2.8,2.8) }
        @{ K='ellipse'; Fill=$true; C=@(5,19,2.8,2.8) }
        @{ K='ellipse'; Fill=$true; C=@(19,5,2.8,2.8) })
    'folder' = @(
        @{ K='poly'; Fill=$true; P=@(3,7, 3,20, 21,20, 21,7, 11,7, 9,4, 3,4) })
    'predict-path' = @(
        @{ K='poly'; Fill=$false; P=@(6.4,18.5, 11,18.5, 11,7, 15.5,7); W=2.6 }
        @{ K='poly'; Fill=$true; P=@(15,3.2, 21.8,7, 15,10.8) }
        @{ K='ellipse'; Fill=$true; C=@(4,18.5,2.6,2.6) })
    'memory-chip' = @(
        @{ K='rrect'; Fill=$true; R=@(6,6,12,12); Radius=2.5 }
        @{ K='poly'; Fill=$false; P=@(9,2.5, 9,6, 15,6, 15,2.5); W=2.2 }
        @{ K='poly'; Fill=$false; P=@(9,21.5, 9,18, 15,18, 15,21.5); W=2.2 }
        @{ K='poly'; Fill=$false; P=@(2.5,9, 6,9, 6,15, 2.5,15); W=2.2 }
        @{ K='poly'; Fill=$false; P=@(21.5,9, 18,9, 18,15, 21.5,15); W=2.2 })
    'warm-listings' = @(
        @{ K='poly'; Fill=$true; P=@(3,8, 3,21, 21,21, 21,8, 11,8, 9,4.5, 3,4.5) }
        @{ K='poly'; Fill=$true; Knock=$true; P=@(10.4,18, 13.6,18, 13.6,12.5, 17,12.5, 12,7.5, 7,12.5, 10.4,12.5) })
    'layers' = @(
        @{ K='poly'; Fill=$true; P=@(12,2, 22,12, 12,22, 2,12) }
        @{ K='poly'; Fill=$true; Knock=$true; P=@(12,7.5, 16.5,12, 12,16.5, 7.5,12) })
}
foreach ($name in $glyphNames) { if (-not $shapes.ContainsKey($name)) { throw "No artwork for the '$name' badge; add it to `$shapes in scripts\Build-Shell.ps1." } }

# Draws one badge at one frame size. Coordinates are divided by 24 and multiplied by the frame,
# so a shape is described once and every DPI gets the same picture.
function New-BadgeFrame([string]$glyph, [string]$colour, [int]$size) {
    $unit = $size / 24.0
    $bitmap = New-Object System.Drawing.Bitmap($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $rgb = [System.Drawing.ColorTranslator]::FromHtml($colour)
        $ink = New-Object System.Drawing.SolidBrush($rgb)
        $clear = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::Transparent)
        try {
            foreach ($op in $shapes[$glyph]) {
                $points = @()
                if ($op.P) {
                    for ($i=0; $i -lt $op.P.Count; $i += 2) {
                        $points += New-Object System.Drawing.PointF([single]($op.P[$i]*$unit), [single]($op.P[$i+1]*$unit))
                    }
                }
                $rect = $null
                if ($op.R) { $rect = New-Object System.Drawing.RectangleF([single]($op.R[0]*$unit), [single]($op.R[1]*$unit), [single]($op.R[2]*$unit), [single]($op.R[3]*$unit)) }
                $ellipse = $null
                if ($op.C) {
                    $ellipse = New-Object System.Drawing.RectangleF(
                        [single](($op.C[0]-$op.C[2])*$unit), [single](($op.C[1]-$op.C[3])*$unit),
                        [single]($op.C[2]*2*$unit), [single]($op.C[3]*2*$unit))
                }
                # Knock-outs erase what is under them: one icon is one colour, so a ring or an
                # inner diamond is a hole in the shape rather than a second colour.
                $graphics.CompositingMode = if ($op.Knock) { [System.Drawing.Drawing2D.CompositingMode]::SourceCopy } else { [System.Drawing.Drawing2D.CompositingMode]::SourceOver }
                $brush = if ($op.Knock) { $clear } else { $ink }
                if ($op.W) {
                    $width = [single][Math]::Max($op.W * $unit, 1.25)
                    $pen = New-Object System.Drawing.Pen($brush.Color, $width)
                    try {
                        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                        if ($points.Count -ge 2) { $graphics.DrawLines($pen, $points) }
                        if ($ellipse) { $graphics.DrawEllipse($pen, $ellipse) }
                    } finally { $pen.Dispose() }
                } elseif ($op.K -eq 'poly') {
                    $graphics.FillPolygon($brush, $points)
                } elseif ($op.K -eq 'ellipse') {
                    $graphics.FillEllipse($brush, $ellipse)
                } elseif ($op.K -eq 'rect') {
                    $graphics.FillRectangle($brush, $rect)
                } elseif ($op.K -eq 'rrect') {
                    $radius = [single]($op.Radius * $unit)
                    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
                    try {
                        $path.AddArc($rect.Left, $rect.Top, $radius*2, $radius*2, 180, 90)
                        $path.AddArc($rect.Right-$radius*2, $rect.Top, $radius*2, $radius*2, 270, 90)
                        $path.AddArc($rect.Right-$radius*2, $rect.Bottom-$radius*2, $radius*2, $radius*2, 0, 90)
                        $path.AddArc($rect.Left, $rect.Bottom-$radius*2, $radius*2, $radius*2, 90, 90)
                        $path.CloseFigure()
                        $graphics.FillPath($brush, $path)
                    } finally { $path.Dispose() }
                }
            }
        } finally { $ink.Dispose(); $clear.Dispose() }
        $memory = New-Object System.IO.MemoryStream
        try { $bitmap.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png); return $memory.ToArray() }
        finally { $memory.Dispose() }
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

for ($i=0; $i -lt $glyphNames.Count; $i++) {
    # PNG-backed ICO frames retain exact RGB/alpha, avoiding GetHicon's mask conversion
    # and low-resolution palette quantization by Explorer at different DPI settings.
    $frames = @()
    foreach ($size in @(16,24,32,48,64,256)) {
        $frames += @{ Size = $size; Data = (New-BadgeFrame $glyphNames[$i] $palette[$i] $size) }
    }
    $stream = [System.IO.File]::Create((Join-Path $icons "badge-$($glyphNames[$i]).ico"))
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

# The setup registers one overlay handler per badge, so it needs exactly the number drawn above.
# It reads that from here rather than keeping its own literal: the glyph list is the only place
# the count lives, and a badge added there must not leave a stale 7 behind. (It cannot ride on the
# compiler command line instead: ISCC's /D values are strings, and the .iss needs an integer to
# loop over.)
$slotFile = Join-Path $OutputDirectory 'badge-slots.iss'
Set-Content -LiteralPath $slotFile -Encoding ascii -Value @(
    '; Generated by scripts\Build-Shell.ps1 from src\PolyMount.Core\Core\OverlayGlyphs.inc. Do not edit.'
    "#define BadgeSlots $($glyphNames.Count)")

# The setup uses this to decide whether an upgrade changes what Explorer runs. The DLL's own bytes
# cannot answer that: the PE timestamp differs on every link, and signing adds a timestamped
# signature, so two builds of identical source never compare equal. This is a hash of the build's
# inputs instead -- the files the DLL is compiled from, the glyph list it includes, plus the flags
# below -- written beside the DLL and shipped unsigned. It changes only when what Explorer would
# load changes.
$glyphFile = Join-Path $repo 'src\PolyMount.Core\Core\OverlayGlyphs.inc'
$inputHash = @(foreach ($file in @($source, $exports, $glyphFile)) {
    (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
}) -join ' '
$stamp = $inputHash + ' cl /std:c++17 /EHsc /O2 /MD /LD'
$builder = [System.Security.Cryptography.SHA256]::Create()
try {
    $id = -join ($builder.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($stamp)) |
        ForEach-Object { $_.ToString('x2') })
} finally { $builder.Dispose() }
[System.IO.File]::WriteAllText((Join-Path $OutputDirectory 'build-id.txt'), $id, (New-Object System.Text.UTF8Encoding($false)))
