# Confirms every icon still magnifies and still launches.
#
# Worth having as a file rather than an inline command: the window region added for the
# rubber-band fix shrinks the dock to the icon rectangles while idle, and a mistake there would
# show up exactly here - as icons that quietly stop responding near their edges.

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class HC {
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    public static void Tap() {
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
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
}
'@ -ReferencedAssemblies System.Drawing

(New-Object -ComObject Shell.Application).MinimizeAll()
Start-Sleep -Milliseconds 1800

$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$y = [int]($work.Bottom - 12 - 48 / 2)
$mid = [int]($work.Width / 2)
$cell = 48 + 14
$first = $mid - (7 * 48 + 6 * 14) / 2 + 24     # centre of icon 0

$saved = [System.Windows.Forms.Cursor]::Position
$failures = 0

try {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 40, 40
    Start-Sleep -Milliseconds 900
    $rest = [HC]::Grab(700, 950, 560, 95)

    for ($i = 0; $i -lt 7; $i++) {
        $x = [int]($first + $i * $cell)
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $x, $y
        Start-Sleep -Milliseconds 450
        $d = [HC]::Diff($rest, [HC]::Grab(700, 950, 560, 95))
        if ($d -lt 5) { $script:failures++ }
        Write-Output ("  icon {0} at x={1,5}  diff {2,6:N1}  {3}" -f $i, $x, $d, $(if ($d -lt 5) { "NO RESPONSE" } else { "ok" }))
    }

    $before = @(Get-Process notepad -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]($first + 4 * $cell)), $y
    Start-Sleep -Milliseconds 450
    [HC]::Tap()
    Start-Sleep -Seconds 2
    $new = Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id }
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}

Write-Output ""
if ($new) {
    Write-Output "click on icon 4 launched notepad - closing it"
    $new | ForEach-Object { [void]$_.CloseMainWindow() }
} else {
    Write-Output "click on icon 4 launched NOTHING"
    $failures++
}

Write-Output ""
if ($failures -eq 0) { Write-Output "PASS - all seven icons magnify and the click launches." }
else { Write-Output "FAIL - $failures problem(s)." }
