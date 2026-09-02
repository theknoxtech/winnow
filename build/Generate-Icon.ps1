#Requires -Version 5.1
<#
.SYNOPSIS
    Generates src\Winnow.App\winnow.ico.

.DESCRIPTION
    Draws the app icon and writes a multi-resolution .ico. Kept as a script rather than a
    committed-and-forgotten binary so the icon can be re-derived, tweaked, or re-coloured later
    without anyone having to reverse-engineer it in an image editor.

    The mark is a filter funnel - three tapering bars - with two specks of chaff coming off the
    top right. Winnowing is separating grain from chaff, which is what the presets do to a noisy
    event log, and the tapering-bars glyph is already universally read as "filter" by the people
    who will use this.

    Design constraints, in priority order:
      1. Legible at 16x16, where it spends most of its life in a taskbar and title bar.
      2. Recognisable in a monochrome silhouette, since some shell contexts desaturate it.
      3. Distinct at a glance from Event Viewer's own icon.

    The chaff specks are dropped below 32px - at 16px they degrade into indistinct grey pixels
    that just muddy the mark.

.EXAMPLE
    .\build\Generate-Icon.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'src\Winnow.App\winnow.ico' }

# Accent blue, matching Theme.xaml's AccentColor so the icon and the app agree.
$accent     = [System.Drawing.Color]::FromArgb(30, 123, 214)
$accentDeep = [System.Drawing.Color]::FromArgb(21, 94, 168)
$white      = [System.Drawing.Color]::White
$chaff      = [System.Drawing.Color]::FromArgb(255, 214, 102)

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- Rounded-square tile -------------------------------------------------
    # A filled tile rather than a bare glyph: it gives the icon presence at 16px and keeps the
    # white mark readable against both light and dark taskbars.
    $radius = [Math]::Max(2, [int]($size * 0.18))
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($size, $size)),
        $accent, $accentDeep)
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()

    # --- Filter funnel: three tapering bars ----------------------------------
    # Widths and the vertical block are proportional, so the mark holds its shape at every size.
    $barH = [Math]::Max(2, [int][Math]::Round($size * 0.13))
    $gap  = [Math]::Max(1, [int][Math]::Round($size * 0.085))
    $totalH = ($barH * 3) + ($gap * 2)
    $top = [int][Math]::Round(($size - $totalH) / 2.0) + [int]($size * 0.04)

    $widths = @(0.56, 0.36, 0.17)
    $whiteBrush = New-Object System.Drawing.SolidBrush($white)

    for ($i = 0; $i -lt 3; $i++) {
        $w = [Math]::Max(2, [int][Math]::Round($size * $widths[$i]))
        $x = [int][Math]::Round(($size - $w) / 2.0)
        $y = $top + ($i * ($barH + $gap))

        # Rounded ends at larger sizes; square at 16px, where rounding just blurs the bar away.
        if ($size -ge 32) {
            $r = [Math]::Min([int]($barH / 2), [int]($w / 2))
            $bar = New-Object System.Drawing.Drawing2D.GraphicsPath
            $bd = $r * 2
            if ($bd -lt 2) {
                $bar.AddRectangle((New-Object System.Drawing.Rectangle($x, $y, $w, $barH)))
            } else {
                $bar.AddArc($x, $y, $bd, $bd, 90, 180)
                $bar.AddArc($x + $w - $bd, $y, $bd, $bd, 270, 180)
                $bar.CloseFigure()
            }
            $g.FillPath($whiteBrush, $bar)
            $bar.Dispose()
        } else {
            $g.FillRectangle($whiteBrush, $x, $y, $w, $barH)
        }
    }

    # --- Chaff ---------------------------------------------------------------
    # Two specks blown clear of the funnel. Omitted at 16px, where they read as dirt.
    if ($size -ge 32) {
        $chaffBrush = New-Object System.Drawing.SolidBrush($chaff)
        $s1 = [Math]::Max(2, [int][Math]::Round($size * 0.075))
        $s2 = [Math]::Max(2, [int][Math]::Round($size * 0.055))
        $g.FillEllipse($chaffBrush, [int]($size * 0.70), [int]($size * 0.17), $s1, $s1)
        $g.FillEllipse($chaffBrush, [int]($size * 0.83), [int]($size * 0.31), $s2, $s2)
        $chaffBrush.Dispose()
    }

    $whiteBrush.Dispose()
    $g.Dispose()
    return $bmp
}

# Sizes Windows actually asks for across the shell, taskbar, alt-tab and file properties.
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = @()

foreach ($size in $sizes) {
    $bmp = New-Frame $size
    $stream = New-Object System.IO.MemoryStream
    # Each frame is stored PNG-compressed, which every Windows version since Vista reads and which
    # keeps a 256px frame from bloating the file to megabytes.
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $bmp.Dispose()
}

# --- Assemble the ICO container ---------------------------------------------
# Written by hand because Bitmap.Save(..., ImageFormat.Icon) does not produce a real multi-frame
# icon - it silently emits a single low-colour frame.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)

$w.Write([UInt16]0)                 # reserved
$w.Write([UInt16]1)                 # type: 1 = icon
$w.Write([UInt16]$frames.Count)

# Directory entries are fixed-length, so the first frame's data starts after all of them.
$offset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }   # 0 means 256 in this format
    $w.Write([Byte]$dim)            # width
    $w.Write([Byte]$dim)            # height
    $w.Write([Byte]0)               # palette size (0 = no palette)
    $w.Write([Byte]0)               # reserved
    $w.Write([UInt16]1)             # colour planes
    $w.Write([UInt16]32)            # bits per pixel
    $w.Write([UInt32]$frame.Bytes.Length)
    $w.Write([UInt32]$offset)
    $offset += $frame.Bytes.Length
}

foreach ($frame in $frames) { $w.Write($frame.Bytes) }

$w.Flush()
[System.IO.File]::WriteAllBytes($OutputPath, $out.ToArray())
$w.Dispose()
$out.Dispose()

$kb = [math]::Round((Get-Item $OutputPath).Length / 1KB, 1)
Write-Host "Wrote $OutputPath ($kb KB, $($frames.Count) frames: $($sizes -join ', '))" -ForegroundColor Green

# Also emit a PNG for the README. Generated here rather than exported by hand so the documented
# mark and the shipped icon cannot drift apart - GitHub will not render an .ico inline.
$pngPath = Join-Path $repoRoot 'docs\images\icon.png'
$pngDir = Split-Path $pngPath -Parent
if (Test-Path $pngDir) {
    $preview = New-Frame 256
    $preview.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $preview.Dispose()
    Write-Host "Wrote $pngPath (256 px, for the README)" -ForegroundColor Green
}
