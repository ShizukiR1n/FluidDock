# Validates a multi-size .ico: directory in bounds, every size present, and the 16px entry is the
# render we intended rather than something Windows rescaled on load.
#
# Worth having as a file because the first generated icon was truncated and still loaded without
# complaint - the small sizes were intact, so everything looked fine right up until a high-DPI
# tray asked for one of the missing ones.

param(
    [string] $Icon   = "D:\AI\work space\win\src\FluidDock\assets\tray.ico",
    [string] $Source = "C:\Users\shayu\Downloads\VA11.ico"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# Size selection is checked through LoadImageW, not System.Drawing.Icon. Icon skips
# PNG-compressed entries, so it reports the 256px one as missing and hands back 128 instead -
# which had me about to "fix" an icon that Windows was reading perfectly well.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class IconProbe {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetObjectW(IntPtr h, int n, ref BITMAP bm);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    [StructLayout(LayoutKind.Sequential)] struct ICONINFO {
        public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor;
    }
    [StructLayout(LayoutKind.Sequential)] struct BITMAP {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits;
    }

    public static int WidthAt(string path, int size) {
        IntPtr h = LoadImageW(IntPtr.Zero, path, 1, size, size, 0x0010);
        if (h == IntPtr.Zero) return -1;
        ICONINFO info;
        if (!GetIconInfo(h, out info)) { DestroyIcon(h); return -1; }
        var bm = new BITMAP();
        GetObjectW(info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask, Marshal.SizeOf<BITMAP>(), ref bm);
        if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        DestroyIcon(h);
        return bm.bmWidth;
    }
}
'@

$b = [System.IO.File]::ReadAllBytes($Icon)
$count = [BitConverter]::ToUInt16($b, 4)
$bad = 0

Write-Output ("{0} images, file is {1:N1} KB" -f $count, ($b.Length / 1KB))
for ($i = 0; $i -lt $count; $i++) {
    $o = 6 + $i * 16
    $w = $b[$o]; if ($w -eq 0) { $w = 256 }
    $len = [BitConverter]::ToUInt32($b, $o + 8)
    $off = [BitConverter]::ToUInt32($b, $o + 12)
    $fmt = if ($b[$off] -eq 0x89) { "PNG" } else { "BMP" }
    $ok = ($off + $len) -le $b.Length
    if (-not $ok) { $bad++ }
    Write-Output ("  {0,3}px {1}  {2,7:N0} bytes  {3}" -f $w, $fmt, $len, $(if ($ok) { "ok" } else { "PAST END OF FILE" }))
}

Write-Output ""
foreach ($s in @(16, 20, 24, 32, 48, 64, 128, 256)) {
    $got = [IconProbe]::WidthAt($Icon, $s)
    $exact = ($got -eq $s)
    if (-not $exact) { $bad++ }
    Write-Output ("  asked {0,3}px -> got {1,3}px  {2}" -f $s, $got, $(if ($exact) { "exact" } else { "WOULD BE RESCALED" }))
}

# The tray size is the one that matters most, and it is the one a lazy generator gets wrong by
# letting Windows downscale. Compare against an explicit nearest-neighbour render.
$srcIcon = New-Object System.Drawing.Icon($Source)
$src = $srcIcon.ToBitmap(); $srcIcon.Dispose()
$want = New-Object System.Drawing.Bitmap 16, 16
$g = [System.Drawing.Graphics]::FromImage($want)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.DrawImage($src, 0, 0, 16, 16); $g.Dispose()

$got = (New-Object System.Drawing.Icon($Icon, 16, 16)).ToBitmap()
$differ = 0
for ($y = 0; $y -lt 16; $y++) {
    for ($x = 0; $x -lt 16; $x++) {
        if ($got.GetPixel($x, $y).ToArgb() -ne $want.GetPixel($x, $y).ToArgb()) { $differ++ }
    }
}
if ($differ -gt 0) { $bad++ }
Write-Output ""
Write-Output ("  16px entry vs intended nearest-neighbour render: {0} of 256 pixels differ" -f $differ)

Write-Output ""
if ($bad -eq 0) { Write-Output "PASS - icon is complete and every size is exact." }
else { Write-Output "FAIL - $bad problem(s)." }
