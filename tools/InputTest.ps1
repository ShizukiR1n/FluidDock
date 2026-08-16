# Input responsiveness test.
#
# Drives the cursor across the dock at a known, fixed rate and then asks the dock how many of
# those moves it actually handled. The gap between the two is the whole story: Windows coalesces
# WM_MOUSEMOVE, so a handler that is slow does not merely add latency, it silently discards
# input samples. A dock that sees 300 of the 500 moves you made is a dock that is animating
# from a 40%-decimated signal, and that is what "stutter" looks like from the inside.
#
# The dock only writes its log at exit, so this quits it with the Ctrl+Alt+Q hotkey rather than
# killing it - Stop-Process -Force skips the finally block and the log never lands.

#
# -Exe picks which build to measure. It used to be hardcoded to the dev build, which quietly made
# this the one test in the suite that never touched the shipped exe: run against a running package
# it would kill it, start the dev build, measure that, and report as if nothing had happened.
param(
    [int] $Samples = 500,
    [int] $IntervalMicros = 8000,  # 8ms = a 125Hz mouse. Ordinary hardware, not a stress test.
    [int] $PixelsPerSample = 3,    # 3px at 125Hz = 375px/s, an unhurried browse along the dock.
    [switch] $SendInput,
    [string] $Exe = "D:\AI\work space\win\src\FluidDock\bin\Release\net9.0-windows10.0.19041.0\FluidDock.exe"
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class Sweep {
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

    // x64 layout. The MOUSEINPUT union is 8-byte aligned because of its ULONG_PTR tail, so there
    // are 4 bytes of padding after `type` that have to be declared - without them the struct is
    // 32 bytes instead of 40, SendInput rejects the size, and the cursor silently never moves.
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public uint pad; public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

    public static void Hotkey(IntPtr h, int id) { PostMessageW(h, 0x0312, (IntPtr)id, IntPtr.Zero); }

    /// SendInput injects into the raw input thread, which is the path real mouse hardware takes.
    /// SetCursorPos does not - it writes the cursor position directly - so the two can disagree
    /// about how many WM_MOUSEMOVE a given motion produces. Worth having both.
    static void Inject(int x, int y) {
        var input = new INPUT {
            type = 0,
            dx = x * 65535 / GetSystemMetrics(0),
            dy = y * 65535 / GetSystemMetrics(1),
            dwFlags = 0x0001 | 0x8000,   // MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
        };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 0)
            throw new InvalidOperationException("SendInput rejected the input (size " + Marshal.SizeOf<INPUT>() + ")");
    }

    /// Ping-pongs across the dock at a fixed interval, held by spinning - Start-Sleep cannot
    /// resolve 8ms, and a jittery send rate would make the handled count meaningless.
    ///
    /// Moving by a whole pixel every sample matters: SetCursorPos to a position the cursor is
    /// already at produces no message, so a slow sweep with sub-pixel steps would look like
    /// coalescing when it is really just duplicate positions being dropped.
    public static void Run(int left, int right, int y, int samples, int micros, int pixelsPerSample, bool sendInput) {
        double ticksPerMicro = Stopwatch.Frequency / 1000000.0;
        long step = (long)(micros * ticksPerMicro);
        long next = Stopwatch.GetTimestamp();

        int x = left, direction = pixelsPerSample;
        for (int i = 0; i < samples; i++) {
            if (sendInput) Inject(x, y); else SetCursorPos(x, y);
            x += direction;
            if (x >= right || x <= left) direction = -direction;
            next += step;
            while (Stopwatch.GetTimestamp() < next) { }
        }
    }
}
'@

$exe = $Exe
$log = Join-Path (Split-Path $exe) "fluiddock.log"

Get-Process FluidDock -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $log) { Remove-Item $log }

$saved = [System.Windows.Forms.Cursor]::Position
$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea

# Icon centre line, derived the same way the dock derives it: work-area bottom, less the screen
# margin, less half an icon. The dock anchors the icon row's bottom edge there, not the window's.
# Guessing this from screen height put the cursor below the icons, where every move is
# hit-tested away as transparent and never arrives.
$y = [int]($work.Bottom - 12 - 48 / 2)
$mid = [int]($work.Width / 2)

Start-Process $exe
Start-Sleep -Seconds 3

$process = Get-Process FluidDock -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw "FluidDock did not start" }
$hwnd = & "$PSScriptRoot\DockHwnd.ps1"

try {
    # Enter from inside the dock so the first move is already a hover, not an arrival.
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ($mid - 200), $y
    Start-Sleep -Milliseconds 500

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    [Sweep]::Run(($mid - 200), ($mid + 200), $y, $Samples, $IntervalMicros, $PixelsPerSample, $SendInput.IsPresent)
    $elapsed = $clock.Elapsed.TotalSeconds
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}

Write-Output ("sent {0} moves at y={1} over {2:N2}s = {3:N0}/s" -f $Samples, $y, $elapsed, ($Samples / $elapsed))

[Sweep]::Hotkey($hwnd, 1)
$process.WaitForExit(5000) | Out-Null
Start-Sleep -Milliseconds 300

if (Test-Path $log) {
    Get-Content $log | Where-Object { $_ -match "mousemove|pump:" }
} else {
    Write-Output "no log written - the dock did not exit cleanly"
}
