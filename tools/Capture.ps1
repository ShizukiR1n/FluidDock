# Screen capture helper for eyeballing the dock.
#   .\Capture.ps1 -Out ..\_shot.png                       # whole screen
#   .\Capture.ps1 -Out ..\_shot.png -X 510 -Y 760 -W 900 -H 260   # region
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$X = 0,
    [int]$Y = 0,
    [int]$W = 0,
    [int]$H = 0
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if ($W -le 0 -or $H -le 0) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $X = $bounds.X; $Y = $bounds.Y; $W = $bounds.Width; $H = $bounds.Height
}

$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size($W, $H)))
$g.Dispose()

$full = if ([System.IO.Path]::IsPathRooted($Out)) { $Out }
        else { [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Out)) }
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $full ($W x $H at $X,$Y)"
