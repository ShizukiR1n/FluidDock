using System.Numerics;
using Windows.UI.Composition;
using GdiColor = System.Drawing.Color;
using GdiFont = System.Drawing.Font;

namespace FluidDock.Menu;

/// <summary>
/// A text sprite that can be given a new string without leaking the old one.
///
/// Text in this panel is a rasterised bitmap uploaded to the GPU, which is exactly right for a
/// label that is written once and then moved around for free - and exactly wrong for a slider
/// readout that changes as the user drags. So this keeps one surface at a time and closes the
/// previous as it installs the next.
///
/// The string is compared before anything is rasterised, which is what makes a drag cheap: a
/// slider showing whole pixels over a 64-pixel range produces at most 64 rasterisations across
/// an entire sweep, however many hundred mouse messages arrive in the meantime.
/// </summary>
internal sealed class MutableText : IDisposable
{
    private readonly MenuCanvas _canvas;
    private readonly GdiFont _font;
    private readonly GdiColor _color;
    private readonly Action<SpriteVisual>? _layout;

    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceBrush? _brush;
    private string _text = string.Empty;

    public SpriteVisual Visual { get; }

    internal MutableText(MenuCanvas canvas, GdiFont font, GdiColor color, Action<SpriteVisual>? layout)
    {
        _canvas = canvas;
        _font = font;
        _color = color;
        _layout = layout;

        Visual = canvas.Compositor.CreateSpriteVisual();
        Visual.Size = Vector2.Zero;
    }

    /// <summary>
    /// Replaces the string, and re-runs the caller's layout so a right-aligned or centred label
    /// stays where it belongs once its width changes.
    /// </summary>
    public void Set(string text)
    {
        if (text == _text) return;
        _text = text;

        _brush?.Dispose();
        _surface?.Dispose();
        _brush = null;
        _surface = null;

        if (string.IsNullOrEmpty(text))
        {
            Visual.Brush = null;
            Visual.Size = Vector2.Zero;
        }
        else
        {
            using System.Drawing.Bitmap bitmap = Graphics.TextRaster.Render(text, _font, _color);
            _surface = _canvas.Surfaces.CreateSurface(bitmap);
            _brush = _canvas.Compositor.CreateSurfaceBrush(_surface);
            Visual.Size = new Vector2(bitmap.Width, bitmap.Height);
            Visual.Brush = _brush;
        }

        _layout?.Invoke(Visual);
    }

    public void Dispose()
    {
        _brush?.Dispose();
        _surface?.Dispose();
        Visual.Dispose();
    }
}
