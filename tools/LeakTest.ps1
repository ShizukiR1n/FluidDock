# Hammers the hover transition and watches for anything that does not come back.
#
# The window region is rebuilt on every hover-out, and regions are GDI objects. SetWindowRgn is
# documented to take ownership, but "documented" and "measured" are different things, and a dock
# that runs all day cannot afford to lose a handle per hover. GetGuiResources is the only way to
# see GDI and USER objects - Process.HandleCount counts kernel handles and would miss this.

param([int] $Cycles = 60)

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Res {
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
    public static uint Gdi(IntPtr p)  { return GetGuiResources(p, 0); }
    public static uint User(IntPtr p) { return GetGuiResources(p, 1); }
}
'@

$process = Get-Process FluidDock -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw "FluidDock is not running" }
$handle = $process.Handle

(New-Object -ComObject Shell.Application).MinimizeAll()
Start-Sleep -Milliseconds 1500

$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$onDock = New-Object System.Drawing.Point ([int]($work.Width / 2)), ([int]($work.Bottom - 12 - 24))
$away = New-Object System.Drawing.Point 40, 40
$saved = [System.Windows.Forms.Cursor]::Position

function Snapshot($label) {
    [System.GC]::Collect()
    $process.Refresh()
    Write-Output ("{0,-14} gdi {1,5}   user {2,5}   handles {3,5}   private {4,7:N1} MB" -f `
        $label, [Res]::Gdi($handle), [Res]::User($handle), $process.HandleCount, ($process.PrivateMemorySize64 / 1MB))
}

try {
    [System.Windows.Forms.Cursor]::Position = $away
    Start-Sleep -Milliseconds 800
    Snapshot "before"

    # Each cycle is one hover-in and one hover-out, i.e. one region rebuild.
    for ($i = 0; $i -lt $Cycles; $i++) {
        [System.Windows.Forms.Cursor]::Position = $onDock
        Start-Sleep -Milliseconds 60
        [System.Windows.Forms.Cursor]::Position = $away
        Start-Sleep -Milliseconds 60
    }

    Start-Sleep -Seconds 2
    Snapshot "after $Cycles"
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}
