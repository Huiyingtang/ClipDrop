$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pngPath = Join-Path $root 'logo.png'
$icoPath = Join-Path $root 'logo.ico'
$source = Join-Path $root 'logo_source.png'

function New-LogoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    $circle = New-Object System.Drawing.RectangleF -ArgumentList (3 * $s), (3 * $s), (250 * $s), (250 * $s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($circle,
        [System.Drawing.Color]::FromArgb(255, 122, 186, 240),
        [System.Drawing.Color]::FromArgb(255, 26, 92, 186), 62.0)
    $g.FillEllipse($grad, $circle)
    $grad.Dispose()

    $hi = New-Object System.Drawing.RectangleF -ArgumentList (38 * $s), (16 * $s), (180 * $s), (150 * $s)
    $hiBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(34, 255, 255, 255))
    $g.FillEllipse($hiBrush, $hi)
    $hiBrush.Dispose()

    $r = New-Object System.Drawing.RectangleF -ArgumentList (128 * $s - 60 * $s), (128 * $s - 60 * $s), (120 * $s), (120 * $s)
    $penW = 37 * $s
    $start = 25.0
    $sweep = 310.0

    if ($size -ge 32) {
        $shadowPen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(80, 8, 34, 78)), $penW
        $shadowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
        $shadowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
        $g.DrawArc($shadowPen, ($r.X + 3 * $s), ($r.Y + 4 * $s), $r.Width, $r.Height, $start, $sweep)
        $shadowPen.Dispose()
    }

    $cBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($r,
        [System.Drawing.Color]::FromArgb(255, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(255, 219, 233, 250), 90.0)
    $cPen = New-Object System.Drawing.Pen -ArgumentList $cBrush, $penW
    $cPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $cPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $g.DrawArc($cPen, $r, $start, $sweep)
    $cPen.Dispose()
    $cBrush.Dispose()

    $g.Dispose()
    return $bmp
}

function Get-BmpEntry([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)
    $bw.Write([uint32]($w * $h * 4))
    $bw.Write([int32]0)
    $bw.Write([int32]0)
    $bw.Write([uint32]0)
    $bw.Write([uint32]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B)
            $bw.Write([byte]$c.G)
            $bw.Write([byte]$c.R)
            $bw.Write([byte]$c.A)
        }
    }
    $rowBytes = [int]([Math]::Ceiling($w / 32.0) * 4)
    $bw.Write((New-Object byte[] ($rowBytes * $h)))
    $bw.Flush()
    $data = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()
    return ,$data
}

function Get-PngEntry([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $data = $ms.ToArray()
    $ms.Dispose()
    return ,$data
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$entries = @()
$sourceBitmap = $null
if (Test-Path $source) {
    $sourceBitmap = New-Object System.Drawing.Bitmap -ArgumentList $source
    Write-Output ("using custom source image: {0} ({1}x{2})" -f $source, $sourceBitmap.Width, $sourceBitmap.Height)
}

foreach ($size in $sizes) {
    if ($sourceBitmap -ne $null) {
        $bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($sourceBitmap, 0, 0, $size, $size)
        $g.Dispose()
    } else {
        $bmp = New-LogoBitmap $size
    }
    if ($size -ge 128) { $data = Get-PngEntry $bmp } else { $data = Get-BmpEntry $bmp }
    $entries += [pscustomobject]@{ Size = $size; Data = $data; Bitmap = $bmp }
    if ($size -eq 256) { $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png) }
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $bw.Write([byte]($(if ($e.Size -ge 256) { 0 } else { $e.Size })))
    $bw.Write([byte]($(if ($e.Size -ge 256) { 0 } else { $e.Size })))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]([byte[]]$e.Data).Length)
    $bw.Write([uint32]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $bw.Write([byte[]]$e.Data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()
foreach ($e in $entries) { $e.Bitmap.Dispose() }
if ($sourceBitmap -ne $null) { $sourceBitmap.Dispose() }

Write-Output ("wrote {0} ({1} KB) and {2}" -f $icoPath, [int]((Get-Item $icoPath).Length / 1KB), $pngPath)
