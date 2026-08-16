using System.Drawing;
using System.Drawing.Imaging;

namespace FluidDock.Graphics;

/// <summary>
/// Bakes a slab of glass over a captured backdrop.
///
/// The thing that separates this from the frosted-acrylic look Windows has had since 2017 is not
/// the blur - everybody has the blur. It is what happens in the last dozen pixels before the
/// edge, where a real piece of glass stops being a flat filter and starts being an object with
/// thickness:
///
///   refraction   the rim bends what is behind it outward, so the background appears to stretch
///                and slide as it passes under the bevel. This is the single cue that reads as
///                "thick" rather than "translucent", and it is the one nobody fakes.
///   specular     a bright line where the bevel faces the light, fading as the surface turns
///                away, and a dimmer second one on the far side from light that went through the
///                slab and came back. Two highlights, not one.
///   tint         applied after the blur and after a saturation lift, so colour from behind still
///                comes through instead of being flattened to grey.
///
/// All of it is baked into one bitmap, once, when the panel is placed. There is no per-frame cost
/// and nothing here runs while the panel is on screen - which matters, because the alternative
/// designs all end in a pixel shader and this project has no shader pipeline and wants none.
///
/// The blur is done on a downsampled copy. That is not only for speed: a box blur wide enough to
/// look like ground glass at full resolution is an enormous kernel, whereas at a fifth of the
/// size a radius of two is the same thing, and the bilinear upsample on the way back out smooths
/// the box blur's characteristic square shoulders into something Gaussian enough for the eye.
/// </summary>
internal static class LiquidGlass
{
    /// <summary>How far the backdrop is shrunk before blurring. See the note above.</summary>
    private const int DownScale = 5;

    /// <summary>Box blur passes on the small copy. Three is where a box blur stops looking like one.</summary>
    private const int BlurPasses = 3;
    private const int BlurRadius = 2;

    /// <summary>Depth of the bevel, in final pixels. Below about ten it reads as a border.</summary>
    private const float RimDepth = 13f;

    /// <summary>How far the rim drags the backdrop outward at its very edge.</summary>
    private const float Refraction = 9f;

    /// <summary>Width of the crisp outer line, on top of the soft bevel. See the note in Bake.</summary>
    private const float LipWidth = 1.7f;

    /// <summary>Saturation multiplier applied before tinting. Under 1.2 the tint eats all the colour.</summary>
    private const float Saturation = 1.45f;

    /// <summary>
    /// How much darker the tint gets over a bright backdrop, at most.
    ///
    /// The panel's text is white and its backdrop is whatever the user had open, which includes
    /// a full-screen white document. A fixed tint has to be chosen for that worst case and is
    /// then far too heavy over a dark one - at which point the glass is just the dark theme with
    /// extra steps. So the tint is chosen per bake from the backdrop's own mean luminance, which
    /// is the cheapest possible version of what Apple's material does and buys most of it.
    /// </summary>
    private const float AdaptiveTint = 0.26f;

    /// <summary>Backdrop luminance at which the tint starts thickening, and where it stops.</summary>
    private const float TintFloor = 84f;
    private const float TintCeiling = 200f;

    /// <summary>
    /// Direction the light comes from, as a unit vector in screen space. Up and to the left,
    /// which is where every operating system has agreed the sun is since about 1984.
    /// </summary>
    private static readonly float LightX = -0.7071f;
    private static readonly float LightY = -0.7071f;

