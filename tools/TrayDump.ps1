# Dumps every notification-area toolbar button with the window that owns it.
#
# Diagnostic for TrayButtons.ps1: if the owner handles come back as zeroes the cross-process read
# is failing, and if they look like real handles but none of them is ours the icon genuinely is
# not registered. Those two failures are indistinguishable from "not found".

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class TrayDump {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr p, IntPtr a, string c, string n);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, IntPtr size, uint t, uint pr);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, IntPtr size, uint t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr s, out IntPtr r);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct TBBUTTON {
        public int iBitmap; public int idCommand;
        public byte fsState; public byte fsStyle;
        public byte r0, r1, r2, r3, r4, r5;
        public IntPtr dwData; public IntPtr iString;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct TRAYDATA { public IntPtr hwnd; public uint uID; public uint uCallbackMessage; public uint res0, res1; public IntPtr hIcon; }

    static string Cls(IntPtr h) { var s = new StringBuilder(128); GetClassNameW(h, s, 128); return s.ToString(); }

    public static string Dump(IntPtr toolbar, string label) {
        var sb = new StringBuilder();
        sb.AppendLine(label + "  toolbar 0x" + ((long)toolbar).ToString("X"));
        if (toolbar == IntPtr.Zero) { sb.AppendLine("  (not present)"); return sb.ToString(); }

        uint pid; GetWindowThreadProcessId(toolbar, out pid);
        IntPtr proc = OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid);
        sb.AppendLine("  explorer pid " + pid + ", OpenProcess -> 0x" + ((long)proc).ToString("X"));
        if (proc == IntPtr.Zero) { sb.AppendLine("  cannot open explorer, error " + Marshal.GetLastWin32Error()); return sb.ToString(); }

        IntPtr scratch = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)4096, 0x1000 | 0x2000, 0x04);
        int count = (int)SendMessageW(toolbar, 0x0418, IntPtr.Zero, IntPtr.Zero);
        sb.AppendLine("  TB_BUTTONCOUNT = " + count);

        for (int i = 0; i < count; i++) {
            SendMessageW(toolbar, 0x0417, (IntPtr)i, scratch);
            byte[] buf = new byte[Marshal.SizeOf<TBBUTTON>()];
            IntPtr got;
            if (!ReadProcessMemory(proc, scratch, buf, (IntPtr)buf.Length, out got)) {
                sb.AppendLine("    [" + i + "] TBBUTTON read failed, error " + Marshal.GetLastWin32Error());
                continue;
            }
            GCHandle gh = GCHandle.Alloc(buf, GCHandleType.Pinned);
            TBBUTTON btn = Marshal.PtrToStructure<TBBUTTON>(gh.AddrOfPinnedObject());
            gh.Free();

            byte[] tbuf = new byte[Marshal.SizeOf<TRAYDATA>()];
            TRAYDATA td = default(TRAYDATA);
            bool ok = ReadProcessMemory(proc, btn.dwData, tbuf, (IntPtr)tbuf.Length, out got);
            if (ok) {
                GCHandle th = GCHandle.Alloc(tbuf, GCHandleType.Pinned);
                td = Marshal.PtrToStructure<TRAYDATA>(th.AddrOfPinnedObject());
                th.Free();
            }
            sb.AppendLine("    [" + i + "] state 0x" + btn.fsState.ToString("X2")
                + "  dwData 0x" + ((long)btn.dwData).ToString("X")
                + "  owner 0x" + ((long)td.hwnd).ToString("X")
                + "  class " + (td.hwnd != IntPtr.Zero ? Cls(td.hwnd) : "-"));
        }

        VirtualFreeEx(proc, scratch, IntPtr.Zero, 0x8000);
        CloseHandle(proc);
        return sb.ToString();
    }

    public static IntPtr Visible() {
        IntPtr t = FindWindowW("Shell_TrayWnd", null);
        IntPtr n = FindWindowExW(t, IntPtr.Zero, "TrayNotifyWnd", null);
        IntPtr p = FindWindowExW(n, IntPtr.Zero, "SysPager", null);
        IntPtr tb = FindWindowExW(p, IntPtr.Zero, "ToolbarWindow32", null);
        // Some builds drop the SysPager level and hang the toolbar off TrayNotifyWnd directly.
        if (tb == IntPtr.Zero) tb = FindWindowExW(n, IntPtr.Zero, "ToolbarWindow32", null);
        return tb;
    }
    public static IntPtr Overflow() {
        return FindWindowExW(FindWindowW("NotifyIconOverflowWindow", null), IntPtr.Zero, "ToolbarWindow32", null);
    }
    public static IntPtr Ours() { return FindWindowW("FluidDockTray", null); }
}
'@

Write-Output ("our tray window: 0x{0:X}" -f [int64][TrayDump]::Ours())
Write-Output ""
Write-Output ([TrayDump]::Dump([TrayDump]::Visible(), "VISIBLE"))
Write-Output ([TrayDump]::Dump([TrayDump]::Overflow(), "OVERFLOW"))
