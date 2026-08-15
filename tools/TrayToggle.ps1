# Toggles the dock's visibility through the tray, the way a user would.
#
# Shared by TrayMenuTest and HideShowTest so there is one definition of "what the user does",
# rather than two that drift apart.
#
# -DoubleClick uses the icon's double-click handler instead of the menu. Both reach the same
# Toggle(), but only one of them is what the menu test exercises, so the other needs covering too.
#
# Kept ASCII-only on purpose: Windows PowerShell 5.1 parses .ps1 as the system ANSI code page
# unless the file carries a UTF-8 BOM, so a Chinese literal here would be silently corrupted.
# The show item is matched on its "Dock" substring, which survives either decoding.

param([switch] $DoubleClick)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class TT {
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    public static void Left()  { mouse_event(0x0002,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x0004,0,0,0,IntPtr.Zero); }
    public static void Right() { mouse_event(0x0008,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x0010,0,0,0,IntPtr.Zero); }
    public static void Escape(){ keybd_event(0x1B,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(0x1B,0,2,IntPtr.Zero); }

    /// Two clicks inside the system double-click interval, so the shell reports WM_LBUTTONDBLCLK
    /// rather than two singles.
    public static void DoubleLeft() {
        mouse_event(0x0002,0,0,0,IntPtr.Zero); mouse_event(0x0004,0,0,0,IntPtr.Zero);
        System.Threading.Thread.Sleep((int)(GetDoubleClickTime() / 4));
        mouse_event(0x0002,0,0,0,IntPtr.Zero); mouse_event(0x0004,0,0,0,IntPtr.Zero);
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(Proc cb, IntPtr p);
    delegate bool Proc(IntPtr h, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuStringW(IntPtr m, uint i, StringBuilder s, int n, uint f);
    [DllImport("user32.dll")] static extern uint GetMenuState(IntPtr m, uint i, uint f);
    [DllImport("user32.dll")] static extern bool GetMenuItemRect(IntPtr h, IntPtr m, uint i, out RECT r);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public struct Item { public string Text; public bool Checked; public int X, Y; }

    /// The popup menu that is actually on screen. Menu windows are cached and reused, so several
    /// exist at once and the first in z-order is usually a stale one parked at 0,0.
    public static IntPtr VisibleMenu() {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) => {
            var c = new StringBuilder(64);
            GetClassNameW(h, c, 64);
            if (c.ToString() != "#32768" || !IsWindowVisible(h)) return true;
            RECT r;
            if (!GetWindowRect(h, out r) || (r.L == 0 && r.T == 0)) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    /// Selectable items of the on-screen menu, separators dropped, with the point to click.
    /// Read through the menu API rather than UI Automation, which returns an empty tree for a
    /// popup menu that is demonstrably on screen.
    public static Item[] Items() {
        var list = new List<Item>();
        IntPtr wnd = VisibleMenu();
        if (wnd == IntPtr.Zero) return list.ToArray();
        IntPtr menu = SendMessageW(wnd, 0x01E1, IntPtr.Zero, IntPtr.Zero);   // MN_GETHMENU
        if (menu == IntPtr.Zero) return list.ToArray();
        int count = GetMenuItemCount(menu);
        for (uint i = 0; i < count; i++) {
            uint state = GetMenuState(menu, i, 0x0400);                      // MF_BYPOSITION
            if ((state & 0x0800) != 0) continue;                             // MF_SEPARATOR
            var sb = new StringBuilder(256);
            GetMenuStringW(menu, i, sb, 256, 0x0400);
            RECT r;
            if (!GetMenuItemRect(wnd, menu, i, out r)) continue;
            list.Add(new Item { Text = sb.ToString(), Checked = (state & 0x0008) != 0,
                                X = (r.L + r.R) / 2, Y = (r.T + r.B) / 2 });
        }
        return list.ToArray();
    }
}
'@

function Move-To($x, $y) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]$x), ([int]$y)
    Start-Sleep -Milliseconds 250
}

$saved = [System.Windows.Forms.Cursor]::Position
try {
    # A menu left open from an earlier step blocks the dock's UI thread and swallows the next
    # click, so start from a known-closed state rather than assuming one.
    for ($i = 0; $i -lt 3 -and [TT]::VisibleMenu() -ne [IntPtr]::Zero; $i++) {
        [TT]::Escape(); Start-Sleep -Milliseconds 300
    }

    $btn = & "$PSScriptRoot\TrayButtons.ps1" -OpenOverflow
    if (-not $btn.Found) { throw "tray icon not found: $($btn.Reason)" }
    Move-To $btn.X $btn.Y

    if ($DoubleClick) {
        [TT]::DoubleLeft()
        Start-Sleep -Milliseconds 700
        return
    }

    [TT]::Right()
    Start-Sleep -Milliseconds 800
    $item = @([TT]::Items()) | Where-Object { $_.Text -like "*Dock*" } | Select-Object -First 1
    if (-not $item) { throw "no show/hide item in the tray menu" }
    Move-To $item.X $item.Y
    [TT]::Left()
    Start-Sleep -Milliseconds 800
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}