    /// <summary>
    /// Bakes the panel.
    ///
    /// <paramref name="backdrop"/> is a capture of the screen where the panel is about to appear.
    /// It is aligned to the output's bottom-right corner and sampled with clamping, so a capture
    /// that is the wrong size - which is what a rebuild leaves behind, since the panel changes
    /// height while its anchor corner does not - stretches the outermost row rather than failing.
    /// Under this much blur that is not a visible difference.
    /// </summary>
    public static Bitmap Bake(
        Bitmap? backdrop, int width, int height, float cornerRadius, Color tint, float noise)
    {
        Layer source = Layer.Blurred(backdrop);

        var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = output.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        // Aligns the capture's bottom-right with the panel's, which is the corner the panel is
        // anchored to and therefore the only one that has not moved.
        float shiftX = source.Width * DownScale - width;
        float shiftY = source.Height * DownScale - height;

        float halfW = width / 2f;
        float halfH = height / 2f;
        float innerW = halfW - cornerRadius;
        float innerH = halfH - cornerRadius;

        float tintR = tint.R, tintG = tint.G, tintB = tint.B;

        float lift = Math.Clamp((source.MeanLuma - TintFloor) / (TintCeiling - TintFloor), 0f, 1f);
        float tintA = MathF.Min(tint.A / 255f + lift * AdaptiveTint, 0.94f);

        try
        {
            unsafe
            {
                for (int y = 0; y < height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;

                    for (int x = 0; x < width; x++)
                    {
                        // Signed distance to the rounded rectangle: negative inside, zero on the
                        // boundary. The corner case falls out of the same expression as the edges,
                        // which is why this is worth the two lines of vector arithmetic over four
                        // separate quadrant tests that have to agree with each other.
                        float px = x + 0.5f - halfW;
                        float py = y + 0.5f - halfH;
                        float qx = MathF.Abs(px) - innerW;
                        float qy = MathF.Abs(py) - innerH;

                        float mx = MathF.Max(qx, 0f);
                        float my = MathF.Max(qy, 0f);
                        float distance =
                            MathF.Sqrt(mx * mx + my * my) + MathF.Min(MathF.Max(qx, qy), 0f) - cornerRadius;

                        // One pixel of coverage ramp, which is the whole of the antialiasing.
                        float coverage = Math.Clamp(0.5f - distance, 0f, 1f);
                        if (coverage <= 0f)
                        {
                            *(uint*)(row + x * 4) = 0;
                            continue;
                        }

                        // Outward normal. In the corners it points along the radius; along the
                        // straight edges it is axis-aligned, and computing it from the same q
                        // keeps it consistent with the distance above.
                        float nx, ny;
                        if (qx > 0f && qy > 0f)
                        {
                            float length = MathF.Sqrt(qx * qx + qy * qy);
                            nx = qx / length * MathF.Sign(px);
                            ny = qy / length * MathF.Sign(py);
                        }
                        else if (qx > qy)
                        {
                            nx = MathF.Sign(px);
                            ny = 0f;
                        }
                        else
                        {
                            nx = 0f;
                            ny = MathF.Sign(py);
                        }

                        // How far into the bevel this pixel is: 0 in the flat middle, 1 at the
                        // outermost edge. Squared, because a bevel's slope steepens towards the
                        // rim and a linear ramp reads as a chamfer rather than a curve.
                        float depth = Math.Clamp((RimDepth + distance) / RimDepth, 0f, 1f);
                        float bevel = depth * depth;

                        float push = Refraction * bevel;
                        float sampleX = (x + shiftX + nx * push) / DownScale;
                        float sampleY = (y + shiftY + ny * push) / DownScale;

                        source.Sample(sampleX, sampleY, out float r, out float g, out float b);

                        Saturate(ref r, ref g, ref b);

                        r += (tintR - r) * tintA;
                        g += (tintG - g) * tintA;
                        b += (tintB - b) * tintA;

                        // The two highlights. The near one is where the bevel faces the light; the
                        // far one is weaker and sits opposite it, standing in for light that went
                        // through the slab and came back off the inside of the far wall. Dropping
                        // the second is the difference between glass and a bevelled button.
                        float facing = nx * LightX + ny * LightY;
                        float near = MathF.Max(facing, 0f);
                        float far = MathF.Max(-facing, 0f);

                        // Two scales, not one. The bevel alone is a wide soft ramp, and a wide
                        // soft ramp with no hard edge on it reads as inflated plastic - the shape
                        // is right and the surface is not. What says "glass" is the sharp line
                        // exactly at the boundary, where the curvature is infinite and a real one
                        // catches the light in a single pixel. The ramp is the body; the lip is
                        // the surface.
                        float lip = Math.Clamp((distance + LipWidth) / LipWidth, 0f, 1f);

                        float sheen =
                            bevel * bevel * (near * 148f + far * 50f) +
                            lip * lip * (near * 86f + 26f);
                        r += sheen;
                        g += sheen;
                        b += sheen;

                        byte alpha = (byte)(coverage * 255f);
                        byte* pixel = row + x * 4;
                        pixel[0] = Clamp(b);
                        pixel[1] = Clamp(g);
                        pixel[2] = Clamp(r);
                        pixel[3] = alpha;
                    }
                }
            }
        }
        finally
        {
            output.UnlockBits(data);
        }

        if (noise > 0f) SurfaceTexture.Grain(output, noise);
        return output;
    }

    /// <summary>
    /// Lifts saturation about the pixel's own luminance, so the tint that follows has some colour
    /// left to work with. Without it a dark tint over a blur is uniformly grey whatever is behind.
    /// </summary>
    private static void Saturate(ref float r, ref float g, ref float b)
    {
        float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        r = luma + (r - luma) * Saturation;
        g = luma + (g - luma) * Saturation;
        b = luma + (b - luma) * Saturation;
    }

    private static byte Clamp(float value) => (byte)(value < 0f ? 0f : value > 255f ? 255f : value);

    /// <summary>
    /// The downsampled, blurred backdrop, in a form that can be sampled at fractional coordinates.
    ///
    /// Held as floats rather than bytes because the blur runs over it in place several times and
    /// rounding to bytes between passes shows up as banding in the large flat areas a blur is
    /// mostly made of.
    /// </summary>
    private sealed class Layer
    {
        private readonly float[] _r;
        private readonly float[] _g;
        private readonly float[] _b;

