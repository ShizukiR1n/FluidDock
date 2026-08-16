using System.Drawing;
using FluidDock.Native;

namespace FluidDock.Graphics;

/// <summary>
/// Resolves a dock entry to a bitmap.
///
/// Order matters: an explicit PNG beats a same-named file in assets/icons, which beats the
/// shell's own icon. Windows app icons are stylistically all over the place, so the override
/// path is the one that actually decides whether the dock looks deliberate.
/// </summary>
internal static class IconLoader
{
    /// <summary>Source resolution. Icons are drawn at MaxScale, so this must exceed the magnified size.</summary>
    private const int SourceSize = 256;

    public static string AssetsDirectory => Path.Combine(AppContext.BaseDirectory, "assets", "icons");

    public static Bitmap Load(DockItemConfig item) => Load(item, SourceSize);

    /// <summary>
    /// Resolves an entry's icon, rasterising from a source no larger than it has to.
    ///
    /// The dock asks for the full 256, because an icon it magnifies to 96 pixels is worth
    /// resampling from the real asset. The settings panel draws the same icon at 26 and asks for
    /// 64, which is four times less work per item on a list the user may add to all afternoon.
    /// </summary>
    public static Bitmap Load(DockItemConfig item, int sourceSize)
    {
        Bitmap? bitmap = TryExplicitIcon(item.Icon, sourceSize)
            ?? TryAssetOverride(item)
            ?? TryShellIcon(item.Path, sourceSize);

        return bitmap ?? Placeholder(item);
    }

    /// <summary>
    /// The user's own choice of icon, which may not be an image file at all.
    ///
    /// Pointing at an .exe or .dll is allowed and means "use that program's icon" - the quickest
    /// fix for an application whose own icon is the ugly one, and it needs no image editor.
    /// </summary>
    private static Bitmap? TryExplicitIcon(string? icon, int sourceSize)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;

        string path = Path.IsPathRooted(icon) ? icon : Path.Combine(AppContext.BaseDirectory, icon);
        return LoadPng(path) ?? LoadIco(path, sourceSize) ?? TryShellIcon(path, sourceSize);
    }

    /// <summary>
    /// An .ico holds several sizes, and GDI+'s Bitmap constructor picks one of the small ones.
    /// Icon does let us ask, so this asks for the biggest and takes what it gets.
    /// </summary>
    private static Bitmap? LoadIco(string path, int sourceSize)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var icon = new Icon(stream, sourceSize, sourceSize);
            return icon.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Looks for assets/icons/&lt;exe name&gt;.png, so overriding an icon needs no config edit.</summary>
    private static Bitmap? TryAssetOverride(DockItemConfig item)
    {
        string stem = Path.GetFileNameWithoutExtension(item.Path);
        if (string.IsNullOrEmpty(stem)) return null;

        return LoadPng(Path.Combine(AssetsDirectory, stem + ".png"))
            ?? (item.Label is null ? null : LoadPng(Path.Combine(AssetsDirectory, item.Label + ".png")));
    }

    private static Bitmap? LoadPng(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            // Read through memory so the file is not left locked - hot reload rewrites these.
            byte[] bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            using var loaded = new Bitmap(stream);
            return new Bitmap(loaded);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? TryShellIcon(string path, int sourceSize)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        Bitmap? shell = Shell.GetHighResIcon(path, sourceSize);
        if (shell is not null) return shell;

        try
        {
            using Icon? associated = Icon.ExtractAssociatedIcon(path);
            return associated?.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A neutral rounded tile with an initial, so a bad path is visible but not ugly.</summary>
    private static Bitmap Placeholder(DockItemConfig item)
    {
        var bitmap = new Bitmap(SourceSize, SourceSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var bounds = new Rectangle(8, 8, SourceSize - 16, SourceSize - 16);
        using var path = RoundedRect(bounds, 56);
        using var fill = new System.Drawing.Drawing2D.LinearGradientBrush(
            bounds, Color.FromArgb(255, 96, 100, 112), Color.FromArgb(255, 58, 61, 70),
            System.Drawing.Drawing2D.LinearGradientMode.Vertical);
        g.FillPath(fill, path);

        string label = (item.Label ?? Path.GetFileNameWithoutExtension(item.Path) ?? "?").Trim();
        string glyph = label.Length > 0 ? label[..1].ToUpperInvariant() : "?";

        using var font = new Font("Segoe UI", 120, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(glyph, font, brush, bounds, format);

        return bitmap;
    }

    public static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle bounds, int diameter)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
