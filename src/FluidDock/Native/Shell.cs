using System.Runtime.InteropServices;

namespace FluidDock.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx;
    public int cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAP
{
    public int bmType;
    public int bmWidth;
    public int bmHeight;
    public int bmWidthBytes;
    public ushort bmPlanes;
    public ushort bmBitsPixel;
    public IntPtr bmBits;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;

    /// <summary>Positive means the rows are stored bottom-up; negative means top-down.</summary>
    public int biHeight;

    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

/// <summary>
/// What GetObject returns for a DIB section. BITMAP alone does not carry row order - only the
/// sign of <see cref="BITMAPINFOHEADER.biHeight"/> does, which is why the larger struct is needed.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DIBSECTION
{
    public BITMAP dsBm;
    public BITMAPINFOHEADER dsBmih;
    public uint dsBitfield0;
    public uint dsBitfield1;
    public uint dsBitfield2;
    public IntPtr dshSection;
    public uint dsOffset;
}

[Flags]
internal enum SIIGBF
{
    ResizeToFit = 0x00,
    BiggerSizeOk = 0x01,
    MemoryOnly = 0x02,
    IconOnly = 0x04,
    ThumbnailOnly = 0x08,
    InCacheOnly = 0x10,
    ScaleUp = 0x100,
}

/// <summary>
/// The modern icon path. ExtractIconEx tops out at 32x32, which looks like mud once the dock
/// magnifies it; IShellItemImageFactory returns the real high-resolution asset.
/// </summary>
[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
}

internal static class Shell
{
    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObjectW(IntPtr hgdiobj, int cbBuffer, ref DIBSECTION lpvObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Asks the shell for the largest icon it has for a path. Returns null when the shell has
    /// nothing usable, which callers are expected to handle rather than treat as fatal.
    /// </summary>
    public static System.Drawing.Bitmap? GetHighResIcon(string path, int size)
    {
        object? shellItem = null;
        IntPtr hbitmap = IntPtr.Zero;

        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, in IID_IShellItemImageFactory, out shellItem);
            var factory = (IShellItemImageFactory)shellItem;

            // BiggerSizeOk asks for the next size up rather than a blurry downscale of a
            // smaller asset; IconOnly stops the shell handing back a document thumbnail.
            factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF.BiggerSizeOk | SIIGBF.IconOnly, out hbitmap);
            if (hbitmap == IntPtr.Zero) return null;

            return FromHBitmap(hbitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (shellItem is not null) Marshal.ReleaseComObject(shellItem);
        }
    }

    /// <summary>
    /// Copies a shell HBITMAP into a managed bitmap with its alpha intact.
    /// Bitmap.FromHbitmap would flatten the alpha channel, which is exactly the thing that
    /// makes extracted icons look like they have black halos.
    /// </summary>
    private static System.Drawing.Bitmap? FromHBitmap(IntPtr hbitmap)
    {
        var dib = new DIBSECTION();
        if (GetObjectW(hbitmap, Marshal.SizeOf<DIBSECTION>(), ref dib) < Marshal.SizeOf<DIBSECTION>())
            return null;

        BITMAP info = dib.dsBm;
        if (info.bmBitsPixel != 32 || info.bmBits == IntPtr.Zero) return null;

        // Row order has to be read off the DIB, not assumed. A bottom-up DIB - which is what the
        // shell actually hands back - stores its last row first, so bmBits points at the bottom
        // of the image. Walking it with a positive stride turns every icon upside down.
        //
        // The cure is the standard one: start at the last row and walk backwards. Bitmap accepts
        // a negative stride for exactly this.
        bool bottomUp = dib.dsBmih.biHeight > 0;

        int stride = bottomUp ? -info.bmWidthBytes : info.bmWidthBytes;
        IntPtr scan0 = bottomUp
            ? info.bmBits + (info.bmHeight - 1) * info.bmWidthBytes
            : info.bmBits;

        // The bits are premultiplied 32bpp BGRA, which is bit-for-bit Format32bppPArgb.
        using var view = new System.Drawing.Bitmap(
            info.bmWidth,
            info.bmHeight,
            stride,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb,
            scan0);

        // Clone so the result owns its pixels; the DIB dies with the HBITMAP.
        return new System.Drawing.Bitmap(view);
    }
}
