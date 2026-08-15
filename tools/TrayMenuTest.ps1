# Drives the tray icon's right-click menu the way a user would, and checks what it actually did.
#
# Finds the icon via TrayButtons.ps1 (which identifies it by owning window handle, not by tooltip
# text), right-clicks it, reads the menu items, then exercises both commands: hide/show the dock,
# and exit. Exit is checked twice over - the process must be gone AND the icon must be gone with
# it, because an icon that is not explicitly removed lingers in the notification area as a dead
# entry until something makes the shell re-poll its owner.
#
# The items are read through the menu API (MN_GETHMENU -> GetMenuStringW / GetMenuItemRect), not
# UI Automation. UIA returns an empty tree for a popup menu that is demonstrably on screen with
# three items in it; the menu API returns all three with screen rects and their checked state.
# That is the same lesson the tray icons themselves taught - UIA does not model these shell-level
# windows, and going at the underlying Win32 object directly is both simpler and truthful.
#
# [Console]::WriteLine inside functions rather than Write-Output: a PowerShell function returns
# everything on its output stream, so Write-Output here is captured by the caller as a return
# value instead of being printed.

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class TM {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    public static void Left()  { mouse_event(0x0002,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x0004,0,0,0,IntPtr.Zero); }
    public static void Right() { mouse_event(0x0008,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x0010,0,0,0,IntPtr.Zero); }
    public static void Escape() { keybd_event(0x1B,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(0x1B,0,2,IntPtr.Zero); }

    [DllImport("user32.dll")] static extern bool EnumWindows(Proc cb, IntPtr p);
    delegate bool Proc(IntPtr h, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuStringW(IntPtr m, uint i, StringBuilder s, int n, uint f);
    [DllImport("user32.dll")] static extern uint GetMenuState(IntPtr m, uint i, uint f);
    [DllImport("user32.dll")] static extern bool GetMenuItemRect(IntPtr h, IntPtr m, uint i, out RECT r);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public struct Item { public string Text; public bool Checked; public int X, Y; }

    const uint MN_GETHMENU   = 0x01E1;
    const uint MF_BYPOSITION = 0x0400;
    const uint MF_CHECKED    = 0x0008;
    const uint MF_SEPARATOR  = 0x0800;

    /// The popup menu that is actually on screen.
    ///
    /// FindWindow("#32768") is not good enough: menu windows are cached and reused, so several
    /// exist at once and the first in z-order is usually a stale one parked at 0,0 with no items
    /// in it. Enumerate and take the one that is both visible and somewhere real.
    public static IntPtr VisibleMenu() {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) => {
            var c = new StringBuilder(64);
            GetClassNameW(h, c, 64);
            if (c.ToString() != "#32768" || !IsWindowVisible(h)) return true;
            RECT r;
            if (!GetWindowRect(h, out r) || (r.L == 0 && r.T == 0)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// Selectable items of the on-screen menu, separators dropped, with the screen point to click.
    public static Item[] Items() {
        var list = new List<Item>();
        IntPtr wnd = VisibleMenu();
        if (wnd == IntPtr.Zero) return list.ToArray();
        IntPtr menu = SendMessageW(wnd, MN_GETHMENU, IntPtr.Zero, IntPtr.Zero);
        if (menu == IntPtr.Zero) return list.ToArray();

        int count = GetMenuItemCount(menu);
        for (uint i = 0; i < count; i++) {
            uint state = GetMenuState(menu, i, MF_BYPOSITION);
            if ((state & MF_SEPARATOR) != 0) continue;
            var sb = new StringBuilder(256);
            GetMenuStringW(menu, i, sb, 256, MF_BYPOSITION);
            RECT r;
            if (!GetMenuItemRect(wnd, menu, i, out r)) continue;
            list.Add(new Item {
                Text = sb.ToString(),
                Checked = (state & MF_CHECKED) != 0,
                X = (r.L + r.R) / 2,
                Y = (r.T + r.B) / 2,
            });
        }
        return list.ToArray();
    }
}
'@

$failures = 0

function Move-To($x, $y) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]$x), ([int]$y)
    Start-Sleep -Milliseconds 250
}

# A menu left open from an earlier step blocks the dock's UI thread and swallows the next click,
# so every step starts from a known-closed state rather than assuming one.
function Close-AnyMenu {
    for ($i = 0; $i -lt 3; $i++) {
        if ([TM]::VisibleMenu() -eq [IntPtr]::Zero) { return }
        [TM]::Escape()
        Start-Sleep -Milliseconds 300
    }
}

function Open-TrayMenu {
    Close-AnyMenu
    $btn = & "$PSScriptRoot\TrayButtons.ps1" -OpenOverflow
    if (-not $btn.Found) { throw "tray icon not found: $($btn.Reason)" }
    Move-To $btn.X $btn.Y
    [TM]::Right()
    Start-Sleep -Milliseconds 800
}

function Invoke-MenuItem($items, $pattern) {
    $item = $items | Where-Object { $_.Text -like $pattern } | Select-Object -First 1
    if (-not $item) { throw "no menu item matching '$pattern'" }
    Move-To $item.X $item.Y
    [TM]::Left()
    Start-Sleep -Milliseconds 900
}

$dock = [IntPtr](& "$PSScriptRoot\DockHwnd.ps1")
$saved = [System.Windows.Forms.Cursor]::Position

try {
    # --- the menu itself ---------------------------------------------------------------------
    Open-TrayMenu
    $items = @([TM]::Items())
    Write-Output ("  menu items: {0}" -f (($items | ForEach-Object {
        "'" + $_.Text + "'" + $(if ($_.Checked) { " (ticked)" } else { "" }) }) -join ", "))
    if ($items.Count -lt 2) { Write-Output "  FAIL - menu did not open or is missing items"; $failures++ }

    $show = $items | Where-Object { $_.Text -like "*Dock*" } | Select-Object -First 1
    if ($show -and -not $show.Checked) { Write-Output "  FAIL - the dock is visible, so the item should be ticked"; $failures++ }

    # --- hide ---------------------------------------------------------------------------------
    Invoke-MenuItem $items "*Dock*"
    $visible = [TM]::IsWindowVisible($dock)
    Write-Output ("  picked the show item once:  dock visible = {0}" -f $visible)
    if ($visible) { Write-Output "  FAIL - the dock should be hidden"; $failures++ }

    # --- show again ---------------------------------------------------------------------------
    Open-TrayMenu
    $items = @([TM]::Items())
    $show = $items | Where-Object { $_.Text -like "*Dock*" } | Select-Object -First 1
    if ($show -and $show.Checked) { Write-Output "  FAIL - the dock is hidden, so the tick should be gone"; $failures++ }
    Invoke-MenuItem $items "*Dock*"
    $visible = [TM]::IsWindowVisible($dock)
    Write-Output ("  picked it a second time:    dock visible = {0}" -f $visible)
    if (-not $visible) { Write-Output "  FAIL - the dock should be back"; $failures++ }

    # --- exit ---------------------------------------------------------------------------------
    Open-TrayMenu
    Invoke-MenuItem @([TM]::Items()) "*退出*"
    Start-Sleep -Seconds 2

    $running = @(Get-Process FluidDock -ErrorAction SilentlyContinue).Count
    Write-Output ("  picked exit: {0} process(es) left" -f $running)
    if ($running -ne 0) { Write-Output "  FAIL - exit did not close the app"; $failures++ }

    $stale = & "$PSScriptRoot\TrayButtons.ps1" -OpenOverflow
    if ($stale.Found) { Write-Output "  FAIL - the icon is still in the tray after exit"; $failures++ }
    else { Write-Output "  the icon was removed from the tray" }
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}

Write-Output ""
if ($failures -eq 0) { Write-Output "PASS - menu opens, both commands work, exit cleans up." }
else { Write-Output "FAIL - $failures problem(s)." }
