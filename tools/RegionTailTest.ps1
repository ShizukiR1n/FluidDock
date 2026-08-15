# Checks that the window region no longer clips animations that outlive the hover.
#
# Two failure modes, both reported as "the icons get cut off":
#
#   1. Leaving the dock ends the hover instantly, but the magnification takes another 280ms to
#      collapse. Shrinking the region on hover-out chopped that tail off - visible as tearing on
#      a fast sweep across the dock.
#   2. A launch bounce runs for seconds after the cursor has gone. Same shrink, same chop, so the
#      top of the bouncing icon disappeared.
#
# Both are measured the same way: sample pixels that are OUTSIDE the idle region and see whether
# anything moves there. Inside the idle region a clipped and an unclipped dock look identical, so
# measuring the icons themselves would prove nothing.
#
#   - magnify tail: the 14px gaps BETWEEN the rest icon rectangles. Magnified icons spill into
#     them; a clipped window cannot paint there at all.
#   - bounce tail:  the 30px strip ABOVE the rest icon tops, which only an airborne icon reaches.

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class Tail {
    [DllImport("user32.dll")] static extern void keybd_event(byte k, byte s, uint f, IntPtr e);
    public static void CtrlAltB() {
        keybd_event(0x11, 0, 0, IntPtr.Zero); keybd_event(0x12, 0, 0, IntPtr.Zero);
        keybd_event(0x42, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(0x42, 0, 2, IntPtr.Zero);
        keybd_event(0x12, 0, 2, IntPtr.Zero); keybd_event(0x11, 0, 2, IntPtr.Zero);
    }

    public static byte[] Grab(int x, int y, int w, int h) {
        using (var b = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(b)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] px = new byte[d.Stride * h];
            Marshal.Copy(d.Scan0, px, 0, px.Length);
            b.UnlockBits(d);
            return px;
        }
    }

    public static double Diff(byte[] a, byte[] b) {
        long s = 0;
        for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return (double)s / a.Length;
    }

    /// Mean absolute difference over the gap columns only. One grab of the whole run, then the
    /// icon columns are skipped here - six separate CopyFromScreen calls would cost more time
    /// than the 280ms tail being measured.
    public static double GapDiff(byte[] a, byte[] b, int width, int cell, int icon, int count) {
        long s = 0; long n = 0;
        int rows = a.Length / (width * 4);
        for (int y = 0; y < rows; y++) {
            for (int x = 0; x < width; x++) {
                int inCell = x % cell;
                if (inCell < icon || x >= (count - 1) * cell + icon) continue;   // on an icon, or past the last gap
                int i = (y * width + x) * 4;
                for (int c = 0; c < 4; c++) { s += Math.Abs(a[i + c] - b[i + c]); n++; }
            }
        }
        return n == 0 ? 0 : (double)s / n;
    }
}
'@ -ReferencedAssemblies System.Drawing

(New-Object -ComObject Shell.Application).MinimizeAll()
Start-Sleep -Milliseconds 1800

$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$iconSize = 48; $gap = 14; $count = 7
$cell = $iconSize + $gap
$runWidth = $count * $iconSize + ($count - 1) * $gap
$runLeft = [int](($work.Width - $runWidth) / 2)
$iconBottom = $work.Bottom - 12
$iconTop = $iconBottom - $iconSize

$away = New-Object System.Drawing.Point 40, 40
$onDock = New-Object System.Drawing.Point ([int]($work.Width / 2)), ([int]($iconTop + $iconSize / 2))
$saved = [System.Windows.Forms.Cursor]::Position
$failures = 0

try {
    # ---- 1. magnify tail -------------------------------------------------------------------
    [System.Windows.Forms.Cursor]::Position = $away
    Start-Sleep -Milliseconds 1200
    $restBand = [Tail]::Grab($runLeft, $iconTop, $runWidth, $iconSize)

    [System.Windows.Forms.Cursor]::Position = $onDock
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.Cursor]::Position = $away

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $peakEarly = 0.0; $peakLate = 0.0
    while ($clock.ElapsedMilliseconds -lt 1500) {
        $t = $clock.ElapsedMilliseconds
        $d = [Tail]::GapDiff($restBand, [Tail]::Grab($runLeft, $iconTop, $runWidth, $iconSize), $runWidth, $cell, $iconSize, $count)
        if ($t -lt 250) { if ($d -gt $peakEarly) { $peakEarly = $d } }
        elseif ($t -gt 900) { if ($d -gt $peakLate) { $peakLate = $d } }
    }

    Write-Output ("magnify tail  gaps 0-250ms after leaving: {0,6:N2}   (settled, 900ms+: {1,5:N2})" -f $peakEarly, $peakLate)
    if ($peakEarly -lt 3) { Write-Output "  FAIL - nothing painted in the gaps; the region was shrunk before the wave collapsed"; $failures++ }
    else { Write-Output "  ok - the collapsing magnification still paints between the rest rectangles" }
    if ($peakLate -gt 3) { Write-Output "  FAIL - still moving a second later; the dock never settles"; $failures++ }

    # ---- 2. bounce tail --------------------------------------------------------------------
    Start-Sleep -Milliseconds 800
    $restAbove = [Tail]::Grab($runLeft, $iconTop - 30, $runWidth, 30)

    [Tail]::CtrlAltB()
    Start-Sleep -Milliseconds 250

    $peakAir = 0.0
    $clock.Restart()
    while ($clock.ElapsedMilliseconds -lt 1400) {
        $d = [Tail]::Diff($restAbove, [Tail]::Grab($runLeft, $iconTop - 30, $runWidth, 30))
        if ($d -gt $peakAir) { $peakAir = $d }
    }

    [Tail]::CtrlAltB()
    Start-Sleep -Milliseconds 600

    Write-Output ""
    Write-Output ("bounce tail   strip above the rest icon tops: {0,6:N2}" -f $peakAir)
    if ($peakAir -lt 3) { Write-Output "  FAIL - the airborne icons are clipped away above their rest rectangles"; $failures++ }
    else { Write-Output "  ok - the bounce is drawn in full with the cursor away from the dock" }
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}

Write-Output ""
if ($failures -eq 0) { Write-Output "PASS - the region no longer clips either tail." }
else { Write-Output "FAIL - $failures problem(s)." }
