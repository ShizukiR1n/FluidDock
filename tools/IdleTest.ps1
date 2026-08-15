# Idle cost regression test.
#
# The dock is meant to sit on screen all day, so anything it leaves running after an
# interaction is a battery leak that compounds. Two separate bugs already showed up here:
# a bounce and a hover each left a Composition property under animation control, and the
# compositor keeps requesting a per-frame tick for as long as that is true - ~240 wake-ups a
# second on this display, forever, for a dock that is standing still.
#
# Every sample below must come back at essentially zero.

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Poke {
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    public static void Hotkey(IntPtr h, int id) { PostMessageW(h, 0x0312, (IntPtr)id, IntPtr.Zero); }
}
'@

$process = Get-Process FluidDock -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw "FluidDock is not running" }
$hwnd = & "$PSScriptRoot\DockHwnd.ps1"

$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$dockX = [int]($work.Width / 2)

# Icon centre line: work-area bottom, less ScreenMargin, less half an icon. This was previously
# guessed from screen height and landed below the icons, outside the hit region - so every
# "hovering" sample in this file was measuring a dock the cursor had never actually touched.
$dockY = [int]($work.Bottom - 12 - 48 / 2)
$away = New-Object System.Drawing.Point 40, 40

$failures = 0

# Sampled in 2s buckets rather than as one average, because the average cannot tell the two
# failure shapes apart. A leaked per-frame tick burns CPU in every bucket - the original bug was
# ~190ms in all of them. A garbage collection burns one bucket and leaves the rest at zero, and
# a single 10s average reports both as "some milliseconds" and cries leak either way.
function Sample($label, $seconds) {
    $buckets = [Math]::Max([int]($seconds / 2), 1)
    $samples = @()

    for ($i = 0; $i -lt $buckets; $i++) {
        $process.Refresh(); $a = $process.TotalProcessorTime
        Start-Sleep -Seconds 2
        $process.Refresh()
        $samples += ($process.TotalProcessorTime - $a).TotalMilliseconds
    }

    # The median is the statistic that separates the two shapes. One garbage collection moves a
    # single bucket and leaves the median at zero; a leaked per-frame tick moves every bucket and
    # takes the median with it. A mean would flag the first and a "max" would flag it harder.
    $total = ($samples | Measure-Object -Sum).Sum
    $median = ($samples | Sort-Object)[[int]($buckets / 2)]
    $leaking = $median -gt 2

    if ($leaking) { $script:failures++ }

    Write-Output ("{0,-32} {1,5:N0} ms / {2,2}s   median bucket {3,4:N0} ms/2s   {4}" -f `
        $label, $total, ($buckets * 2), $median, $(if ($leaking) { "LEAK" } else { "ok" }))
}

[System.Windows.Forms.Cursor]::Position = $away
Start-Sleep -Milliseconds 800
Sample "baseline, untouched" 10

# Sweep across the dock, then leave. Exercises the cursor spring and the magnify in/out.
foreach ($pass in 1..3) {
    foreach ($x in ($dockX - 260)..($dockX + 260)) {
        if ($x % 4 -eq 0) {
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $x, $dockY
            Start-Sleep -Milliseconds 4
        }
    }
}
[System.Windows.Forms.Cursor]::Position = $away
Start-Sleep -Seconds 2
Sample "after hovering and leaving" 10

# Park on the dock and stop moving: magnified but static, still should not tick.
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $dockX, $dockY
Start-Sleep -Seconds 2
Sample "parked on the dock, magnified" 10
[System.Windows.Forms.Cursor]::Position = $away
Start-Sleep -Seconds 2

# Bounce on and off.
[Poke]::Hotkey($hwnd, 3)
Start-Sleep -Seconds 2
[Poke]::Hotkey($hwnd, 3)
Start-Sleep -Seconds 2
Sample "after a bounce" 10

Sample "settled" 15

$process.Refresh()
Write-Output ""
Write-Output ("working set {0:N1} MB   private {1:N1} MB   threads {2}   handles {3}" -f `
    ($process.WorkingSet64 / 1MB), ($process.PrivateMemorySize64 / 1MB), $process.Threads.Count, $process.HandleCount)
Write-Output ""
if ($failures -eq 0) { Write-Output "PASS - idle cost stays at zero after every interaction." }
else { Write-Output "FAIL - $failures sample(s) left the dock ticking." }
