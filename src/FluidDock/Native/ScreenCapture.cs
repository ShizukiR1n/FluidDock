using System.Drawing;

namespace FluidDock.Native;

/// <summary>
/// Copies a rectangle of the screen into a bitmap.
///
/// This exists because there is no live backdrop to be had. Both of Composition's backdrop
/// brushes were tried against this window and neither works from a DesktopWindowTarget:
/// <c>CreateBackdropBrush</c> samples the composition tree behind the visual, which for a
/// transparent window is nothing at all, and <c>CreateHostBackdropBrush</c> renders solid black.
/// The undocumented accent blur does work, but it blurs the whole window rectangle - and this
/// window is a shadow's width bigger than the panel on every side, so the blur shows up as a
/// square standing out past the rounded corners. Clipping it back with SetWindowRgn takes the
/// shadow with it.
///
/// So the backdrop is grabbed once, off the screen, in the moment before the panel is shown. It
/// is a frozen image of what was behind, which sounds like a compromise and here is not one: the
/// panel is placed and then shown without moving, and it dismisses itself the instant anything
/// else takes the foreground. Nothing that could change behind it can happen while it is up. The
/// one visible seam would be an animation playing underneath - a video, a progress bar - which
/// keeps moving while the frozen copy does not.
/// </summary>
internal static class ScreenCapture
{
    /// <summary>
    /// Grabs a screen rectangle, or null if it cannot be had.
    ///
    /// Callers are expected to cope with null rather than treat it as fatal: a locked desktop, a
    /// protected-content window or a display change between placing and grabbing all fail here,
    /// and none of them is a reason to refuse to open the settings.
    /// </summary>
    public static Bitmap? Grab(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        try
        {
            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bitmap))
            {
                // CopyFromScreen rather than a hand-rolled BitBlt: it is the same call underneath,
                // and it already handles the DC lifetime that this would otherwise have to.
                g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }

            // The screen has no alpha channel, so every pixel comes back with alpha 0. Left that
            // way the capture is invisible the moment it reaches a surface that respects alpha -
            // which is every surface in this program.
            Opaque(bitmap);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Opaque(Bitmap bitmap)
    {
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                for (int row = 0; row < bitmap.Height; row++)
                {
                    byte* line = (byte*)data.Scan0 + row * data.Stride;
                    for (int column = 0; column < bitmap.Width; column++) line[column * 4 + 3] = 255;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
