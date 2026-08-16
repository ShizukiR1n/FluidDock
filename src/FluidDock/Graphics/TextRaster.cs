using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace FluidDock.Graphics;

/// <summary>
/// Turns a string into a tightly-cropped ARGB bitmap, ready to become a Composition surface.
///
/// Composition has no text of its own in a desktop app - the DirectWrite path exists but would
/// mean a second text stack alongside the GDI+ one the dock already uses for its icons. So text
/// is rasterised here, once per string, and from then on it is just another textured sprite the
/// compositor moves around for free.
///
/// The bitmap is cropped to the glyphs rather than padded to a line box, because every caller
/// wants to place text by its visual edge. A row that centres a label vertically against a line
/// box is off by the font's internal leading, which is the kind of two-pixel wrongness that is
/// impossible to see and impossible to unsee.
/// </summary>
internal static class TextRaster
{
    /// <summary>
    /// GenericTypographic, not GenericDefault. The default format reserves a chunk of extra
    /// space either side of the string for overhang that these fonts do not have, which shows up
    /// as text that will not sit flush against anything.
    /// </summary>
    private static readonly StringFormat Format = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap,
    };

    private static readonly Bitmap Scratch = new(1, 1, PixelFormat.Format32bppArgb);
    private static readonly System.Drawing.Graphics Measurer = CreateMeasurer();

    private static System.Drawing.Graphics CreateMeasurer()
    {
        var g = System.Drawing.Graphics.FromImage(Scratch);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g;
    }

    /// <summary>Width and height of the drawn text, in pixels.</summary>
    public static SizeF Measure(string text, Font font)
    {
        if (string.IsNullOrEmpty(text)) return SizeF.Empty;
        lock (Measurer) return Measurer.MeasureString(text, font, int.MaxValue, Format);
    }

    /// <summary>
    /// Draws text into a new bitmap sized to fit it.
    ///
    /// AntiAliasGridFit rather than ClearType: the target has an alpha channel, and subpixel
    /// antialiasing is a lie told about a known background colour. Rendered over transparency it
    /// produces coloured fringes that follow the text around wherever the panel is composited.
    /// </summary>
    public static Bitmap Render(string text, Font font, Color color)
    {
        SizeF size = Measure(text, font);

        // One pixel of margin. Antialiased glyph edges reach a fraction past the measured box,
        // and a bitmap cropped exactly to that measurement shaves the outermost coverage off.
        int width = Math.Max(1, (int)MathF.Ceiling(size.Width) + 2);
        int height = Math.Max(1, (int)MathF.Ceiling(size.Height) + 2);

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, 1f, 1f, Format);
        }

        return bitmap;
    }
}
