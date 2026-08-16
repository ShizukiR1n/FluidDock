using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FluidDock.Graphics;

/// <summary>
/// Bakes a rounded translucent panel: vertical gradient fill, film grain, top highlight,
/// hairline border.
///
/// Only the settings panel uses this. The dock itself has no background any more - it is bare
/// icons on the wallpaper - so what was once a shared recipe now has exactly one caller, and its
/// constants are free to be tuned against the one thing it sits on: whatever the user had open.
///
/// Baked rather than drawn as Composition shapes for one specific reason: the resulting alpha
/// channel is what a DropShadow uses as its mask, so the shadow follows the rounded corners
/// instead of squaring off at the visual's bounds. Composition shapes cast no shadow of their
/// own, so the panel background is a sprite and everything inside it is vector.
/// </summary>
internal static class SurfaceTexture
{
    public static Bitmap RoundedPanel(int width, int height, float cornerRadius, Color tint, float border, float topHighlight, float noise)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            int diameter = (int)MathF.Round(cornerRadius * 2f);
            var bounds = new Rectangle(0, 0, width - 1, height - 1);
            using GraphicsPath path = IconLoader.RoundedRect(bounds, diameter);

            // Lighter at the top, darker at the bottom. Glass catches light from above, and a
            // flat fill is the single biggest thing that makes a translucent panel read as a
            // plastic rectangle rather than a material.
            using (var fill = new LinearGradientBrush(
                       new Rectangle(0, 0, 1, height), Shift(tint, 10), Shift(tint, -8), LinearGradientMode.Vertical))
            {
                fill.WrapMode = WrapMode.TileFlipXY;
                g.FillPath(fill, path);
            }

            if (border > 0f)
            {
                using var pen = new Pen(Color.FromArgb(Alpha(border), 255, 255, 255), 1f);
                g.DrawPath(pen, path);
            }

            if (topHighlight > 0f)
            {
                using var highlight = new LinearGradientBrush(
                    new Rectangle(0, 0, width, 1),
                    Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                    LinearGradientMode.Horizontal);

                // Faded out towards the rounded ends, where the top edge is curving away and a
                // hard stop would show as two bright dots.
                highlight.InterpolationColors = new ColorBlend
                {
                    Positions = [0f, 0.15f, 0.85f, 1f],
                    Colors =
                    [
                        Color.FromArgb(0, 255, 255, 255),
                        Color.FromArgb(Alpha(topHighlight), 255, 255, 255),
                        Color.FromArgb(Alpha(topHighlight), 255, 255, 255),
                        Color.FromArgb(0, 255, 255, 255),
                    ],
                };

                g.FillRectangle(highlight, new Rectangle(0, 1, width, 1));
            }
        }

        if (noise > 0f) Grain(bitmap, noise);
        return bitmap;
    }

    /// <summary>
    /// The panel's outline, opaque, with nothing in it.
    ///
    /// Exists to be a DropShadow's mask. The shadow needs an alpha channel to take its silhouette
    /// from, and the obvious candidate - the panel's own background - is not usable for the glass
    /// theme, where the background is re-baked against a fresh screen capture every time the panel
    /// is placed. A shadow whose mask is swapped out from under it flickers; this one is built
    /// once and never changes, because the shape never does.
    /// </summary>
    public static Bitmap Silhouette(int width, int height, float cornerRadius)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using GraphicsPath path = IconLoader.RoundedRect(
            new Rectangle(0, 0, width - 1, height - 1), (int)MathF.Round(cornerRadius * 2f));
        using var fill = new SolidBrush(Color.Black);
        g.FillPath(fill, path);

        return bitmap;
    }

    /// <summary>
    /// Per-pixel luminance jitter, weighted by the alpha already there so it stops cleanly at the
    /// rounded edge instead of speckling outside it.
    ///
    /// Seeded, not random: an unseeded pattern would differ between the panel and a rebuild of
    /// the panel, and a background that shimmers when a row is added is worse than no grain.
    /// </summary>
    public static void Grain(Bitmap bitmap, float strength)
    {
        var random = new Random(0x5EED);
        int amplitude = (int)MathF.Round(Math.Clamp(strength, 0f, 1f) * 255f);
        if (amplitude <= 0) return;

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

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

    private static Color Shift(Color color, int amount) =>
        Color.FromArgb(color.A, Clamp(color.R + amount), Clamp(color.G + amount), Clamp(color.B + amount));

    private static int Alpha(float opacity) => (int)MathF.Round(Math.Clamp(opacity, 0f, 1f) * 255f);
}
