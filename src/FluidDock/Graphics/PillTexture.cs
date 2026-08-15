using System.Drawing;
using System.Drawing.Imaging;

namespace FluidDock.Graphics;

/// <summary>
/// Bakes the dock's background into a single bitmap: rounded rect, tint, film grain, top
/// highlight, hairline border.
///
/// Baking rather than composing this from Composition layers buys three things. The corners
/// are anti-aliased by GDI+ instead of clipped, the grain costs nothing at runtime, and the
/// resulting alpha channel doubles as the drop shadow's mask - so the shadow follows the
/// rounded shape for free instead of squaring off at the visual's bounds.
/// </summary>
internal static class PillTexture
{
    public static Bitmap Create(int width, int height, float cornerRadius, DockAppearanceConfig appearance)
    {
        Color tint = ParseColor(appearance.Tint, Color.FromArgb(140, 28, 28, 30));

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            int diameter = (int)MathF.Round(cornerRadius * 2f);
            var bounds = new Rectangle(0, 0, width - 1, height - 1);

            using (var path = IconLoader.RoundedRect(bounds, diameter))
            {
                // A vertical gradient, not a flat fill. Real glass is brighter where it catches
                // the light from above; a single flat colour is the main thing that makes a
                // translucent panel read as a plastic rectangle.
                using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(
                           new Rectangle(0, 0, 1, height),
                           Shift(tint, 14),
                           Shift(tint, -6),
                           System.Drawing.Drawing2D.LinearGradientMode.Vertical))
                {
                    fill.WrapMode = System.Drawing.Drawing2D.WrapMode.TileFlipXY;
                    g.FillPath(fill, path);
                }

                if (appearance.Border > 0f)
                {
                    using var pen = new Pen(Color.FromArgb(Alpha(appearance.Border), 255, 255, 255), 1f);
                    g.DrawPath(pen, path);
                }
            }

            // A single bright line just inside the top edge. This is what reads as "glass lit
            // from above" rather than "translucent rectangle"; it is worth more than the blur.
            if (appearance.TopHighlight > 0f)
            {
                using var highlight = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, width, 1),
                    Color.FromArgb(0, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255),
                    System.Drawing.Drawing2D.LinearGradientMode.Horizontal);

                // Fade the highlight out towards the rounded ends, where the top edge curves away.
                highlight.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend
                {
                    Positions = [0f, 0.18f, 0.82f, 1f],
                    Colors =
                    [
                        Color.FromArgb(0, 255, 255, 255),
                        Color.FromArgb(Alpha(appearance.TopHighlight), 255, 255, 255),
                        Color.FromArgb(Alpha(appearance.TopHighlight), 255, 255, 255),
                        Color.FromArgb(0, 255, 255, 255),
                    ],
                };

                g.FillRectangle(highlight, new Rectangle(0, 1, width, 1));
            }
        }

        if (appearance.Noise > 0f)
            ApplyGrain(bitmap, appearance.Noise);

        return bitmap;
    }

    /// <summary>
    /// Adds per-pixel luminance jitter, weighted by existing alpha so it stops cleanly at the
    /// rounded edge. Without this the pill reads as flat plastic no matter how the tint is tuned.
    /// </summary>
    private static void ApplyGrain(Bitmap bitmap, float strength)
    {
        var random = new Random(0x5EED);
        int amplitude = (int)MathF.Round(Math.Clamp(strength, 0f, 1f) * 255f);
        if (amplitude <= 0) return;

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        byte* px = row + x * 4; // BGRA
                        if (px[3] == 0) continue;

                        int delta = random.Next(-amplitude, amplitude + 1) * px[3] / 255;
                        px[0] = Clamp(px[0] + delta);
                        px[1] = Clamp(px[1] + delta);
                        px[2] = Clamp(px[2] + delta);
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    /// <summary>Brightens or darkens a colour without touching its alpha.</summary>
    private static Color Shift(Color color, int amount) =>
        Color.FromArgb(color.A, Clamp(color.R + amount), Clamp(color.G + amount), Clamp(color.B + amount));

    private static int Alpha(float opacity) => (int)MathF.Round(Math.Clamp(opacity, 0f, 1f) * 255f);

    /// <summary>Parses #AARRGGBB or #RRGGBB, falling back rather than throwing on a bad config.</summary>
    public static Color ParseColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        ReadOnlySpan<char> span = text.AsSpan().Trim().TrimStart('#');
        if (!uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out uint value))
            return fallback;

        return span.Length switch
        {
            6 => Color.FromArgb(255, (int)((value >> 16) & 0xFF), (int)((value >> 8) & 0xFF), (int)(value & 0xFF)),
            8 => Color.FromArgb((int)((value >> 24) & 0xFF), (int)((value >> 16) & 0xFF), (int)((value >> 8) & 0xFF), (int)(value & 0xFF)),
            _ => fallback,
        };
    }
}
