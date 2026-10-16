[CmdletBinding()]
param([string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Destination) { $Destination = Join-Path $repo 'assets\app.ico' }

# The mark is the one the tray draws for itself -- src\PolyMount.Tray\TrayIcons.cs, the "app" glyph:
# a filled rounded square in the accent colour with three stacked chevrons laid over it in white.
# Same numbers in the same 24-unit space, so the picture in Explorer, in the setup wizard and in the
# sidebar are one mark rather than three drawings that drift apart. It is a second description of
# that glyph and not a second opinion about it: change one, change both.
$Accent = '#2563eb'
$Ink = '#ffffff'
# Below 32 px the mark's three strokes land at 1.13 px and no rasteriser can resolve them: they
# average into a smudge, which is what a shrunken copy of the large frame looks like. The small
# frames carry the same idea in fewer, heavier strokes instead.
$Marks = @(
    @{ MaxSize = 24; Stroke = 2.2; Chevrons = @(
            , @(@(4, 9), @(12, 4), @(20, 9), @(12, 14), @(4, 9))
            , @(@(5, 16), @(12, 20), @(19, 16))
        ) }
    @{ MaxSize = [int]::MaxValue; Stroke = 1.7; Chevrons = @(
            , @(@(5, 9), @(12, 5), @(19, 9), @(12, 13), @(5, 9))
            , @(@(5, 13), @(12, 17), @(19, 13))
            , @(@(5, 17), @(12, 21), @(19, 17))
        ) }
)

Add-Type -AssemblyName System.Drawing

function Get-Mark([int]$Size) {
    foreach ($mark in $Marks) { if ($Size -le $mark.MaxSize) { return $mark } }
    return $Marks[-1]
}

function New-Frame([int]$Size) {
    $mark = Get-Mark $Size
    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $memory = New-Object System.IO.MemoryStream
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        # Drawn in the mark's own coordinates whatever the frame is, so every size is the same
        # drawing rather than a differently-proportioned one.
        $graphics.ScaleTransform($Size / 24.0, $Size / 24.0)

        $body = New-Object System.Drawing.Drawing2D.GraphicsPath
        try {
            $body.AddArc(1, 1, 12, 12, 180, 90)
            $body.AddArc(11, 1, 12, 12, 270, 90)
            $body.AddArc(11, 11, 12, 12, 0, 90)
            $body.AddArc(1, 11, 12, 12, 90, 90)
            $body.CloseFigure()
            $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($Accent))
            try { $graphics.FillPath($brush, $body) } finally { $brush.Dispose() }
        } finally { $body.Dispose() }

        $pen = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml($Ink), $mark.Stroke)
        try {
            $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            foreach ($chevron in $mark.Chevrons) {
                $path = New-Object System.Drawing.Drawing2D.GraphicsPath
                try {
                    $points = [System.Drawing.PointF[]]@($chevron | ForEach-Object { New-Object System.Drawing.PointF($_[0], $_[1]) })
                    $path.AddLines($points)
                    $graphics.DrawPath($pen, $path)
                } finally { $path.Dispose() }
            }
        } finally { $pen.Dispose() }

        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        return $memory.ToArray()
    } finally {
        $memory.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$directory = Split-Path $Destination -Parent
New-Item -ItemType Directory -Path $directory -Force | Out-Null

# PNG-backed frames, for the same reason the overlay dots are: they keep exact alpha, where an
# icon built through GetHicon is quantised to a 1-bit mask and looks ragged at every DPI.
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $frames += @{ Size = $size; Data = (New-Frame $size) }
}

$stream = [System.IO.File]::Create($Destination)
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        # 256 is written as 0: the field is a single byte and zero is how it spells 256.
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
} finally { $writer.Dispose(); $stream.Dispose() }

Write-Host "Built: $Destination ($($frames.Count) frames: $(($frames | ForEach-Object { $_.Size }) -join ', '))"
