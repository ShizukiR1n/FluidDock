using System.Numerics;
using FluidDock.Graphics;
using FluidDock.Visuals;
using Windows.UI;
using Windows.UI.Composition;
using GdiColor = System.Drawing.Color;
using GdiFont = System.Drawing.Font;

namespace FluidDock.Menu;

/// <summary>
/// A rounded rectangle drawn as Composition geometry, with the handful of handles a caller needs
/// to animate it afterwards.
///
/// Vector rather than a baked bitmap. Everything inside the panel changes shape or colour while
/// the user is looking at it - a switch knob slides, a slider fill grows, a selection travels
/// between segments - and a texture would have to be re-rasterised and re-uploaded for each of
/// those. Geometry is re-evaluated by the compositor for free. The one exception is the panel's
/// own background, which stays a bitmap because a DropShadow needs an alpha channel to use as
/// its mask, and shapes do not have one to offer.
/// </summary>
internal sealed class RoundRect
{
    public required ShapeVisual Visual { get; init; }
    public required CompositionRoundedRectangleGeometry Geometry { get; init; }
    public required CompositionColorBrush Fill { get; init; }

    /// <summary>Whether the geometry is inset half a pixel to keep a centred stroke inside the visual.</summary>
    public required bool Stroked { get; init; }

    public Vector3 Offset
    {
        get => Visual.Offset;
        set => Visual.Offset = value;
    }

    public Vector2 Size
    {
        get => Visual.Size;
        set
        {
            Visual.Size = value;
            Geometry.Size = Stroked ? value - new Vector2(1f, 1f) : value;
        }
    }

    public Color Color
    {
        get => Fill.Color;
        set => Fill.Color = value;
    }

    public float Opacity
    {
        get => Visual.Opacity;
        set => Visual.Opacity = value;
    }
}

/// <summary>
/// The drawing surface a row is handed when it is built.
///
/// This exists so a row type is a description of what it looks like rather than a lesson in
/// Composition. Adding a new control means writing Build() against these four or five calls; it
/// does not mean knowing that a stroked shape has to be inset half a pixel or that every finite
/// animation needs a scoped batch to release it. Those are decided once, here, and every row
/// inherits the decision whether or not its author knew it was being made.
///
/// It also owns the bookkeeping: everything created through it is registered for disposal with
/// the panel's tree, so a row cannot leak a GPU surface by forgetting to.
/// </summary>
internal sealed class MenuCanvas
{
    private readonly List<IDisposable> _resources;
    private readonly List<AnimatedProperty> _animations = [];

    public Compositor Compositor { get; }
    public SurfaceFactory Surfaces { get; }

    public MenuCanvas(Compositor compositor, SurfaceFactory surfaces, List<IDisposable> resources)
    {
        Compositor = compositor;
        Surfaces = surfaces;
        _resources = resources;
    }

    /// <summary>Registers a Composition object to be closed when the panel's tree is released.</summary>
    public T Own<T>(T resource) where T : IDisposable
    {
        _resources.Add(resource);
        return resource;
    }

    public ContainerVisual Container(float width, float height)
    {
        ContainerVisual container = Own(Compositor.CreateContainerVisual());
        container.Size = new Vector2(width, height);
        return container;
    }

    /// <summary>
    /// A filled rounded rectangle. Pass equal width, height and a radius of half that for a circle -
    /// there is deliberately no separate ellipse, because one shape type means one set of rules
    /// about insets, clipping and animation instead of two that drift.
    /// </summary>
    public RoundRect Rect(float width, float height, float radius, Color fill, Color? border = null)
    {
        CompositionRoundedRectangleGeometry geometry = Own(Compositor.CreateRoundedRectangleGeometry());
        geometry.CornerRadius = new Vector2(radius, radius);

        CompositionSpriteShape shape = Own(Compositor.CreateSpriteShape(geometry));
        CompositionColorBrush fillBrush = Own(Compositor.CreateColorBrush(fill));
        shape.FillBrush = fillBrush;

        bool stroked = border is not null;
        if (border is { } borderColor)
        {
            shape.StrokeBrush = Own(Compositor.CreateColorBrush(borderColor));
            shape.StrokeThickness = 1f;

            // A stroke is centred on the path, so half of it falls outside the geometry. A
            // ShapeVisual clips to its own Size, which would shave that half off along every
            // edge - the corners worst of all, where the missing sliver reads as a chipped
            // rectangle. Inset the path by half a pixel and the whole stroke lands inside.
            geometry.Offset = new Vector2(0.5f, 0.5f);
        }

        ShapeVisual visual = Own(Compositor.CreateShapeVisual());
        visual.Shapes.Add(shape);

        var rect = new RoundRect
        {
            Visual = visual,
            Geometry = geometry,
            Fill = fillBrush,
            Stroked = stroked,
        };

        rect.Size = new Vector2(width, height);
        return rect;
    }

