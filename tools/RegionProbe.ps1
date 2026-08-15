# Reports the dock's window region: none, or the union of the icon rectangles.
#
# The tail test only proves the region stops clipping the animations. It cannot tell a correct
# deferred shrink apart from a region that never shrinks at all - which would quietly undo the
# rubber-band fix. GetWindowRgnBox settles it: complexity 1 means no region, 3 means the seven
# icon rectangles are still there.

param([switch] $Hovered)

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Rgn {
    [DllImport("user32.dll")] public static extern int GetWindowRgnBox(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }
}
'@

$hwnd = [IntPtr](& "$PSScriptRoot\DockHwnd.ps1")
$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$saved = [System.Windows.Forms.Cursor]::Position

try {
    if ($Hovered) {
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]($work.Width / 2)), ([int]($work.Bottom - 36))
        Start-Sleep -Milliseconds 400
    } else {
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 40, 40
        Start-Sleep -Milliseconds 1500     # longer than the 500ms settle delay
    }

    $box = New-Object Rgn+RECT
    $kind = [Rgn]::GetWindowRgnBox($hwnd, [ref]$box)
    $win = New-Object Rgn+RECT
    [void][Rgn]::GetWindowRect($hwnd, [ref]$win)

    $name = switch ($kind) { 0 { "ERROR (no region)" } 1 { "NULLREGION" } 2 { "SIMPLEREGION" } 3 { "COMPLEXREGION" } }
    $state = if ($Hovered) { "hovered" } else { "idle" }
    Write-Output ("{0,-8} region {1,-20} box {2}x{3}   window {4}x{5}" -f `
        $state, $name, ($box.r - $box.l), ($box.b - $box.t), ($win.r - $win.l), ($win.b - $win.t))
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}
