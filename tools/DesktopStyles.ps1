# Dumps the style bits of every window in the desktop chain.
#
# The rubber-band hole is caused by WS_CLIPSIBLINGS on whichever desktop window paints the
# selection. Which window that is, and whether it also sets WS_CLIPCHILDREN, decides whether the
# dock can escape the problem by parenting one level deeper instead of shrinking its own region.
# Guessing this from documentation is how the last two attempts went wrong.
#
# The whole walk lives in C# on purpose: calling FindWindowW from PowerShell with $null for the
# title silently passes "" instead, which matches nothing and reports every window as missing.

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Sty {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr p, IntPtr a, string c, string n);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }

    static string Line(string label, IntPtr h) {
        if (h == IntPtr.Zero) return string.Format("{0,-24} MISSING", label);
        uint s = (uint)GetWindowLongW(h, -16);
        var f = new StringBuilder();
        if ((s & 0x04000000) != 0) f.Append("CLIPSIBLINGS ");
        if ((s & 0x02000000) != 0) f.Append("CLIPCHILDREN ");
        if ((s & 0x10000000) != 0) f.Append("VISIBLE");
        RECT r; GetWindowRect(h, out r);
        return string.Format("{0,-24} 0x{1:X8}  style 0x{2:X8}  {3,-38}  {4},{5} {6}x{7}",
            label, (long)h, s, f.ToString(), r.l, r.t, r.r - r.l, r.b - r.t);
    }

    public static string Dump() {
        var sb = new StringBuilder();
        IntPtr progman = FindWindowW("Progman", null);
        sb.AppendLine(Line("Progman", progman));

        IntPtr defview = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        sb.AppendLine(Line("  SHELLDLL_DefView", defview));

        IntPtr list = FindWindowExW(defview, IntPtr.Zero, "SysListView32", null);
        sb.AppendLine(Line("    SysListView32", list));

        sb.AppendLine(Line("  FluidDockWindow", FindWindowExW(progman, IntPtr.Zero, "FluidDockWindow", null)));
        sb.AppendLine(Line("  (top-level dock)", FindWindowW("FluidDockWindow", null)));
        return sb.ToString();
    }
}
'@

[Sty]::Dump()
