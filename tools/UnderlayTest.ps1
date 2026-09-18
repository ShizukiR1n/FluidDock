# Proves the dock repairs the pixels it owns in the desktop's surface.
#
# On the desktop layer the dock is a child of the desktop window, and its rectangle is a hole
# the parent never paints into. Whatever ends up in those pixels shows through under the icons -
# and a fullscreen game followed by Win+D ends up leaving garbage there, one rectangle per icon.
#
# The corruption is reproduced exactly rather than approximately: a FillRect through the dock's
# own DC lands in the parent's surface, clipped to the dock's region, which is the same place
# and the same shape the garbage takes. Then both repair paths are exercised:
#   1. WM_PAINT     - RedrawWindow on the dock must paint the wallpaper back.
#   2. show desktop - Win+D must do the same without being asked. Win+D raises no foreground
#                    event for the desktop it leaves in front, so this exercises the minimise
#                    events the dock also listens for.
#
# Needs the dock running on the desktop layer. Presses Win+D twice, so the desktop's windows
# are minimised and restored - do not run it while typing somewhere.
#   .\UnderlayTest.ps1
param([int]$SettleMs = 900)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Underlay {
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr rgn, uint flags);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint flags, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static string ClassOf(IntPtr h) {
        var sb = new System.Text.StringBuilder(64); GetClassNameW(h, sb, 64); return sb.ToString();
    }

    // COLORREF is 0x00BBGGRR. Counted on screen as "green well above red and blue" rather than
    // as this exact value: the dock composites a translucent layer over every icon rectangle,
    // so the fill arrives on screen lightened.
    public const uint Green = 0x0050B000;

    public static void Scribble(IntPtr dock, int w, int h) {
        IntPtr dc = GetDC(dock);
        IntPtr brush = CreateSolidBrush(Green);
        RECT r = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
        FillRect(dc, ref r, brush);
        DeleteObject(brush);
        ReleaseDC(dock, dc);
    }

    public static void WinD() {
        keybd_event(0x5B, 0, 0, UIntPtr.Zero); keybd_event(0x44, 0, 0, UIntPtr.Zero);
        keybd_event(0x44, 0, 2, UIntPtr.Zero); keybd_event(0x5B, 0, 2, UIntPtr.Zero);
    }
}
'@

$dock = & "$PSScriptRoot\DockHwnd.ps1"
$parent = [Underlay]::GetAncestor($dock, 1)
$parentClass = [Underlay]::ClassOf($parent)
if ($parentClass -notin @("Progman", "WorkerW")) {
    throw "dock is not parented into the desktop (parent class '$parentClass') - set Layer to Desktop first"
}

$rect = New-Object Underlay+RECT
[Underlay]::GetWindowRect($dock, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
[Console]::WriteLine(("dock 0x{0:X} under {1}, rect {2},{3} {4}x{5}" -f [int64]$dock, $parentClass, $rect.Left, $rect.Top, $w, $h))

function Count-Green {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $n = 0
    for ($y = 0; $y -lt $h; $y += 2) {
        for ($x = 0; $x -lt $w; $x += 2) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.G - [Math]::Max($c.R, $c.B) -gt 40) { $n++ }
        }
    }
    $bmp.Dispose()
    return $n
}

$failures = 0
function Check([string]$label, [int]$green, [int]$baseline, [bool]$expectClean) {
    $ok = if ($expectClean) { $green -le $baseline + 20 } else { $green -gt $baseline + 200 }
    $verdict = if ($ok) { "ok" } else { "FAIL" }
    [Console]::WriteLine(("  {0,-42} green={1,6}  {2}" -f $label, $green, $verdict))
    if (-not $ok) { $script:failures++ }
}

# Start from whatever a repaint gives - on a build that repairs, that is a clean desktop even if
# an earlier run left its scribble behind.
[Underlay]::RedrawWindow($dock, [IntPtr]::Zero, [IntPtr]::Zero, 0x0101) | Out-Null
Start-Sleep -Milliseconds 300
$baseline = Count-Green
[Console]::WriteLine("baseline green pixels: $baseline")

# 1. The scribble must be visible at all, or nothing below proves anything.
[Console]::WriteLine("WM_PAINT path")
[Underlay]::Scribble($dock, $w, $h)
Start-Sleep -Milliseconds 400
Check "scribble shows through under icons" (Count-Green) $baseline $false

# RDW_INVALIDATE | RDW_UPDATENOW: a plain WM_PAINT, nothing else.
[Underlay]::RedrawWindow($dock, [IntPtr]::Zero, [IntPtr]::Zero, 0x0101) | Out-Null
Start-Sleep -Milliseconds 400
Check "RedrawWindow paints the wallpaper back" (Count-Green) $baseline $true

# 2. The show-desktop path, which is what leaving a game with Win+D actually exercises.
[Console]::WriteLine("show-desktop path")
[Underlay]::Scribble($dock, $w, $h)
Start-Sleep -Milliseconds 400
Check "scribble shows through again" (Count-Green) $baseline $false

[Underlay]::WinD()
Start-Sleep -Milliseconds $SettleMs
Check "Win+D repairs it unasked" (Count-Green) $baseline $true

# Put the user's windows back.
[Underlay]::WinD()

if ($failures -gt 0) { throw "$failures check(s) failed" }
[Console]::WriteLine("all checks passed")
