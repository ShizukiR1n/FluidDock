# Starts a rubber-band selection on the desktop, drags it across the dock, and screenshots
# mid-drag - then always releases the button.
#
# Exists because the artefact only appears while the desktop is actively painting a selection
# over the dock's window rectangle, which no static screenshot can show.

param(
    [Parameter(Mandatory = $true)][string] $Out
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Drag {
    [DllImport("user32.dll")] static extern void keybd_event(byte k, byte s, uint f, IntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

    public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassNameW(h, s, 256); return s.ToString(); }

    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);

    /// What window actually owns a screen point. Checking this beats checking the foreground:
    /// the drag only needs the desktop to be uncovered where the drag starts, and Win+D proved
    /// a poor way to arrange that - it is a toggle, and pressing it blind is a coin flip. Every
    /// earlier reading here that "the dock survived Win+D" was a toggle that went the other way.
    public static string AtPoint(int x, int y) {
        var p = new POINT(); p.x = x; p.y = y;
        return Cls(WindowFromPoint(p));
    }

    public static void Escape() {
        keybd_event(0x1B, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(0x1B, 0, 2, IntPtr.Zero);
    }

    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    public static void Down() { mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); }
    public static void Up()   { mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }
    public static void MoveTo(int x, int y, int steps) {
        int sx, sy; GetPos(out sx, out sy);
        for (int i = 1; i <= steps; i++) {
            SetCursorPos(sx + (x - sx) * i / steps, sy + (y - sy) * i / steps);
            System.Threading.Thread.Sleep(8);
        }
    }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    static void GetPos(out int x, out int y) { POINT p; GetCursorPos(out p); x = p.x; y = p.y; }
}
'@

$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$saved = [System.Windows.Forms.Cursor]::Position

# Start well left of the dock and well above it, then drag down-right so the band covers the
# dock's whole window rectangle, not just the icons.
$startX = [int]($work.Width / 2) - 500
$startY = $work.Bottom - 400
$endX = [int]($work.Width / 2) + 400
$endY = $work.Bottom - 30

try {
    [Drag]::Escape()
    (New-Object -ComObject Shell.Application).MinimizeAll()
    Start-Sleep -Milliseconds 1800

    $under = [Drag]::AtPoint($startX, $startY)
    if ($under -notin @("Progman", "WorkerW", "SHELLDLL_DefView", "SysListView32")) {
        throw "the drag would not start on the desktop - $under is there instead"
    }
    Write-Output "drag starts on: $under"

    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $startX, $startY
    Start-Sleep -Milliseconds 300
    [Drag]::Down()
    [Drag]::MoveTo($endX, $endY, 25)
    Start-Sleep -Milliseconds 400

    & "$PSScriptRoot\Capture.ps1" -Out $Out -X ($startX - 40) -Y ($startY - 20) -W 1000 -H 400
}
finally {
    [Drag]::Up()
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.Cursor]::Position = $saved
}
