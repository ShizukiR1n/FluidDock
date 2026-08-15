# Opens the tray menu and reports, step by step, what is actually on screen.
#
# TrayMenuTest reports an empty menu while a screenshot shows the menu rendered correctly, so the
# fault is somewhere between "menu is up" and "UIA lists its items". This narrows it down: it
# prints every #32768 window with its visibility and rect, then what UIA sees in the one picked.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class MP {
    [DllImport("user32.dll")] static extern bool EnumWindows(Proc cb, IntPtr p);
    delegate bool Proc(IntPtr h, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public static void Right() { mouse_event(0x0008,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x0010,0,0,0,IntPtr.Zero); }

    /// Every menu window on the desktop, described. Menus are cached and reused, so there are
    /// normally several and only one of them is the one the user is looking at.
    public static string[] Menus() {
        var list = new List<string>();
        EnumWindows((h, _) => {
            var c = new StringBuilder(64);
            GetClassNameW(h, c, 64);
            if (c.ToString() != "#32768") return true;
            RECT r; GetWindowRect(h, out r);
            uint pid; GetWindowThreadProcessId(h, out pid);
            list.Add(string.Format("0x{0:X}  visible={1,-5}  pid={2,-6}  rect={3},{4} {5}x{6}",
                (long)h, IsWindowVisible(h), pid, r.L, r.T, r.R - r.L, r.B - r.T));
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }
}
'@

$btn = & "$PSScriptRoot\TrayButtons.ps1" -OpenOverflow
Write-Output ("tray button: found={0} overflow={1} at {2},{3}" -f $btn.Found, $btn.InOverflow, $btn.X, $btn.Y)
if (-not $btn.Found) { return }

[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]$btn.X), ([int]$btn.Y)
Start-Sleep -Milliseconds 300
[MP]::Right()
Start-Sleep -Milliseconds 900

Write-Output ""
Write-Output "menu windows on the desktop:"
foreach ($m in [MP]::Menus()) { Write-Output "  $m" }

# Screenshot regardless - a picture settles "did it open at all" without another round trip.
$bmp = New-Object System.Drawing.Bitmap 1920, 1080
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
$bmp.Save("$PSScriptRoot\..\_probe.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ""
Write-Output "screenshot -> _probe.png"
