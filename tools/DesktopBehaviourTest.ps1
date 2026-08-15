# Walks the dock through the desktop interactions that matter, capturing after each one.
#
# Runs as a single script on purpose. Doing these steps as separate commands let the console
# window take the foreground between them, which re-ordered windows and made earlier readings
# describe the harness rather than the dock.

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Desk {
    [DllImport("user32.dll")] static extern void keybd_event(byte k, byte s, uint f, IntPtr e);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

    public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassNameW(h, s, 256); return s.ToString(); }

    public static void WinD() {
        keybd_event(0x5B, 0, 0, IntPtr.Zero); keybd_event(0x44, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(0x44, 0, 2, IntPtr.Zero); keybd_event(0x5B, 0, 2, IntPtr.Zero);
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(50);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }

    public static void DragFrom(int x1, int y1, int x2, int y2) {
        SetCursorPos(x1, y1); System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        for (int i = 1; i <= 20; i++) {
            SetCursorPos(x1 + (x2 - x1) * i / 20, y1 + (y2 - y1) * i / 20);
            System.Threading.Thread.Sleep(10);
        }
    }

    public static void Release() { mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }

    /// Names any visible desktop-ish window sitting above the dock.
    public static string Above(IntPtr dock) {
        var sb = new StringBuilder();
        IntPtr h = GetWindow(dock, 0);
        int i = 0;
        while (h != IntPtr.Zero && h != dock && i < 500) {
            if (IsWindowVisible(h)) {
                string c = Cls(h);
                if (c == "Progman" || c == "WorkerW") sb.Append(c).Append(' ');
            }
            h = GetWindow(h, 2); i++;
        }
        return sb.Length == 0 ? "none" : sb.ToString().Trim();
    }
}
'@

$dock = & "$PSScriptRoot\DockHwnd.ps1"
$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$mid = [int]($work.Width / 2)
$root = Split-Path $PSScriptRoot

function Shot($tag) {
    & "$PSScriptRoot\Capture.ps1" -Out "$root\_beh_$tag.png" -X 700 -Y 950 -W 520 -H 95 | Out-Null
    Write-Output ("{0,-22} foreground={1,-24} desktop-above-dock={2}" -f $tag, [Desk]::Cls([Desk]::GetForegroundWindow()), [Desk]::Above($dock))
}

$saved = [System.Windows.Forms.Cursor]::Position
try {
    [Desk]::WinD()
    Start-Sleep -Milliseconds 2000
    Shot "1-after-wind"

    # A plain click on empty wallpaper, well away from the dock.
    [Desk]::Click(200, 300)
    Start-Sleep -Milliseconds 900
    Shot "2-after-click"

    [Desk]::DragFrom(($mid - 500), ($work.Bottom - 400), ($mid + 400), ($work.Bottom - 30))
    Start-Sleep -Milliseconds 400
    Shot "3-during-drag"

    [Desk]::Release()
    Start-Sleep -Milliseconds 900
    Shot "4-after-drag"
}
finally {
    [Desk]::Release()
    [System.Windows.Forms.Cursor]::Position = $saved
}
