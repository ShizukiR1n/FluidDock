# Does the dock follow the screen when the resolution changes?
#
# Switches the primary display to a different mode, waits, and measures where the dock's window
# is against the new screen and work area: centred, and the same distance above the work area's
# bottom edge as it was before. Then switches back and measures again.
#
# The mode is chosen from what the display reports, 1680x1050 if it has it and the widest mode
# narrower than the current one otherwise, so this runs on any monitor. The change is made
# without saving (CDS flags 0) and undone by restoring the registry mode, so a crash mid-test
# leaves nothing changed after the next sign-in either.
#
# The bug this guards: on the desktop layer the dock is a child window, and WM_DISPLAYCHANGE is
# broadcast to top-level windows only. The dock never heard it, and sat where the old screen had
# put it - over the taskbar of the new one.
#
# Kept ASCII-only on purpose: Windows PowerShell 5.1 parses .ps1 as the system ANSI code page
# unless the file carries a UTF-8 BOM, so a Chinese literal here would be silently corrupted.
param([int]$SettleMs = 2500)

$ErrorActionPreference = "Stop"

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RT {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY; public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels; public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string dev, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string dev, IntPtr dm, IntPtr hwnd, uint flags, IntPtr p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint a, uint b, ref RECT r, uint c);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);

    public static DEVMODE Current() {
        var dm = new DEVMODE(); dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
        EnumDisplaySettingsW(null, -1, ref dm); return dm;
    }

    // 1680x1050 at the current depth if the display has it, else the widest mode narrower than now.
    public static int[] PickOther() {
        DEVMODE now = Current();
        int bestW = 0, bestH = 0;
        for (int i = 0; ; i++) {
            var dm = new DEVMODE(); dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettingsW(null, i, ref dm)) break;
            if (dm.dmBitsPerPel != now.dmBitsPerPel || dm.dmPelsWidth >= now.dmPelsWidth) continue;
            if (dm.dmPelsWidth == 1680 && dm.dmPelsHeight == 1050) return new int[] { 1680, 1050 };
            if (dm.dmPelsWidth > bestW) { bestW = (int)dm.dmPelsWidth; bestH = (int)dm.dmPelsHeight; }
        }
        return new int[] { bestW, bestH };
    }

    public static int Set(int w, int h) {
        DEVMODE dm = Current();
        dm.dmPelsWidth = (uint)w; dm.dmPelsHeight = (uint)h; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettingsExW(null, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
    }

    public static int Restore() { return ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); }

    public static RECT Work() { RECT r = new RECT(); SystemParametersInfoW(0x0030, 0, ref r, 0); return r; }
}
'@

$failures = 0
function Check($name, $condition, $detail) {
    $verdict = if ($condition) { "ok" } else { "FAIL" }
    [Console]::WriteLine(("  {0,-44} {1,-5} {2}" -f $name, $verdict, $detail))
    if (-not $condition) { $script:failures++ }
}

# Where the dock is, against the screen: (centre offset from the screen's centre, gap between
# the window's bottom and the work area's bottom). Both should survive a mode change unchanged.
function Get-DockPlacement {
    $dock = & "$PSScriptRoot\DockHwnd.ps1"
    $r = New-Object RT+RECT
    [RT]::GetWindowRect($dock, [ref]$r) | Out-Null
    $work = [RT]::Work()
    $sw = [RT]::GetSystemMetrics(0)
    @{
        Screen = "$sw x $([RT]::GetSystemMetrics(1))"
        Rect = "$($r.L),$($r.T)-$($r.R),$($r.B)"
        Off = [int](($r.L + $r.R) / 2) - [int]($sw / 2)
        Gap = $work.B - $r.B
    }
}

if (-not (Get-Process FluidDock -ErrorAction SilentlyContinue)) { throw "FluidDock is not running" }

$other = [RT]::PickOther()
if ($other[0] -eq 0) { throw "the display reports no smaller mode to switch to" }

$before = Get-DockPlacement
[Console]::WriteLine("before: screen $($before.Screen) dock $($before.Rect) off=$($before.Off) gap=$($before.Gap)")
Check "dock centred to begin with" ([Math]::Abs($before.Off) -le 1) "off by $($before.Off)"

try {
    $rc = [RT]::Set($other[0], $other[1])
    Check "switched to $($other[0])x$($other[1])" ($rc -eq 0) "ChangeDisplaySettingsEx=$rc"
    Start-Sleep -Milliseconds $SettleMs

    $during = Get-DockPlacement
    [Console]::WriteLine("during: screen $($during.Screen) dock $($during.Rect) off=$($during.Off) gap=$($during.Gap)")
    Check "screen actually changed" ($during.Screen -ne $before.Screen) $during.Screen
    Check "dock centred on the new screen" ([Math]::Abs($during.Off) -le 1) "off by $($during.Off)"
    Check "dock same height above the work area" ($during.Gap -eq $before.Gap) "gap $($during.Gap), was $($before.Gap)"
}
finally {
    $rc = [RT]::Restore()
    Start-Sleep -Milliseconds $SettleMs
}

Check "restored the original mode" ($rc -eq 0) "ChangeDisplaySettingsEx=$rc"
$after = Get-DockPlacement
[Console]::WriteLine("after:  screen $($after.Screen) dock $($after.Rect) off=$($after.Off) gap=$($after.Gap)")
Check "screen back" ($after.Screen -eq $before.Screen) $after.Screen
Check "dock back where it started" ($after.Rect -eq $before.Rect) $after.Rect

if ($failures -gt 0) { throw "$failures check(s) failed" }
[Console]::WriteLine("PASS - the dock followed the screen there and back.")
