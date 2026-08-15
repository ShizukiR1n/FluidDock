# Checks that the dock still works after being hidden and shown again from the tray.
#
# The menu test proves the window's visibility flips, which is not the same as the dock still
# being usable: it never looks at whether the icons still magnify afterwards, or whether the idle
# window region came back.
#
# What this test does NOT cover, stated plainly: hiding the dock *while the cursor is on it*.
# That is the case SetVisible's hover-drop exists for - a hidden window gets no WM_MOUSELEAVE, so
# a dock hidden mid-hover would return still believing it was hovered. But reaching the tray
# requires moving the cursor off the dock, which fires that WM_MOUSELEAVE on the way, so the
# scenario is unreachable through the tray by construction. The defensive code stays because a
# hotkey or an external toggle would reach it; it is simply not exercised here, and no rearranging
# of this script would exercise it.
#
# Magnification is measured as a *difference from the resting frame* in the strip above where the
# resting icons stop, not by thresholding pixel values. Two earlier attempts failed here and both
# failures were the measurement, not the dock: alpha is useless because CopyFromScreen returns a
# fully opaque bitmap, and IconTop's saturation threshold assumes a grey desktop behind the dock,
# which this wallpaper is not. A difference against the dock's own resting frame does not care
# what the wallpaper looks like.

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class HS {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowRgnBox(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    /// Pixels differing by more than a tolerance between two frames, counted only above row
    /// <paramref name="rows"/>. The tolerance absorbs the compositor's dithering of the tint
    /// layer, which flickers the bottom bit or two of a channel between otherwise identical
    /// frames and would otherwise show up as a few hundred spurious differences.
    public static int Diff(Bitmap a, Bitmap b, int rows, int tolerance) {
        byte[] ba = Rows(a, rows), bb = Rows(b, rows);
        int count = 0;
        for (int i = 0; i < ba.Length; i += 4) {
            if (Math.Abs(ba[i]   - bb[i])   > tolerance ||
                Math.Abs(ba[i+1] - bb[i+1]) > tolerance ||
                Math.Abs(ba[i+2] - bb[i+2]) > tolerance) count++;
        }
        return count;
    }

    static byte[] Rows(Bitmap bmp, int rows) {
        var rect = new Rectangle(0, 0, bmp.Width, rows);
        BitmapData d = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] buf = new byte[d.Stride * rows];
        Marshal.Copy(d.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(d);
        return buf;
    }
}
'@ -ReferencedAssemblies System.Drawing

$dock = [IntPtr](& "$PSScriptRoot\DockHwnd.ps1")
$saved = [System.Windows.Forms.Cursor]::Position
$failures = 0

# Measured layout: 632x186 window at 644,870, icons every 62px, resting icon tops at row 142
# within the window. The strip above that row is empty at rest and filled when magnified.
$dockX = 644; $dockY = 870; $dockW = 632; $dockH = 186
$restTop = 142
$centres = @(774, 836, 898, 960, 1022, 1084, 1146)
$iconY = 1012
$Away = @(300, 500)

# Anything under this is the dithering floor rather than a moved icon; the observed spread
# between two resting frames is a couple of hundred pixels, a magnified icon is tens of thousands.
$MinLift = 2000

function Move-To($x, $y) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]$x), ([int]$y)
    Start-Sleep -Milliseconds 400
}

function Shot {
    $bmp = New-Object System.Drawing.Bitmap $dockW, $dockH
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($dockX, $dockY, 0, 0, $bmp.Size)
    $g.Dispose()
    return $bmp
}

try {
    # --- baseline -----------------------------------------------------------------------------
    Move-To $Away[0] $Away[1]
    $rest0 = Shot

    Move-To 960 $iconY
    $hover0 = Shot
    $liftBefore = [HS]::Diff($rest0, $hover0, $restTop, 12)
    $hover0.Dispose()
    Write-Output ("  baseline hover:      {0} px changed above the rest line" -f $liftBefore)
    if ($liftBefore -lt $MinLift) { Write-Output "  FAIL - not magnifying to begin with"; $failures++ }

    # --- hide ---------------------------------------------------------------------------------
    & "$PSScriptRoot\TrayToggle.ps1" | Out-Null
    Start-Sleep -Milliseconds 600
    $visible = [HS]::IsWindowVisible($dock)
    Write-Output ("  hidden:              dock visible = {0}" -f $visible)
    if ($visible) { Write-Output "  FAIL - the dock should be hidden"; $failures++ }

    # --- park the cursor away from the dock, then show ----------------------------------------
    Move-To $Away[0] $Away[1]
    & "$PSScriptRoot\TrayToggle.ps1" | Out-Null
    Start-Sleep -Milliseconds 900

    $visible = [HS]::IsWindowVisible($dock)
    Write-Output ("  shown again:         dock visible = {0}" -f $visible)
    if (-not $visible) { Write-Output "  FAIL - the dock should be back"; $failures++ }

    # Cursor is far away, so the dock must look exactly as it did at rest before any of this.
    Move-To $Away[0] $Away[1]
    $rest1 = Shot
    $settled = [HS]::Diff($rest0, $rest1, $restTop, 12)
    Write-Output ("  at rest after show:  {0} px differ from the original rest frame" -f $settled)
    if ($settled -ge $MinLift) { Write-Output "  FAIL - the dock came back still magnified"; $failures++ }

    $r = New-Object HS+RECT
    $complexity = [HS]::GetWindowRgnBox($dock, [ref]$r)
    Write-Output ("  window region:       complexity = {0} (want 3 = COMPLEXREGION)" -f $complexity)
    if ($complexity -ne 3) { Write-Output "  FAIL - the idle region was not reinstated"; $failures++ }

    # --- and it still magnifies, on every icon -------------------------------------------------
    $dead = @()
    $lifts = @()
    for ($i = 0; $i -lt $centres.Count; $i++) {
        Move-To $centres[$i] $iconY
        $s = Shot
        $lift = [HS]::Diff($rest1, $s, $restTop, 12)
        $lifts += $lift
        if ($lift -lt $MinLift) { $dead += $i }
        $s.Dispose()
    }
    Write-Output ("  hover after show:    {0}" -f ($lifts -join ", "))
    if ($dead.Count -eq 0) { Write-Output "  all 7 icons magnify" }
    else { Write-Output ("  FAIL - icons not magnifying: {0}" -f ($dead -join ", ")); $failures++ }

    $rest0.Dispose(); $rest1.Dispose()
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}

Write-Output ""
if ($failures -eq 0) { Write-Output "PASS - hide/show leaves the dock fully working." }
else { Write-Output "FAIL - $failures problem(s)." }