    /// <summary>
    /// A string, rasterised and wrapped in a sprite sized exactly to its glyphs.
    ///
    /// Positioned by the caller, so the returned visual's Size is the measurement to lay out
    /// against - there is no line box and no baseline to compensate for.
    /// </summary>
    public SpriteVisual Text(string text, GdiFont font, GdiColor color)
    {
        SpriteVisual visual = Own(Compositor.CreateSpriteVisual());

        if (string.IsNullOrEmpty(text))
        {
            visual.Size = Vector2.Zero;
            return visual;
        }

        CompositionDrawingSurface surface;
        using (System.Drawing.Bitmap bitmap = TextRaster.Render(text, font, color))
        {
            surface = Own(Surfaces.CreateSurface(bitmap));
            visual.Size = new Vector2(bitmap.Width, bitmap.Height);
        }

        CompositionSurfaceBrush brush = Own(Compositor.CreateSurfaceBrush(surface));
        visual.Brush = brush;
        return visual;
    }

    /// <summary>
    /// A paragraph: text broken into lines at <paramref name="maxWidth"/>, in a sprite that is
    /// that wide and as tall as the lines came to. For the one place in the panel that shows
    /// prose rather than a label - the release notes under the update row.
    /// </summary>
    public SpriteVisual Paragraph(string text, GdiFont font, GdiColor color, float maxWidth)
    {
        SpriteVisual visual = Own(Compositor.CreateSpriteVisual());

        if (string.IsNullOrEmpty(text))
        {
            visual.Size = Vector2.Zero;
            return visual;
        }

        CompositionDrawingSurface surface;
        using (System.Drawing.Bitmap bitmap = TextRaster.RenderWrapped(text, font, color, maxWidth))
        {
            surface = Own(Surfaces.CreateSurface(bitmap));
            visual.Size = new Vector2(bitmap.Width, bitmap.Height);
        }

        CompositionSurfaceBrush brush = Own(Compositor.CreateSurfaceBrush(surface));
        visual.Brush = brush;
        return visual;
    }

    /// <summary>
    /// Shortens a string with an ellipsis until it fits, and returns it unchanged if it already does.
    ///
    /// Measured rather than counted, because the panel's labels are whatever the user's programs
    /// are called: a Chinese name is twice the width of a Latin one per character, and a rule
    /// expressed in characters would truncate one too early and let the other overflow the row.
    /// </summary>
    public static string Fit(string text, GdiFont font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || TextRaster.Measure(text, font).Width <= maxWidth) return text;

        for (int length = text.Length - 1; length > 0; length--)
        {
            string candidate = text[..length] + "…";
            if (TextRaster.Measure(candidate, font).Width <= maxWidth) return candidate;
        }

        return "…";
    }

    /// <summary>
    /// A dock entry's icon, resolved the same way the dock resolves it.
    ///
    /// Deliberately the same call: the whole point of a picture of the icon in the settings list
    /// is that it is the icon, so a second resolution path here - one that did not know about
    /// asset overrides, say - would show the user something the dock was not going to draw.
    /// </summary>
    public SpriteVisual Icon(DockItemConfig item, float size)
    {
        SpriteVisual visual = Own(Compositor.CreateSpriteVisual());
        int pixels = Math.Max(1, (int)MathF.Ceiling(size));

        CompositionDrawingSurface surface;
        using (System.Drawing.Bitmap bitmap = IconLoader.Load(item, IconPreviewSource))
        {
            surface = Own(Surfaces.CreateSurface(bitmap, pixels, pixels));
        }

        CompositionSurfaceBrush brush = Own(Compositor.CreateSurfaceBrush(surface));
        brush.Stretch = CompositionStretch.Uniform;

        visual.Brush = brush;
        visual.Size = new Vector2(size, size);
        return visual;
    }

    /// <summary>
    /// What to ask the shell for when drawing a 26-pixel thumbnail. The dock asks for 256 because
    /// it magnifies; the panel does not, and asking for the same asset would be four times the
    /// decode for pixels that get thrown away.
    /// </summary>
    private const int IconPreviewSource = 64;

    /// <summary>
    /// A text sprite whose string changes while the panel is open - a slider's readout, say.
    ///
    /// Text is a bitmap here, so every distinct string is a GPU surface. Owning each one the way
    /// <see cref="Text"/> does would grow the tree's resource list for the whole length of a
    /// drag and hold every intermediate surface until the panel was rebuilt. This holds one at a
    /// time and closes the last as it takes the next.
    /// </summary>
    public MutableText Changing(GdiFont font, GdiColor color, Action<SpriteVisual>? layout = null) =>
        Own(new MutableText(this, font, color, layout));

    /// <summary>Vertically centres a child of the given height inside a row.</summary>
    public static float CenterY(float rowHeight, float childHeight) =>
        MathF.Round((rowHeight - childHeight) / 2f);

    /// <summary>
    /// Wraps one animatable property with the release-and-supersede discipline. Rows should get
    /// their properties from here rather than calling StartAnimation, so that no row can be the
    /// one that leaves a per-frame tick running on an idle panel.
    /// </summary>
    public AnimatedProperty Animate(CompositionObject target, string property)
    {
        var animated = new AnimatedProperty(Compositor, target, property);
        _animations.Add(animated);
        return animated;
    }

    /// <summary>Abandons every in-flight animation, ahead of the tree being closed.</summary>
    public void RetireAnimations()
    {
        foreach (AnimatedProperty animation in _animations) animation.Retire();
        _animations.Clear();
    }
}
