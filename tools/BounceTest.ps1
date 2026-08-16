# End-to-end launch test: click an icon, watch the bounce, watch it stop.
#
# Measures a small box strictly inside the icon rather than the icon's silhouette against the
# desktop. The launched app's own window lands wherever it likes and swamped a silhouette
# measurement on the first attempt; the dock is topmost, so anything changing inside that box
# is the icon itself moving.

param(
    [int] $IconX = 1028,
    [int] $IconY = 1000,
    [string] $ExpectProcess = "notepad"
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class Bounce {
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    public static void Tap() {
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(40);
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
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}
'@ -ReferencedAssemblies System.Drawing

$existing = @(Get-Process $ExpectProcess -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
$saved = [System.Windows.Forms.Cursor]::Position

$bx = $IconX - 31; $by = $IconY - 12; $bw = 62; $bh = 32
$times = New-Object System.Collections.ArrayList
$diffs = New-Object System.Collections.ArrayList

try {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point (($IconX - 200)), $IconY
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $IconX, $IconY
    Start-Sleep -Milliseconds 600

    $rest = [Bounce]::Grab($bx, $by, $bw, $bh)
    [Bounce]::Tap()

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt 3000) {
        [void]$times.Add($clock.ElapsedMilliseconds)
        [void]$diffs.Add([Bounce]::Diff($rest, [Bounce]::Grab($bx, $by, $bw, $bh)))
    }
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}

Write-Output "motion inside the pill, relative to the pre-click frame:"
for ($bucket = 0; $bucket -lt 12; $bucket++) {
    $lo = $bucket * 250; $hi = $lo + 250
    $peak = 0.0
    for ($i = 0; $i -lt $times.Count; $i++) {
        if ($times[$i] -ge $lo -and $times[$i] -lt $hi -and $diffs[$i] -gt $peak) { $peak = $diffs[$i] }
    }
    $bar = '#' * [Math]::Min(46, [int]($peak / 2))
    Write-Output ("  {0,4}-{1,4}ms  peak {2,6:N1}  {3}" -f $lo, $hi, $peak, $bar)
}

Start-Sleep -Milliseconds 400
$launched = Get-Process $ExpectProcess -ErrorAction SilentlyContinue | Where-Object { $existing -notcontains $_.Id }
if ($launched) {
    Write-Output ""
    Write-Output "launched $ExpectProcess (pid $($launched.Id -join ',')) - closing it again"
    $launched | ForEach-Object { [void]$_.CloseMainWindow() }
} else {
    Write-Output ""
    Write-Output "no new $ExpectProcess process appeared"
}
