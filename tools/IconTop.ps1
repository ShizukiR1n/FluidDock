# Measures how high the dock icons are sitting in a capture.
#
# The bounce moves icons vertically, so the topmost saturated row in the frame is a direct
# readout of the animation's phase. Eyeballing two 540x115 screenshots cannot tell a 6px
# difference from none; this can.
#
# Thresholds on the max channel, not luminance: the dock icons are saturated (yellow folder,
# blue Chrome) while everything behind them here is grey, so this ignores the desktop and any
# window edges crossing the crop.

param(
    [Parameter(Mandatory = $true)][string[]] $Path,
    [int] $Threshold = 110,
    [int] $Left = 0,
    [int] $Right = 0
)

Add-Type -AssemblyName System.Drawing

function Measure-IconTop {
    param([string] $File, [int] $Threshold, [int] $Left, [int] $Right)

    $bitmap = [System.Drawing.Bitmap]::FromFile($File)
    try {
        $rect = New-Object System.Drawing.Rectangle 0, 0, $bitmap.Width, $bitmap.Height
        $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                                 [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stride = $data.Stride
        $bytes = New-Object byte[] ($stride * $bitmap.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        $bitmap.UnlockBits($data)

        $x0 = $Left
        $x1 = if ($Right -gt 0) { [Math]::Min($Right, $bitmap.Width) } else { $bitmap.Width }

        $top = -1
        $bright = 0
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            $run = 0
            for ($x = $x0; $x -lt $x1; $x++) {
                $i = $y * $stride + $x * 4
                $max = [Math]::Max([Math]::Max($bytes[$i], $bytes[$i + 1]), $bytes[$i + 2])
                if ($max -gt $Threshold) {
                    $bright++
                    $run++
                    # Three in a row, so a stray anti-aliased pixel cannot set the mark.
                    if ($run -ge 3 -and $top -lt 0) { $top = $y }
                } else { $run = 0 }
            }
        }

        [pscustomobject]@{
            Name   = [System.IO.Path]::GetFileName($File)
            Top    = $top
            Bright = $bright
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

foreach ($file in $Path) {
    $full = if ([System.IO.Path]::IsPathRooted($file)) { $file } else { Join-Path (Get-Location) $file }
    if (-not (Test-Path $full)) { Write-Output "$file : missing"; continue }
    $r = Measure-IconTop -File $full -Threshold $Threshold -Left $Left -Right $Right
    Write-Output ("{0,-24} icon top row = {1,3}   bright px = {2}" -f $r.Name, $r.Top, $r.Bright)
}
