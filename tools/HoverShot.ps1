# Parks the cursor over a point, waits for the animation to settle, screenshots, then puts the
# cursor back where the user left it.
param(
    [Parameter(Mandatory = $true)][int]$CursorX,
    [Parameter(Mandatory = $true)][int]$CursorY,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$X = 0, [int]$Y = 0, [int]$W = 0, [int]$H = 0,
    [int]$SettleMs = 450,
    [switch]$Jiggle
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$saved = [System.Windows.Forms.Cursor]::Position

try {
    if ($Jiggle) {
        # Arrive from outside so the dock sees an enter, not a teleport into the middle.
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point(($CursorX - 200), $CursorY)
        Start-Sleep -Milliseconds 120
    }

    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($CursorX, $CursorY)
    Start-Sleep -Milliseconds $SettleMs

    & "$PSScriptRoot\Capture.ps1" -Out $Out -X $X -Y $Y -W $W -H $H
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}