        public int Width { get; }
        public int Height { get; }

        /// <summary>Average luminance, 0-255. Drives how heavily the glass is tinted.</summary>
        public float MeanLuma { get; private set; }

        private Layer(int width, int height)
        {
            Width = width;
            Height = height;
            _r = new float[width * height];
            _g = new float[width * height];
            _b = new float[width * height];
        }

        /// <summary>
        /// A capture, shrunk and blurred. A null capture - the screen grab failed, or the theme
        /// was switched on with nothing to grab - becomes a flat mid-grey, which the tint and the
        /// rim turn into a perfectly presentable piece of frosted glass with nothing behind it.
        /// </summary>
        public static Layer Blurred(Bitmap? backdrop)
        {
            if (backdrop is null || backdrop.Width < DownScale || backdrop.Height < DownScale)
            {
                var flat = new Layer(1, 1);
                flat._r[0] = flat._g[0] = flat._b[0] = 128f;
                flat.MeanLuma = 128f;
                return flat;
            }

            int width = Math.Max(1, backdrop.Width / DownScale);
            int height = Math.Max(1, backdrop.Height / DownScale);

            var layer = new Layer(width, height);
            layer.Read(backdrop, width, height);

            var scratch = new float[width * height];
            for (int pass = 0; pass < BlurPasses; pass++)
            {
                Blur(layer._r, scratch, width, height);
                Blur(layer._g, scratch, width, height);
                Blur(layer._b, scratch, width, height);
            }

            // Measured after the blur, so it is the luminance of what will actually be seen
            // rather than of a capture whose highlights the blur was about to average away.
            double sum = 0;
            for (int i = 0; i < layer._r.Length; i++)
                sum += 0.2126f * layer._r[i] + 0.7152f * layer._g[i] + 0.0722f * layer._b[i];

            layer.MeanLuma = (float)(sum / layer._r.Length);
            return layer;
        }

        /// <summary>Shrinks through GDI+, which already does a correctly weighted area resample.</summary>
        private void Read(Bitmap backdrop, int width, int height)
        {
            using var small = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(backdrop, new Rectangle(0, 0, width, height));
            }

            BitmapData data = small.LockBits(
                new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* row = (byte*)data.Scan0 + y * data.Stride;
                        int line = y * width;

                        for (int x = 0; x < width; x++)
                        {
                            byte* pixel = row + x * 4;
                            _b[line + x] = pixel[0];
                            _g[line + x] = pixel[1];
                            _r[line + x] = pixel[2];
                        }
                    }
                }
            }
            finally
            {
                small.UnlockBits(data);
            }
        }

        /// <summary>
        /// One separable box blur pass, edges clamped.
        ///
        /// Clamped rather than wrapped or zeroed: this is a crop out of the middle of the screen,
        /// so the world does not stop at its border, and a zero-padded blur darkens every edge -
        /// which lands exactly under the rim, where it is most visible.
        /// </summary>
        private static void Blur(float[] channel, float[] scratch, int width, int height)
        {
            const float Weight = 1f / (BlurRadius * 2 + 1);

            for (int y = 0; y < height; y++)
            {
                int line = y * width;
                for (int x = 0; x < width; x++)
                {
                    float sum = 0f;
                    for (int k = -BlurRadius; k <= BlurRadius; k++)
                        sum += channel[line + Math.Clamp(x + k, 0, width - 1)];
                    scratch[line + x] = sum * Weight;
                }
            }

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    float sum = 0f;
                    for (int k = -BlurRadius; k <= BlurRadius; k++)
                        sum += scratch[Math.Clamp(y + k, 0, height - 1) * width + x];
                    channel[y * width + x] = sum * Weight;
                }
            }
        }

        /// <summary>Bilinear sample, clamped at the edges for the reason the blur is.</summary>
        public void Sample(float x, float y, out float r, out float g, out float b)
        {
            float fx = Math.Clamp(x, 0f, Width - 1.001f);
            float fy = Math.Clamp(y, 0f, Height - 1.001f);

            int x0 = (int)fx;
            int y0 = (int)fy;
            int x1 = Math.Min(x0 + 1, Width - 1);
            int y1 = Math.Min(y0 + 1, Height - 1);

            float tx = fx - x0;
            float ty = fy - y0;

            int a = y0 * Width + x0, bIndex = y0 * Width + x1;
            int c = y1 * Width + x0, d = y1 * Width + x1;

            r = Mix(Mix(_r[a], _r[bIndex], tx), Mix(_r[c], _r[d], tx), ty);
            g = Mix(Mix(_g[a], _g[bIndex], tx), Mix(_g[c], _g[d], tx), ty);
            b = Mix(Mix(_b[a], _b[bIndex], tx), Mix(_b[c], _b[d], tx), ty);
        }

        private static float Mix(float a, float b, float t) => a + (b - a) * t;
    }
}
