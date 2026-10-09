# Regenerates the Hotshot .ico/.png assets. Run with PowerShell 7 on Windows:
#   pwsh build\make-icons.ps1
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\Hotshot.App\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size, [string]$variant) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $path = $bg = $pen = $mark = $null
    try {
        $g.SmoothingMode = 'AntiAlias'
        $g.PixelOffsetMode = 'HighQuality'
        $g.Clear([System.Drawing.Color]::Transparent)

        $pad = [Math]::Max(0.5, $size * 0.03)
        $rect = [System.Drawing.RectangleF]::new($pad, $pad, $size - 2 * $pad, $size - 2 * $pad)
        $path = New-RoundedPath $rect.X $rect.Y $rect.Width $rect.Height ($size * 0.22)
        if ($variant -eq 'recording') {
            $bg = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 28, 28, 34))
        } else {
            $bg = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect,
                [System.Drawing.Color]::FromArgb(255, 255, 159, 10),
                [System.Drawing.Color]::FromArgb(255, 255, 55, 95), 45.0)
        }
        $g.FillPath($bg, $path)

        $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [Math]::Max(1.35, $size * 0.075))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
        $a = $size * 0.24; $b = $size - $a; $len = $size * 0.14
        $g.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($a, $a + $len), [System.Drawing.PointF]::new($a, $a), [System.Drawing.PointF]::new($a + $len, $a)))
        $g.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($b - $len, $a), [System.Drawing.PointF]::new($b, $a), [System.Drawing.PointF]::new($b, $a + $len)))
        $g.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($b, $b - $len), [System.Drawing.PointF]::new($b, $b), [System.Drawing.PointF]::new($b - $len, $b)))
        $g.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($a + $len, $b), [System.Drawing.PointF]::new($a, $b), [System.Drawing.PointF]::new($a, $b - $len)))

        if ($variant -eq 'recording') {
            $mark = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 59, 48))
            $radius = $size * 0.13
            $g.FillEllipse($mark, ($size / 2) - $radius, ($size / 2) - $radius, $radius * 2, $radius * 2)
        } else {
            $mark = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
            $g.FillPolygon($mark, [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new($size * 0.55, $size * 0.29),
                [System.Drawing.PointF]::new($size * 0.37, $size * 0.54),
                [System.Drawing.PointF]::new($size * 0.49, $size * 0.54),
                [System.Drawing.PointF]::new($size * 0.43, $size * 0.71),
                [System.Drawing.PointF]::new($size * 0.63, $size * 0.45),
                [System.Drawing.PointF]::new($size * 0.52, $size * 0.45)))
        }
    } catch {
        $bmp.Dispose()
        throw
    } finally {
        if ($mark) { $mark.Dispose() }
        if ($pen) { $pen.Dispose() }
        if ($bg) { $bg.Dispose() }
        if ($path) { $path.Dispose() }
        $g.Dispose()
    }
    return $bmp
}

function Write-Ico([string]$file, [int[]]$sizes, [string]$variant) {
    $pngs = foreach ($s in $sizes) {
        $bmp = New-IconBitmap $s $variant
        $ms = [System.IO.MemoryStream]::new()
        try {
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            , $ms.ToArray()
        } finally {
            $bmp.Dispose()
            $ms.Dispose()
        }
    }
    $fs = [System.IO.File]::Create($file)
    $bw = [System.IO.BinaryWriter]::new($fs)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Dispose()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
Write-Ico (Join-Path $assets 'Hotshot.ico') $sizes 'normal'
Write-Ico (Join-Path $assets 'HotshotRecording.ico') $sizes 'recording'
$logo = New-IconBitmap 256 'normal'
$logo.Save((Join-Path $assets 'Logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
Write-Host "Icons written to $assets"
