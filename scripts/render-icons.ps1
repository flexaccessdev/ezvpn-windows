#Requires -Version 5.1
<#
.SYNOPSIS
    Renders assets\icon.svg into src\Ezvpn.App\Assets\ezvpn.ico.

.DESCRIPTION
    The Windows analogue of ezvpn-apple\scripts\render-icons.swift: it turns the
    single source-of-truth icon (assets\icon.svg — a white "shield + keyhole" VPN
    glyph on a teal gradient) into a multi-resolution .ico for the app/taskbar
    icon, the window title-bar icon, and the system-tray icon.

    There is no ImageMagick/Inkscape dependency: the icon's geometry is simple and
    fully known, so it is drawn directly with GDI+ (System.Drawing) via Windows
    PowerShell. Each .ico frame is rendered natively at its target size (rather
    than downscaled from one big bitmap) so the thin shield stroke stays crisp at
    16-24 px in the tray.

    The generated .ico is committed, so CI (dotnet publish) never runs this — it
    only needs to be re-run when icon.svg changes:

        powershell -ExecutionPolicy Bypass -File scripts\render-icons.ps1

    Keep the geometry below in sync with assets\icon.svg.
#>

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$icoPath  = Join-Path $repoRoot 'src\Ezvpn.App\Assets\ezvpn.ico'

# --- Geometry (1024x1024 space, mirrors assets\icon.svg) ---------------------
# NB: this must NOT be a single letter like $S — PowerShell variable names are
# case-insensitive, so $S would alias the foreach loop's $s below and get
# clobbered to the current size, collapsing the scale factor to 1.
$CanvasSize = 1024.0

# Teal gradient background, top-left -> bottom-right.
#   stop 0: rgb(7%,65%,51%)   stop 1: rgb(2%,40%,36%)
$bgTop    = [System.Drawing.Color]::FromArgb(255, [int](0.07*255), [int](0.65*255), [int](0.51*255))
$bgBottom = [System.Drawing.Color]::FromArgb(255, [int](0.02*255), [int](0.40*255), [int](0.36*255))

$strokeW = 51.2   # shield stroke width in 1024-space

# A PointF from 1024-space coords, scaled by $k into the target canvas.
function PF([double]$x, [double]$y, [double]$k) {
    return [System.Drawing.PointF]::new([single]($x * $k), [single]($y * $k))
}

# Append a quadratic Bezier (SVG "Q") to $p as a GDI+ cubic Bezier. For start P0,
# control C, end P1:  CP1 = P0 + 2/3*(C-P0),  CP2 = P1 + 2/3*(C-P1).
function Add-Quad($p, [double]$k, $p0x, $p0y, $cx, $cy, $p1x, $p1y) {
    $c1x = $p0x + (2.0/3.0) * ($cx - $p0x); $c1y = $p0y + (2.0/3.0) * ($cy - $p0y)
    $c2x = $p1x + (2.0/3.0) * ($cx - $p1x); $c2y = $p1y + (2.0/3.0) * ($cy - $p1y)
    $p.AddBezier((PF $p0x $p0y $k), (PF $c1x $c1y $k), (PF $c2x $c2y $k), (PF $p1x $p1y $k))
}

# Shield outline: a rounded badge, mirroring assets\icon.svg:
#   M 512 215.04 Q 658.432 215.04 778.24 256 L 778.24 512
#   Q 778.24 716.8 512 808.96 Q 245.76 716.8 245.76 512 L 245.76 256
#   Q 365.568 215.04 512 215.04 Z
function New-ShieldPath([double]$k) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-Quad $p $k  512.0 215.04  658.432 215.04  778.24 256.0
    $p.AddLine((PF 778.24 256.0 $k), (PF 778.24 512.0 $k))
    Add-Quad $p $k  778.24 512.0  778.24 716.8    512.0 808.96
    Add-Quad $p $k  512.0 808.96  245.76 716.8    245.76 512.0
    $p.AddLine((PF 245.76 512.0 $k), (PF 245.76 256.0 $k))
    Add-Quad $p $k  245.76 256.0  365.568 215.04  512.0 215.04
    $p.CloseFigure()
    return $p
}

function Render-Size([int]$size) {
    $k = $size / $CanvasSize
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        # Gradient background (full square).
        $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF 0, 0),
            (New-Object System.Drawing.PointF $size, $size),
            $bgTop, $bgBottom)
        $g.FillRectangle($brush, $rect)
        $brush.Dispose()

        $white = [System.Drawing.Color]::White

        # Shield outline (stroked, round joins).
        $shield = New-ShieldPath $k
        $pen = New-Object System.Drawing.Pen $white, ([single]($strokeW * $k))
        $pen.LineJoin  = [System.Drawing.Drawing2D.LineJoin]::Round
        $pen.StartCap  = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap    = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawPath($pen, $shield)
        $pen.Dispose(); $shield.Dispose()

        $wb = New-Object System.Drawing.SolidBrush $white

        # Keyhole circle: cx 512 cy 460.8 r 66.56
        $r = 66.56 * $k
        $g.FillEllipse($wb, [single]((512.0*$k) - $r), [single]((460.8*$k) - $r), [single](2*$r), [single](2*$r))

        # Keyhole stem (trapezoid): 483.328,465.92 540.672,465.92 565.248,614.4 458.752,614.4
        $stem = @(
            (New-Object System.Drawing.PointF ([single](483.328*$k), [single](465.92*$k))),
            (New-Object System.Drawing.PointF ([single](540.672*$k), [single](465.92*$k))),
            (New-Object System.Drawing.PointF ([single](565.248*$k), [single](614.4*$k))),
            (New-Object System.Drawing.PointF ([single](458.752*$k), [single](614.4*$k)))
        )
        $g.FillPolygon($wb, $stem)
        $wb.Dispose()
    } finally {
        $g.Dispose()
    }
    return $bmp
}

# --- Render every frame, then pack into a single .ico ------------------------
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @()
foreach ($dim in $sizes) {
    $bmp = Render-Size $dim
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@{ Size = $dim; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $icoPath) | Out-Null
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
try {
    # ICONDIR
    $bw.Write([uint16]0)                 # reserved
    $bw.Write([uint16]1)                 # type: icon
    $bw.Write([uint16]$frames.Count)     # image count

    # ICONDIRENTRY table (16 bytes each). Image data starts after the table.
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }  # 0 means 256
        $bw.Write([byte]$dim)            # width
        $bw.Write([byte]$dim)            # height
        $bw.Write([byte]0)               # palette count
        $bw.Write([byte]0)               # reserved
        $bw.Write([uint16]1)             # color planes
        $bw.Write([uint16]32)            # bits per pixel
        $bw.Write([uint32]$f.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $f.Bytes.Length
    }
    # PNG-encoded frames (supported by the Windows shell for all these sizes).
    foreach ($f in $frames) { $bw.Write($f.Bytes) }
} finally {
    $bw.Dispose(); $fs.Dispose()
}

Write-Host "Wrote $icoPath ($($frames.Count) frames: $($sizes -join ', '))"
