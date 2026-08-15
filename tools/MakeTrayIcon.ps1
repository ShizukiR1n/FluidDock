# Builds a multi-size .ico from a single source image.
#
# Windows picks the closest size in the file and scales it itself if the exact one is missing.
# The supplied art is 32x32 pixel art, and the tray at 100% scaling wants 16x16 - letting Windows
# do that downscale gives a smeared blob, because its filter is bilinear and pixel art has no
# spare detail to lose. Rendering 16 ourselves at exactly 2:1 with nearest-neighbour keeps the
# pixel grid intact and the face readable.
#
# Interpolation rule, and it matters: nearest-neighbour for every integer ratio (2:1 down, 2x/4x/8x
# up), bicubic only for 20 and 24, which are not integer fractions of 32 and would strobe badly
# under nearest. Never bicubic on an upscale - it turns hard pixel edges into mush.

param(
    [string] $Source = "C:\Users\shayu\Downloads\VA11.ico",
    [string] $Out    = "D:\AI\work space\win\src\FluidDock\assets\tray.ico"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# 16/20/24/32 are the tray at 100/125/150/200% scaling; the rest are what Explorer asks for.
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

if ([IO.Path]::GetExtension($Source) -eq ".ico") {
    $icon = New-Object System.Drawing.Icon($Source)
    $src = $icon.ToBitmap()
    $icon.Dispose()
} else {
    $src = New-Object System.Drawing.Bitmap($Source)
}

function Render([System.Drawing.Bitmap] $from, [int] $size) {
    $ratio = $size / [double]$from.Width
    $integer = ($ratio -ge 1 -and [Math]::Abs($ratio - [Math]::Round($ratio)) -lt 0.001) -or
               ($ratio -lt 1 -and [Math]::Abs((1 / $ratio) - [Math]::Round(1 / $ratio)) -lt 0.001)

    $out = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($out)
    $g.InterpolationMode = if ($integer) {
        [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    } else {
        [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    }
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($from, 0, 0, $size, $size)
    $g.Dispose()
    return $out
}

# A BMP-form icon entry: BITMAPINFOHEADER with a doubled height, then bottom-up BGRA, then the
# 1bpp AND mask. The doubled height is not a typo - the header describes XOR and AND stacked.
function BmpEntry([System.Drawing.Bitmap] $bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $maskStride = [int](([Math]::Floor(($w + 31) / 32)) * 4)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1);  $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($w * $h * 4 + $maskStride * $h))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)

    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $h),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $px = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $px, 0, $px.Length)
    $bmp.UnlockBits($data)

    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($px, $y * $data.Stride, $w * 4) }

    # AND mask: a bit set means "leave the screen alone here". The XOR alpha already carries
    # transparency on every Windows this will ever run on, but the field is not optional.
    $row = New-Object byte[] $maskStride
    for ($y = $h - 1; $y -ge 0; $y--) {
        [Array]::Clear($row, 0, $row.Length)
        for ($x = 0; $x -lt $w; $x++) {
            if ($px[$y * $data.Stride + $x * 4 + 3] -eq 0) {
                $row[[int][Math]::Floor($x / 8)] = $row[[int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8))
            }
        }
        $bw.Write($row, 0, $maskStride)
    }

    $bw.Flush()
    return $ms.ToArray()
}

$images = @()
foreach ($s in $sizes) {
    $bmp = Render $src $s
    # 256 goes in PNG-compressed; as raw BMP it alone would be 256 KB.
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += ,@{ Size = $s; Bytes = $ms.ToArray() }
    } else {
        $images += ,@{ Size = $s; Bytes = (BmpEntry $bmp) }
    }
    $bmp.Dispose()
}
$src.Dispose()

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$i.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) {
    [byte[]] $payload = $i.Bytes
    $bw.Write($payload, 0, $payload.Length)
}
$bw.Flush(); $fs.Close()

# An .ico whose directory points past the end of the file still loads - Windows just returns the
# entries it can read and silently ignores the rest, so a truncated file looks like a working one
# until some DPI you did not test at asks for the missing size. Check it here instead.
$expected = $offset
$actual = (Get-Item $Out).Length
if ($actual -ne $expected) {
    throw "icon is truncated: directory describes $expected bytes, file is $actual"
}

Write-Output ("wrote {0} ({1:N1} KB, {2} sizes: {3})" -f `
    $Out, ($actual / 1KB), $images.Count, (($sizes) -join ", "))
