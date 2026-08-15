# The decisive architecture test.
#
# Starts the bounce, then hard-blocks the dock's UI thread and burst-captures frames while it
# is provably not pumping messages. If the icons sit at different heights across those frames,
# the animation is being evaluated on the DWM compositor thread and nothing our process does
# can stall it. That is the whole reason for choosing Windows.UI.Composition over a UI-thread
# animation loop, so it is worth proving rather than assuming.
#
# Two things this deliberately does NOT do:
#   - It does not use SendKeys. Hotkeys are posted straight to the window as WM_HOTKEY, so the
#     trigger cannot silently fail because some other window held the foreground.
#   - It does not test whether magnification keeps following the cursor. It cannot: mouse input
#     arrives as window messages on the very thread we blocked. Only an already-in-flight
#     animation can prove compositor independence.

Add-Type @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class Probe {
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    const uint WM_NULL   = 0x0000;
    const uint WM_HOTKEY = 0x0312;

    // True when the window pumped a message within the timeout.
    // SMTO_ABORTIFHUNG | SMTO_BLOCK, so a blocked thread fails fast instead of hanging us too.
    public static bool Responds(IntPtr hwnd, uint timeoutMs) {
        IntPtr result;
        return SendMessageTimeoutW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, 0x0003, timeoutMs, out result) != IntPtr.Zero;
    }

    public static void Hotkey(IntPtr hwnd, int id) {
        PostMessageW(hwnd, WM_HOTKEY, (IntPtr)id, IntPtr.Zero);
    }

    // Captures straight into memory. Writing PNGs between frames would cost more than the
    // interval we are trying to sample.
    public static Bitmap Grab(int x, int y, int w, int h) {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
        return bmp;
    }

    public static byte[] Pixels(Bitmap bmp) {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] bytes = new byte[data.Stride * bmp.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);
        return bytes;
    }

    /// Mean absolute difference between two frames.
    ///
    /// This replaced a brightness threshold that looked for the topmost saturated row. That
    /// worked only while the dock happened to sit against dark windows; once it moved to the
    /// desktop layer the wallpaper was brighter than the threshold everywhere and every frame
    /// reported row 0. Differencing two frames of the same region cares about what changed, not
    /// about what is behind it, so it reads the same against any wallpaper.
    public static double Diff(byte[] a, byte[] b) {
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}
'@ -ReferencedAssemblies System.Drawing

$process = Get-Process FluidDock -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw "FluidDock is not running" }
$hwnd = & "$PSScriptRoot\DockHwnd.ps1"

$HotkeyStall  = 2
$HotkeyBounce = 3
$X = 700; $Y = 925; $W = 540; $H = 115
$frames = 12

Write-Output "hwnd = 0x$($hwnd.ToString('X'))"
Write-Output "responds before test : $([Probe]::Responds($hwnd, 300))"

# Two frames while nothing is moving. This is the noise floor: whatever difference two identical
# scenes register is what a "still" reading looks like, and every motion figure below has to beat
# it to mean anything.
$still = [Probe]::Grab($X, $Y, $W, $H)
$stillPixels = [Probe]::Pixels($still)
$still.Dispose()
Start-Sleep -Milliseconds 120
$still2 = [Probe]::Grab($X, $Y, $W, $H)
$noiseFloor = [Probe]::Diff($stillPixels, [Probe]::Pixels($still2))
$still2.Dispose()
Write-Output ("noise floor at rest  : {0:N2}" -f $noiseFloor)

# Start bouncing every icon, without launching anything.
[Probe]::Hotkey($hwnd, $HotkeyBounce)
Start-Sleep -Milliseconds 400

# Block the dock's UI thread for 2 seconds.
[Probe]::Hotkey($hwnd, $HotkeyStall)

# Wait for the block to actually take hold before sampling, rather than guessing a delay.
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ([Probe]::Responds($hwnd, 40) -and $sw.ElapsedMilliseconds -lt 1500) { }
$blocked = -not [Probe]::Responds($hwnd, 40)
Write-Output "responds during stall: $(-not $blocked)"
if (-not $blocked) { throw "UI thread never blocked - the stall hotkey did not arrive" }

# Burst-sample while the thread is down, each frame measured against the one before it.
$samples = @()
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$previous = $null
for ($i = 0; $i -lt $frames; $i++) {
    $bmp = [Probe]::Grab($X, $Y, $W, $H)
    $pixels = [Probe]::Pixels($bmp)
    if ($null -ne $previous) {
        $samples += [pscustomobject]@{ Ms = $clock.ElapsedMilliseconds; Move = [Probe]::Diff($previous, $pixels) }
    }
    $previous = $pixels
    if ($i -eq 0 -or $i -eq $frames - 1) {
        $bmp.Save("D:\AI\work space\win\_stall_burst_$i.png", [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
}

# Prove the window was still blocked when the burst finished, so every sample above is
# unambiguously from inside the stall window.
$stillBlocked = -not [Probe]::Responds($hwnd, 40)
Write-Output "still blocked after  : $stillBlocked"

Write-Output ""
Write-Output "frame-to-frame motion while the UI thread was blocked:"
$samples | ForEach-Object {
    Write-Output ("  t+{0,4}ms  moved {1,6:N2}  {2}" -f $_.Ms, $_.Move, ('#' * [Math]::Min(40, [int]$_.Move)))
}

# A frame counts as moving only if it clears the noise floor by a comfortable margin.
$threshold = [Math]::Max($noiseFloor * 4, 0.5)
$moving = @($samples | Where-Object { $_.Move -gt $threshold }).Count
$peak = if ($samples.Count -gt 0) { ($samples | Measure-Object -Property Move -Maximum).Maximum } else { 0 }
Write-Output ""
Write-Output ("moving frames = $moving of $($samples.Count)   peak = {0:N2}   threshold = {1:N2}" -f $peak, $threshold)

Start-Sleep -Milliseconds 2200
Write-Output "responds after stall : $([Probe]::Responds($hwnd, 500))"

# Stop bouncing.
[Probe]::Hotkey($hwnd, $HotkeyBounce)

Write-Output ""
if ($stillBlocked -and $moving -ge 3) {
    Write-Output "PASS - icons moved in $moving frames while the UI thread was provably dead."
} else {
    Write-Output "FAIL - motion not demonstrated during the block."
}
