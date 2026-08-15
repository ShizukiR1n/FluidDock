# Locates FluidDock's notification-area button by reading the shell's toolbar directly.
#
# UI Automation was the wrong tool: it exposes Shell_TrayWnd's children as a handful of Panes and
# never descends into the ToolbarWindow32 that actually holds the icons, so a search for our icon
# came back empty from a tray that visibly had it. The toolbar itself answers TB_BUTTONCOUNT and
# TB_GETBUTTON, and each button's dwData points at a TRAYDATA carrying the owning HWND - which
# means the icon can be identified by window handle instead of by matching tooltip text.
#
# The structures live in Explorer's address space, so reading them needs a scratch allocation
# there and ReadProcessMemory to pull the results back.
#
# Outputs an object with Found / InOverflow / X / Y, or Found=$false.
#
# -OpenOverflow is usually required. Explorer only populates the overflow flyout's toolbar while
# the flyout is showing: with it closed, TB_BUTTONCOUNT comes back one short and the missing entry
# is the most recently added icon - ours. That reads exactly like "the icon was never registered".

param([switch] $OpenOverflow)

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class TrayBtn {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr p, IntPtr a, string c, string n);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool MapWindowPoints(IntPtr from, IntPtr to, ref RECT pt, uint count);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, IntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, IntPtr size, uint type);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    // x64 layout: the 6 reserved bytes are the padding that aligns dwData to 8. Getting this
    // wrong shifts dwData and makes every TRAYDATA read garbage.
    [StructLayout(LayoutKind.Sequential)]
    struct TBBUTTON {
        public int iBitmap; public int idCommand;
        public byte fsState; public byte fsStyle;
        public byte r0, r1, r2, r3, r4, r5;
        public IntPtr dwData; public IntPtr iString;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TRAYDATA { public IntPtr hwnd; public uint uID; public uint uCallbackMessage; public uint res0, res1; public IntPtr hIcon; }

    const uint TB_BUTTONCOUNT = 0x0418;
    const uint TB_GETBUTTON   = 0x0417;
    const uint TB_GETITEMRECT = 0x041D;

    static T Read<T>(IntPtr proc, IntPtr addr) where T : struct {
        int size = Marshal.SizeOf<T>();
        byte[] buf = new byte[size];
        IntPtr got;
        if (!ReadProcessMemory(proc, addr, buf, (IntPtr)size, out got)) return default(T);
        GCHandle h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { return Marshal.PtrToStructure<T>(h.AddrOfPinnedObject()); }
        finally { h.Free(); }
    }

    /// Screen rect of the toolbar button owned by <paramref name="owner"/>, or an empty rect.
    public static RECT Find(IntPtr toolbar, IntPtr owner) {
        var none = new RECT();
        if (toolbar == IntPtr.Zero) return none;

        uint pid; GetWindowThreadProcessId(toolbar, out pid);
        IntPtr proc = OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid);   // VM_OPERATION|VM_READ|VM_WRITE
        if (proc == IntPtr.Zero) return none;

        IntPtr scratch = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)4096, 0x1000 | 0x2000, 0x04);
        if (scratch == IntPtr.Zero) { CloseHandle(proc); return none; }

        try {
            int count = (int)SendMessageW(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < count; i++) {
                SendMessageW(toolbar, TB_GETBUTTON, (IntPtr)i, scratch);
                TBBUTTON btn = Read<TBBUTTON>(proc, scratch);
                TRAYDATA data = Read<TRAYDATA>(proc, btn.dwData);
                if (data.hwnd != owner) continue;

                SendMessageW(toolbar, TB_GETITEMRECT, (IntPtr)i, scratch);
                RECT r = Read<RECT>(proc, scratch);
                MapWindowPoints(toolbar, IntPtr.Zero, ref r, 2);
                return r;
            }
            return none;
        }
        finally {
            VirtualFreeEx(proc, scratch, IntPtr.Zero, 0x8000);
            CloseHandle(proc);
        }
    }

    public static IntPtr VisibleToolbar() {
        IntPtr tray = FindWindowW("Shell_TrayWnd", null);
        IntPtr notify = FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        IntPtr pager = FindWindowExW(notify, IntPtr.Zero, "SysPager", null);
        return FindWindowExW(pager, IntPtr.Zero, "ToolbarWindow32", null);
    }

    public static IntPtr OverflowToolbar() {
        IntPtr overflow = FindWindowW("NotifyIconOverflowWindow", null);
        return FindWindowExW(overflow, IntPtr.Zero, "ToolbarWindow32", null);
    }

    /// The chevron that expands the hidden icons. Found by class rather than by hardcoded
    /// coordinates, which shift every time an icon is added or removed.
    public static IntPtr Chevron() {
        IntPtr tray = FindWindowW("Shell_TrayWnd", null);
        IntPtr notify = FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        return FindWindowExW(notify, IntPtr.Zero, "Button", null);
    }

    public static IntPtr DockTrayWindow() { return FindWindowW("FluidDockTray", null); }

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);

    public static bool OverflowShown() {
        return IsWindowVisible(FindWindowW("NotifyIconOverflowWindow", null));
    }

    /// Checked against IsWindowVisible before calling, never fired blind - the chevron is a
    /// toggle, and pressing a toggle without reading its state is how the last three of these
    /// tests ended up measuring the opposite of what they claimed.
    public static void ClickChevron() {
        RECT r;
        IntPtr c = Chevron();
        if (c == IntPtr.Zero || !GetWindowRect(c, out r)) return;
        SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        System.Threading.Thread.Sleep(200);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
'@

$owner = [TrayBtn]::DockTrayWindow()
if ($owner -eq [IntPtr]::Zero) {
    [pscustomobject]@{ Found = $false; Reason = "FluidDockTray window not present" }
    return
}

$visible = [TrayBtn]::Find([TrayBtn]::VisibleToolbar(), $owner)
$inOverflow = $false
$rect = $visible
if ($visible.Right -eq 0 -and $visible.Bottom -eq 0) {
    # Close it first if it is already open. Explorer fills the overflow toolbar when the flyout
    # opens and does not refresh it while it stays open, so a flyout left open from an earlier
    # step lists whatever was registered back then - which, after restarting the dock, is an icon
    # that no longer exists and none of the one that does.
    if ($OpenOverflow) {
        if ([TrayBtn]::OverflowShown()) {
            [TrayBtn]::ClickChevron()
            Start-Sleep -Milliseconds 500
        }
        [TrayBtn]::ClickChevron()
        Start-Sleep -Milliseconds 900
    }
    $rect = [TrayBtn]::Find([TrayBtn]::OverflowToolbar(), $owner)
    $inOverflow = $true
}

if ($rect.Right -eq 0 -and $rect.Bottom -eq 0) {
    [pscustomobject]@{ Found = $false; Reason = "no toolbar button owned by 0x{0:X}" -f [int64]$owner }
} else {
    [pscustomobject]@{
        Found = $true
        InOverflow = $inOverflow
        X = [int](($rect.Left + $rect.Right) / 2)
        Y = [int](($rect.Top + $rect.Bottom) / 2)
    }
}
