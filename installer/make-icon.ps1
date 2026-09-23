
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot "assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outPath = Join-Path $outDir "codconnect.ico"

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-BrandBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = [System.Drawing.Color]::FromArgb(255, 11, 15, 20)
    $accent = [System.Drawing.Color]::FromArgb(255, 34, 211, 238)
    $bgBrush = New-Object System.Drawing.SolidBrush $bg
    $accentBrush = New-Object System.Drawing.SolidBrush $accent

    $radius = [Math]::Max(3, [int]($size * 0.22))
    $cardPath = New-RoundedRectPath 0 0 $size $size $radius
    $g.FillPath($bgBrush, $cardPath)

    $ringWidth = [Math]::Max(2.0, $size * 0.085)
    $pen = New-Object System.Drawing.Pen ($accent), ($ringWidth)
    $margin = [int]($size * 0.24)
    $ringSize = $size - (2 * $margin)
    $g.DrawEllipse($pen, $margin, $margin, $ringSize, $ringSize)

    $dotRadius = [Math]::Max(2, [int]($size * 0.09))
    $g.FillEllipse($accentBrush, [int]($size / 2 - $dotRadius), [int]($size / 2 - $dotRadius), $dotRadius * 2, $dotRadius * 2)

    $g.Dispose()
    return $bmp
}

$sizes = @(256, 64, 48, 32, 24, 16)
$streams = @()
try {
    foreach ($size in $sizes) {
        $bmp = New-BrandBitmap $size
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $streams += , $ms.ToArray()
        $bmp.Dispose()
    }

    $ico = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $ico
    $writer.Write([UInt16]0)                 # reserved
    $writer.Write([UInt16]1)                 # type: icon
    $writer.Write([UInt16]$streams.Count)    # image count

    $offset = 6 + (16 * $streams.Count)
    for ($i = 0; $i -lt $streams.Count; $i++) {
        $size = $sizes[$i]
        $writer.Write([Byte]($(if ($size -ge 256) { 0 } else { $size })))  # width (0 = 256)
        $writer.Write([Byte]($(if ($size -ge 256) { 0 } else { $size })))  # height
        $writer.Write([Byte]0)               # palette colors
        $writer.Write([Byte]0)               # reserved
        $writer.Write([UInt16]1)             # color planes
        $writer.Write([UInt16]32)            # bits per pixel
        $writer.Write([UInt32]$streams[$i].Length)
        $writer.Write([UInt32]$offset)
        $offset += $streams[$i].Length
    }

    foreach ($data in $streams) {
        $writer.Write($data)
    }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes($outPath, $ico.ToArray())
    Write-Host "Icon written: $outPath ($((Get-Item $outPath).Length) bytes, $($sizes.Count) sizes)"
}
finally {
    $streams = $null
}
