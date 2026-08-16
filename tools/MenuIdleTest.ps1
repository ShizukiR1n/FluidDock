# Does the settings panel cost anything while it is just sitting there?
#
# The same question IdleTest asks of the dock, and for the same reason: Composition keeps
# requesting a per-frame tick for as long as any property is under animation control, so an
# animation that settles without being released costs ~240 wake-ups a second on this display,
# forever. The panel runs far more of them than the dock does - a highlight that springs between
# rows, a switch knob, a segmented selection, a scroll, and the open and close of the panel
# itself - and every one of them goes through AnimatedProperty precisely so that none of them can
# leave the tick behind.
#
# Runs in five phases, and they can be run one at a time:
#
#   .\MenuIdleTest.ps1                    all five, about two and a half minutes
#   .\MenuIdleTest.ps1 -Phase Hover       just that one
#
# The split is not cosmetic. The panel's state lives in the running app, not in this script, so
# each phase picks up exactly where the last one left off - which matters because an automation
# harness that kills a shell after half a minute would otherwise make this file unrunnable. The
# phases are ordered and each assumes the one before it: Open expects the panel closed, Hover
# expects it open.
#
# This changes nothing on disk. It only hovers.
#
# Kept ASCII-only: PowerShell 5.1 parses .ps1 as the system ANSI code page without a UTF-8 BOM.

param(
    [ValidateSet("All", "Baseline", "Open", "Hover", "Left", "Closed")]
    [string] $Phase = "All",
    [int] $Seconds = 20,
    [string] $Exe = "D:\AI\work space\win\src\FluidDock\bin\Debug\net9.0-windows10.0.19041.0\FluidDock.exe"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MI {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string w);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    // Wrapped so C# passes a real null; PowerShell 5.1 coerces $null to "" for string parameters.
    public static IntPtr Menu() { return FindWindowW("FluidDockMenu", null); }
    public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
    public static bool Visible(IntPtr h) { return IsWindowVisible(h); }
    public static void Escape() { keybd_event(0x1B,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(0x1B,0,2,IntPtr.Zero); }
}
'@

if (-not (Get-Process FluidDock -ErrorAction SilentlyContinue)) {
    Start-Process $Exe
    Start-Sleep -Seconds 3
}
$process = Get-Process FluidDock | Select-Object -First 1

$away = New-Object System.Drawing.Point 40, 40
$failures = 0

# Two-second buckets, for the same reason IdleTest uses them: a garbage collection moves one
# bucket, a leaked per-frame tick moves all of them, and a single average cannot tell the two
# apart. The verdict needs both numbers, because neither is enough on its own:
#
#   how many buckets saw any work    a leaked tick lands in every one; a collection or a stray
#                                    window message lands in one or two
#   milliseconds per second          a leaked tick costs about 19 ms/s on this display - that is
#                                    the 190ms-per-10s AnimatedProperty exists to prevent - and an
#                                    idle panel being woken occasionally costs about 2
#
# A median was the original rule and it does not work here. TotalProcessorTime is quantised to the
# scheduler's ~15.6ms, so every bucket reads 0 or 16 and nothing in between; with four buckets,
# two stray wake-ups put the median on 16 and report a perfectly idle panel as leaking. What an
# open, untouched panel actually looks like, measured over 40 seconds:
#
#   16 16 0 0 0 16 16 0 0 0 0 0 0 0 0 0 16 0 0 0     80 ms / 40s = 2.0 ms/s, 5 of 20 buckets busy
#
# Hence the default of 20 seconds rather than 8: at four buckets the fraction is too coarse to
# mean anything.
function Sample($label, $seconds) {
    $buckets = [Math]::Max([int]($seconds / 2), 1)
    $samples = @()
    for ($i = 0; $i -lt $buckets; $i++) {
        $process.Refresh(); $a = $process.TotalProcessorTime
        Start-Sleep -Seconds 2
        $process.Refresh()
        $samples += ($process.TotalProcessorTime - $a).TotalMilliseconds
    }
    $total = ($samples | Measure-Object -Sum).Sum
    $busy = @($samples | Where-Object { $_ -gt 0 }).Count
    $rate = $total / ($buckets * 2)

    $leaking = ($busy * 2 -gt $buckets) -and ($rate -gt 6)
    if ($leaking) { $script:failures++ }
    Write-Output ("{0,-34} {1,5:N0} ms / {2,2}s = {3,4:N1} ms/s   busy {4,2}/{5,-2}  {6}" -f `
        $label, $total, ($buckets * 2), $rate, $busy, $buckets, $(if ($leaking) { "LEAK" } else { "ok" }))
}

function Move-To($x, $y) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]$x), ([int]$y)
    Start-Sleep -Milliseconds 200
}

function Panel-Origin {
    $menu = [MI]::Menu()
    if ($menu -eq [IntPtr]::Zero -or -not [MI]::Visible($menu)) { throw "the panel is not open" }
    $r = [MI]::Rect($menu)
    @{ L = $r.L + 34; T = $r.T + 34 }   # MenuTheme.ShadowMargin
}

function Phase-Baseline {
    [System.Windows.Forms.Cursor]::Position = $away
    Start-Sleep -Milliseconds 800
    Sample "baseline, panel closed" $Seconds
}

function Phase-Open {
    & "$PSScriptRoot\TrayToggle.ps1" -Menu
    Start-Sleep -Milliseconds 800
    [void](Panel-Origin)
    [System.Windows.Forms.Cursor]::Position = $away
    Start-Sleep -Seconds 2
    Sample "panel open, pointer away" $Seconds
}

function Phase-Hover {
    $o = Panel-Origin
    # Down the rows and back, so the highlight springs, resizes and fades several times over.
    foreach ($pass in 1..3) {
        foreach ($rowY in 50, 116, 180, 260, 324, 400, 480) {
            Move-To ($o.L + 172) ($o.T + 54 + $rowY)
        }
    }
    Start-Sleep -Seconds 2
    Sample "highlight travelled, now parked" $Seconds
}

function Phase-Left {
    [void](Panel-Origin)
    [System.Windows.Forms.Cursor]::Position = $away
    Start-Sleep -Seconds 2
    Sample "pointer left the panel" $Seconds
}

function Phase-Closed {
    [MI]::Escape()
    Start-Sleep -Seconds 2
    if ([MI]::Visible([MI]::Menu())) { throw "the panel did not close" }
    Sample "panel closed again" $Seconds
}

$saved = [System.Windows.Forms.Cursor]::Position
try {
    switch ($Phase) {
        "All"      { Phase-Baseline; Phase-Open; Phase-Hover; Phase-Left; Phase-Closed }
        "Baseline" { Phase-Baseline }
        "Open"     { Phase-Open }
        "Hover"    { Phase-Hover }
        "Left"     { Phase-Left }
        "Closed"   { Phase-Closed }
    }
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}

if ($Phase -eq "All" -or $Phase -eq "Closed") {
    $process.Refresh()
    Write-Output ""
    Write-Output ("working set {0:N1} MB   private {1:N1} MB   threads {2}   handles {3}" -f `
        ($process.WorkingSet64 / 1MB), ($process.PrivateMemorySize64 / 1MB), $process.Threads.Count, $process.HandleCount)
}

Write-Output ""
if ($failures -eq 0) { Write-Output "MenuIdleTest/$Phase PASS - nothing left ticking." }
else { Write-Output "MenuIdleTest/$Phase FAIL - $failures sample(s) left the process ticking." }
