<#
.SYNOPSIS
    Draws the WindowLive app icon (design_handoff_project_window_1b, section 1)
    and writes a multi-size .ico to src/WindowLive.App/Assets/WindowLive.ico.

.DESCRIPTION
    Design: dark tile (#1c1c1c, 1px #2e2e2e border, rounded corners) with a
    centered 文 glyph, plus mint (#47d6a2) corner brackets top-left/bottom-right
    at >=48px. At 32/16px the brackets are dropped and the glyph itself is
    rendered in mint instead of white.

    Sizes: 256, 96, 48, 32, 16. The design doc only names metrics for 96/32/16
    (radius 12/6/3px respectively); 256 and 48 are interpolated proportionally
    — see $sizeSpecs below.

    The .ico format allows PNG-compressed image entries (used by every icon
    >=... in practice, all modern sizes) — this script writes PNGs per size
    and assembles the ICONDIR/ICONDIRENTRY structures by hand rather than
    shelling out to an external tool.

.NOTES
    Run from repo root or anywhere; paths are resolved relative to this
    script's location.
#>

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot 'src/WindowLive.App/Assets'
$outPath = Join-Path $outDir 'WindowLive.ico'

if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

# size -> (corner radius px, border thickness px, draw brackets, glyph color)
# Radius/border are interpolated for 256/48 (not named in the design doc,
# which only specifies 96/32/16) following the same visual proportion as the
# nearest named tier.
$mint = [System.Drawing.Color]::FromArgb(255, 0x47, 0xd6, 0xa2)
$white = [System.Drawing.Color]::FromArgb(255, 0xf2, 0xf2, 0xf2)
$tileBg = [System.Drawing.Color]::FromArgb(255, 0x1c, 0x1c, 0x1c)
$tileBorder = [System.Drawing.Color]::FromArgb(255, 0x2e, 0x2e, 0x2e)

$sizeSpecs = @(
    @{ Size = 256; Radius = 28; Border = 2; Brackets = $true;  Glyph = $white },
    @{ Size = 96;  Radius = 12; Border = 1; Brackets = $true;  Glyph = $white },
    @{ Size = 48;  Radius = 8;  Border = 1; Brackets = $true;  Glyph = $white },
    @{ Size = 32;  Radius = 6;  Border = 1; Brackets = $false; Glyph = $mint },
    @{ Size = 16;  Radius = 3;  Border = 1; Brackets = $false; Glyph = $mint }
)

function New-RoundedRectPath {
    param([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($Radius -le 0) {
        $path.AddRectangle([System.Drawing.RectangleF]::new($X, $Y, $Width, $Height))
        return $path
    }
    $d = $Radius * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $Width - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $Width - $d, $Y + $Height - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $Height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconFrame {
    param(
        [int]$Size,
        [float]$Radius,
        [float]$BorderThickness,
        [bool]$Brackets,
        [System.Drawing.Color]$GlyphColor
    )

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([System.Drawing.Color]::Transparent)

        $inset = $BorderThickness / 2.0
        $tileRect = [System.Drawing.RectangleF]::new($inset, $inset, $Size - $BorderThickness, $Size - $BorderThickness)
        $path = New-RoundedRectPath -X $tileRect.X -Y $tileRect.Y -Width $tileRect.Width -Height $tileRect.Height -Radius $Radius

        $bgBrush = New-Object System.Drawing.SolidBrush($tileBg)
        $g.FillPath($bgBrush, $path)

        $borderPen = New-Object System.Drawing.Pen($tileBorder, $BorderThickness)
        $g.DrawPath($borderPen, $path)

        # Glyph: 文, centered. ~40/96 of tile size at the 96px reference tier.
        $glyphSize = [float]([math]::Round($Size * (40.0 / 96.0)))
        if ($glyphSize -lt 6) { $glyphSize = 6 }
        $fontName = 'Yu Gothic UI'
        $font = New-Object System.Drawing.Font($fontName, $glyphSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $glyphBrush = New-Object System.Drawing.SolidBrush($GlyphColor)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString([string]([char]0x6587), $font, $glyphBrush, [System.Drawing.RectangleF]::new(0, 0, $Size, $Size), $sf)

        if ($Brackets) {
            # Corner brackets: 14x14 at 96px reference, 2px thick, scaled
            # proportionally, inset to sit just inside the tile border.
            $bracketLen = $Size * (14.0 / 96.0)
            $bracketThickness = [math]::Max(1.0, $Size * (2.0 / 96.0))
            $bracketInset = $Size * (12.0 / 96.0)
            $bracketPen = New-Object System.Drawing.Pen($mint, $bracketThickness)
            $bracketPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
            $bracketPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat

            # Top-left: border-top + border-left of a 14x14 box at (12,12).
            $g.DrawLine($bracketPen, $bracketInset, $bracketInset, $bracketInset + $bracketLen, $bracketInset)
            $g.DrawLine($bracketPen, $bracketInset, $bracketInset, $bracketInset, $bracketInset + $bracketLen)

            # Bottom-right: border-bottom + border-right of a 14x14 box at (12,12) from the far corner.
            $farX = $Size - $bracketInset
            $farY = $Size - $bracketInset
            $g.DrawLine($bracketPen, $farX - $bracketLen, $farY, $farX, $farY)
            $g.DrawLine($bracketPen, $farX, $farY - $bracketLen, $farX, $farY)

            $bracketPen.Dispose()
        }

        $sf.Dispose()
        $glyphBrush.Dispose()
        $font.Dispose()
        $borderPen.Dispose()
        $bgBrush.Dispose()
        $path.Dispose()
    }
    finally {
        $g.Dispose()
    }

    return $bmp
}

function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    # Unary comma prevents PowerShell from unrolling the byte[] onto the
    # pipeline (which would otherwise silently turn it into an Object[] of
    # boxed bytes by the time the caller captures it, breaking the later
    # BinaryWriter.Write(byte[]) overload resolution).
    return ,$ms.ToArray()
}

# ---- Build each frame as a PNG blob ------------------------------------
$frames = @()
foreach ($spec in $sizeSpecs) {
    Write-Host "Rendering $($spec.Size)x$($spec.Size)..."
    $bmp = New-IconFrame -Size $spec.Size -Radius $spec.Radius -BorderThickness $spec.Border -Brackets $spec.Brackets -GlyphColor $spec.Glyph
    $png = ConvertTo-PngBytes -Bitmap $bmp
    $bmp.Dispose()
    $frames += [PSCustomObject]@{ Size = $spec.Size; Png = $png }
}

# ---- Assemble the .ico (PNG-payload ICONDIRENTRY per frame) -----------
# ICONDIR: reserved(2)=0, type(2)=1, count(2)=N
# ICONDIRENTRY (16 bytes each): width(1,0=256) height(1,0=256) colorCount(1)=0
#   reserved(1)=0 planes(2)=1 bitCount(2)=32 bytesInRes(4) imageOffset(4)
$headerSize = 6
$entrySize = 16
$dirEntriesSize = $entrySize * $frames.Count
$offset = $headerSize + $dirEntriesSize

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)

$writer.Write([UInt16]0)              # reserved
$writer.Write([UInt16]1)              # type = icon
$writer.Write([UInt16]$frames.Count)  # image count

foreach ($frame in $frames) {
    $byteSize = $frame.Size
    $dim = if ($byteSize -ge 256) { 0 } else { $byteSize }
    $writer.Write([byte]$dim)          # width
    $writer.Write([byte]$dim)          # height
    $writer.Write([byte]0)             # color count (0 = no palette, true color)
    $writer.Write([byte]0)             # reserved
    $writer.Write([UInt16]1)           # color planes
    $writer.Write([UInt16]32)          # bits per pixel
    $writer.Write([UInt32]$frame.Png.Length)  # size of PNG data
    $writer.Write([UInt32]$offset)     # offset of PNG data from start of file
    $offset += $frame.Png.Length
}

foreach ($frame in $frames) {
    # Explicit cast forces unambiguous binding to BinaryWriter.Write(byte[]).
    $writer.Write([byte[]]$frame.Png)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($outPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Host "Wrote $outPath ($([math]::Round((Get-Item $outPath).Length / 1024, 1)) KB)"

# ---- Verify the file loads -------------------------------------------
try {
    $icon = [System.Drawing.Icon]::new($outPath)
    Write-Host "Icon loads OK (default size $($icon.Width)x$($icon.Height))."
    $icon.Dispose()

    $icon32 = New-Object System.Drawing.Icon($outPath, 32, 32)
    Write-Host "Icon loads OK at 32x32 (actual $($icon32.Width)x$($icon32.Height))."
    $icon32.Dispose()
}
catch {
    Write-Error "Icon failed validation: $_"
    exit 1
}
