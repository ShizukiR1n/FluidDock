# Finds the dock's window handle.
#
# Get-Process().MainWindowHandle stopped working the moment the dock was reparented into the
# desktop: a child window is not a "main window", so the property silently returns 0 and every
# PostMessage built on it goes nowhere. Search by class name instead, which works whether the
# dock is floating on top or living inside the desktop.

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DockFind {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string cls, string window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string cls, string window);

    [DllImport("user32.dll")] static extern bool EnumWindows(Proc cb, IntPtr p);
    delegate bool Proc(IntPtr hwnd, IntPtr p);

    const string DockClass = "FluidDockWindow";

    public static IntPtr Find() {
        // Top level, i.e. AlwaysOnTop mode.
        IntPtr top = FindWindowW(DockClass, null);
        if (top != IntPtr.Zero) return top;

        IntPtr progman = FindWindowW("Progman", null);
        if (progman != IntPtr.Zero) {
            IntPtr child = FindWindowExW(progman, IntPtr.Zero, DockClass, null);
            if (child != IntPtr.Zero) return child;
        }

        // Explorer sometimes moves the icon view into a WorkerW; the dock follows it there.
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) => {
            if (FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            found = FindWindowExW(hwnd, IntPtr.Zero, DockClass, null);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);

        return found;
    }
}
'@

$hwnd = [DockFind]::Find()
if ($hwnd -eq [IntPtr]::Zero) { throw "FluidDock window not found - is it running?" }
$hwnd
