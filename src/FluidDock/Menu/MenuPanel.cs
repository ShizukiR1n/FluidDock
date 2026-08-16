using System.Numerics;
using FluidDock.Graphics;
using FluidDock.Visuals;
using Windows.UI.Composition;

namespace FluidDock.Menu;

/// <summary>
/// The settings panel's visual tree and everything that happens inside it: layout, the hover
/// highlight, hit testing and scrolling.
///
/// It knows nothing about windows. That separation is what lets the panel be laid out once,
/// measured, and only then given an HWND of the right size - and it means the layout can be
/// reasoned about without a message loop anywhere near it.
///
/// Structure, outermost first:
///
///   Root      window-sized, so nothing - including the panel's own drop shadow - is ever
///             drawing outside its parent's bounds
///   Panel     the rounded card; the open and close animations scale and fade this
///   Header    title, version, close button. Fixed; does not scroll
///   Viewport  clipped, so content taller than the panel is cut off rather than spilling
///   Content   sections, cards, hairlines, the highlight, and the rows themselves
///
/// Rows are children of Content rather than of their cards, so the one highlight visual can
/// travel between rows in any card without changing parents mid-flight.
/// </summary>
internal sealed class MenuPanel : IDisposable
{
    private readonly MenuPage _page;
    private readonly Compositor _compositor;
    private readonly List<IDisposable> _resources = [];
    private readonly MenuCanvas _canvas;

    private readonly ContainerVisual _root;
    private readonly ContainerVisual _panel;
    private readonly ContainerVisual _content;

    /// <summary>Every interactive row, flattened once so hit testing is not a LINQ query per move.</summary>
    private readonly MenuRow[] _targets;

    /// <summary>The panel's background sprite. Its brush is replaced on every open under glass.</summary>
    private SpriteVisual? _background;

    /// <summary>
    /// The current background's surface and brush, held rather than registered with the canvas.
    /// Anything registered lives until the whole tree is closed, and under glass these are made
    /// afresh every time the panel opens.
    /// </summary>
    private CompositionDrawingSurface? _backdropSurface;
    private CompositionSurfaceBrush? _backdropBrush;

    private RoundRect? _highlight;
    private AnimatedProperty? _highlightTravel;
    private AnimatedProperty? _highlightSize;
    private AnimatedProperty? _highlightFade;
    private AnimatedProperty? _scrollTravel;

    private RoundRect? _closeButton;
    private float _closeLeft;
    private float _closeTop;

    private RoundRect? _thumb;
    private AnimatedProperty? _thumbTravel;
    private AnimatedProperty? _thumbFade;
    private bool _thumbShown;

    private MenuRow? _hovered;
    private MenuRow? _pressed;
    private bool _closeHovered;
    private bool _closePressed;

    private float _scroll;

    // ---- Reorder ---------------------------------------------------------------------------
    //
    // Live only between a press on a row in a reorderable section and the release that ends it.

    /// <summary>Where each section's card starts, so a slot's position can be worked out later.</summary>
    private readonly Dictionary<MenuSection, float> _cardTops = [];

    /// <summary>Which section each row belongs to, so a press can ask whether it may be dragged.</summary>
    private readonly Dictionary<MenuRow, MenuSection> _rowSection = [];

    /// <summary>One Offset.Y and one Scale per row, made on first use and reused on every drag.</summary>
    private readonly Dictionary<MenuRow, AnimatedProperty> _rowTravel = [];
    private readonly Dictionary<MenuRow, AnimatedProperty> _rowLift = [];

    private MenuSection? _dragSection;
    private MenuRow? _dragRow;
    private List<MenuRow> _dragOrder = [];
    private int _dragFrom;
    private int _dragIndex;

    /// <summary>Where in the row the pointer took hold, so it does not jump to the row's corner.</summary>
    private float _dragGrab;

    /// <summary>Panel-space pointer position at the press, for the movement threshold.</summary>
    private float _pressY;

    /// <summary>The last pointer position, so an auto-scroll tick can re-place the row without one.</summary>
    private float _dragPointerY;

    private bool _dragArmed;
    private bool _dragging;

    public float PanelWidth => MenuTheme.PanelWidth;
    public float PanelHeight { get; }
    public float ContentHeight { get; }
    public float ViewportHeight { get; }

    /// <summary>Whether the content is taller than the room there is for it.</summary>
    public bool Scrolls => ContentHeight > ViewportHeight;

    public float WindowWidth => PanelWidth + MenuTheme.ShadowMargin * 2f;
    public float WindowHeight => PanelHeight + MenuTheme.ShadowMargin * 2f;

    /// <summary>The window's root visual. Hand this straight to a DesktopWindowTarget.</summary>
    public ContainerVisual Root => _root;

    /// <summary>The card itself, which is what the open and close animations act on.</summary>
    public ContainerVisual Card => _panel;

    /// <summary>
    /// True while a row is being dragged into a new position, so the window knows to run the
    /// clock that scrolls the list when the drag reaches its edge.
    /// </summary>
    public bool Reordering => _dragging;

    /// <summary>Raised when a row changed a value worth persisting.</summary>
    public event Action? Changed;

    /// <summary>Raised by the close button, or by a row whose action needs the panel gone.</summary>
    public event Action? Dismissed;

    public MenuPanel(CompositionHost host, MenuPage page)
    {
        _page = page;
        _compositor = host.Compositor;
        _canvas = new MenuCanvas(_compositor, host.Surfaces, _resources);

        ContentHeight = MeasureContent();
        ViewportHeight = MathF.Min(ContentHeight, MenuTheme.MaxViewportHeight);
        PanelHeight = MenuTheme.HeaderHeight + ViewportHeight + (Scrolls ? MenuTheme.ScrollPad : 0f);

        _root = _canvas.Container(WindowWidth, WindowHeight);

        _panel = _canvas.Container(PanelWidth, PanelHeight);
        _panel.Offset = new Vector3(MenuTheme.ShadowMargin, MenuTheme.ShadowMargin, 0f);
        _root.Children.InsertAtTop(_panel);

        BuildBackground();
        BuildHeader();

        ContainerVisual viewport = _canvas.Container(PanelWidth, ViewportHeight);
        viewport.Offset = new Vector3(0f, MenuTheme.HeaderHeight, 0f);
        viewport.Clip = _canvas.Own(_compositor.CreateInsetClip());
        _panel.Children.InsertAtTop(viewport);

        _content = _canvas.Container(PanelWidth, ContentHeight);
        viewport.Children.InsertAtTop(_content);
        _scrollTravel = _canvas.Animate(_content, "Offset.Y");

        BuildContent();
        BuildScrollBar(viewport);
        _targets = page.AllRows.Where(row => row.Interactive).ToArray();
    }

    /// <summary>
    /// Walks the page adding up heights, without building anything.
    ///
    /// Done first because the panel's height decides the window's, and the window's decides
    /// where on screen it can be put - so everything downstream needs a number that only exists
    /// once the whole page has been counted.
    /// </summary>
    private float MeasureContent()
    {
        float y = 0f;

        foreach (MenuSection section in _page.Sections)
        {
            if (section.Header is not null) y += MenuTheme.SectionHeaderHeight;
            foreach (MenuRow row in section.Rows) y += row.Height;
            y += MenuTheme.SectionGap;
        }

        if (_page.Sections.Count > 0) y -= MenuTheme.SectionGap;
        return y + MenuTheme.FooterHeight;
    }

    /// <summary>
    /// The card behind everything, plus its shadow.
    ///
    /// A baked bitmap rather than a shape, because DropShadow needs a brush to take its silhouette
    /// from and geometry cannot offer one.
    ///
    /// The shadow's mask is a separate, plain silhouette rather than the background texture
    /// itself. That costs one small bitmap and buys the thing the glass theme needs: the
    /// background can be replaced whenever the panel is placed somewhere new, without the shadow
    /// noticing. See <see cref="SetBackdrop"/>.
    /// </summary>
    private void BuildBackground()
    {
        int width = (int)MathF.Ceiling(PanelWidth);
        int height = (int)MathF.Ceiling(PanelHeight);

        CompositionDrawingSurface silhouette;
        using (System.Drawing.Bitmap mask = SurfaceTexture.Silhouette(width, height, MenuTheme.PanelRadius))
        {
            silhouette = _canvas.Own(_canvas.Surfaces.CreateSurface(mask));
        }

        _background = _canvas.Own(_compositor.CreateSpriteVisual());
        _background.Size = new Vector2(PanelWidth, PanelHeight);

        DropShadow shadow = _canvas.Own(_compositor.CreateDropShadow());
        shadow.Mask = _canvas.Own(_compositor.CreateSurfaceBrush(silhouette));
        shadow.BlurRadius = MenuTheme.PanelShadowBlur;
        shadow.Opacity = MenuTheme.PanelShadowOpacity;
        shadow.Offset = new Vector3(0f, MenuTheme.PanelShadowOffsetY, 0f);
        shadow.Color = Windows.UI.Color.FromArgb(255, 0, 0, 0);
        _background.Shadow = shadow;

        _panel.Children.InsertAtTop(_background);

        // The dark theme has nothing to wait for, so it is painted now and never touched again.
        // The glass theme cannot be: it needs the screen behind the panel, and where that is has
        // not been decided yet - the window is placed from this panel's measured height, which
        // only exists once the tree above has been built.
        if (MenuTheme.Panel == PanelTheme.Dark) SetBackdrop(null);
    }

    /// <summary>
    /// Paints the panel's background, over a capture of whatever the panel is about to cover.
    ///
    /// Called once for the dark theme, which ignores the capture, and on every open for the glass
    /// theme - because glass that keeps a picture of somewhere the panel used to be is worse than
    /// no glass at all. A null capture is a working fallback rather than a failure: it bakes as
    /// frosted glass with nothing behind it.
    ///
    /// The old surface is closed as the new one is taken, which is the same rule
    /// <see cref="MutableText"/> follows for the same reason - a bitmap per open, held until the
    /// panel is rebuilt, is a leak with a slow fuse.
    /// </summary>
    public void SetBackdrop(System.Drawing.Bitmap? capture)
    {
        if (_background is null) return;

        int width = (int)MathF.Ceiling(PanelWidth);
        int height = (int)MathF.Ceiling(PanelHeight);

        CompositionDrawingSurface surface;
        using (System.Drawing.Bitmap texture = MenuTheme.Panel == PanelTheme.Glass
                   ? LiquidGlass.Bake(capture, width, height, MenuTheme.PanelRadius, MenuTheme.GlassTint, MenuTheme.GlassNoise)
                   : SurfaceTexture.RoundedPanel(width, height, MenuTheme.PanelRadius, MenuTheme.PanelTint,
                       MenuTheme.PanelBorder, MenuTheme.PanelTopHighlight, MenuTheme.PanelNoise))
        {
            surface = _canvas.Surfaces.CreateSurface(texture);
        }

        CompositionSurfaceBrush brush = _compositor.CreateSurfaceBrush(surface);
        _background.Brush = brush;

        _backdropBrush?.Dispose();
        _backdropSurface?.Dispose();
        _backdropBrush = brush;
        _backdropSurface = surface;
    }

    private void BuildHeader()
    {
        SpriteVisual title = _canvas.Text(_page.Title, MenuTheme.TitleFont, MenuTheme.TextPrimary);
        SpriteVisual? subtitle = _page.Subtitle is null
            ? null
            : _canvas.Text(_page.Subtitle, MenuTheme.SubtitleFont, MenuTheme.TextSecondary);

        // Title and subtitle as one block, centred together against the header. Centring the
        // title alone and hanging the subtitle below it puts the pair visibly low.
        float block = title.Size.Y + (subtitle is null ? 0f : subtitle.Size.Y + 1f);
        float top = MathF.Round((MenuTheme.HeaderHeight - block) / 2f);
        float left = MenuTheme.PanelPadX + 4f;

        title.Offset = new Vector3(left, top, 0f);
        _panel.Children.InsertAtTop(title);

        if (subtitle is not null)
        {
            subtitle.Offset = new Vector3(left, top + title.Size.Y + 1f, 0f);
            _panel.Children.InsertAtTop(subtitle);
        }

        float size = MenuTheme.CloseButtonSize;
        _closeLeft = PanelWidth - MenuTheme.PanelPadX - size;
        _closeTop = MathF.Round((MenuTheme.HeaderHeight - size) / 2f);

        _closeButton = _canvas.Rect(size, size, size / 2f, MenuTheme.SegmentWell);
        _closeButton.Offset = new Vector3(_closeLeft, _closeTop, 0f);
        _panel.Children.InsertAtTop(_closeButton.Visual);

        // A multiplication sign, not one of the dedicated cross glyphs: it is in every font that
        // ships with Windows, so it cannot fall back to a substitute of a different weight.
        SpriteVisual glyph = _canvas.Text("×", MenuTheme.TitleFont, MenuTheme.TextSecondary);
        glyph.Offset = new Vector3(
            MathF.Round((size - glyph.Size.X) / 2f), MathF.Round((size - glyph.Size.Y) / 2f), 0f);
        _closeButton.Visual.Children.InsertAtTop(glyph);

        // A hairline under the header, so the title reads as chrome rather than as the first row.
        RoundRect rule = _canvas.Rect(PanelWidth, 1f, 0f, MenuTheme.Separator);
        rule.Offset = new Vector3(0f, MenuTheme.HeaderHeight - 1f, 0f);
        _panel.Children.InsertAtTop(rule.Visual);
    }

    private void BuildContent()
    {
        float cardWidth = PanelWidth - MenuTheme.PanelPadX * 2f;
        float y = 0f;

        // Three passes over the page, in z-order: cards and their hairlines first, then the
        // highlight, then the rows on top of both. Composition draws children in insertion
        // order, and the highlight has to be able to slide under any row in any card.
        var placements = new List<(MenuSection Section, float Top)>();

        foreach (MenuSection section in _page.Sections)
        {
            if (section.Header is not null)
            {
                SpriteVisual header = _canvas.Text(section.Header, MenuTheme.SectionFont, MenuTheme.TextHeader);
                header.Offset = new Vector3(
                    MenuTheme.PanelPadX + MenuTheme.RowPadX,
                    MathF.Round(y + (MenuTheme.SectionHeaderHeight - header.Size.Y) - 6f),
                    0f);
                _content.Children.InsertAtTop(header);
                y += MenuTheme.SectionHeaderHeight;
            }

            float cardTop = y;
            float cardHeight = section.Rows.Sum(row => row.Height);

            RoundRect card = _canvas.Rect(
                cardWidth, cardHeight, MenuTheme.CardRadius, MenuTheme.CardFill, MenuTheme.CardBorder);
            card.Offset = new Vector3(MenuTheme.PanelPadX, cardTop, 0f);
            _content.Children.InsertAtTop(card.Visual);

            float rowTop = cardTop;
            for (int i = 0; i < section.Rows.Count; i++)
            {
                rowTop += section.Rows[i].Height;

                // Between rows only. A hairline under the last one would sit on the card's
                // bottom edge and read as a double border.
                if (i == section.Rows.Count - 1) continue;

                RoundRect separator = _canvas.Rect(
                    cardWidth - MenuTheme.RowPadX, 1f, 0f, MenuTheme.Separator);
                separator.Offset = new Vector3(
                    MenuTheme.PanelPadX + MenuTheme.RowPadX, MathF.Round(rowTop), 0f);
                _content.Children.InsertAtTop(separator.Visual);
            }

            placements.Add((section, cardTop));
            _cardTops[section] = cardTop;
            y = cardTop + cardHeight + MenuTheme.SectionGap;
        }

        BuildHighlight(cardWidth);

        foreach ((MenuSection section, float top) in placements)
        {
            float rowTop = top;
            foreach (MenuRow row in section.Rows)
            {
                row.Changed = () => Changed?.Invoke();
                row.Dismiss = () => Dismissed?.Invoke();
                row.Attach(_canvas, _content, MenuTheme.PanelPadX, rowTop, cardWidth);
                _rowSection[row] = section;
                rowTop += row.Height;
            }
        }
    }

    /// <summary>
    /// The one highlight, shared by every row.
    ///
    /// Sized for the tallest row it will ever have to cover and never resized as a visual - only
    /// its geometry changes. A ShapeVisual clips its shapes to its own Size, so a highlight that
    /// grew both together would be clipped to its old bounds for the length of the animation and
    /// arrive at the right size having spent the whole journey the wrong shape.
    /// </summary>
    private void BuildHighlight(float cardWidth)
    {
        float width = cardWidth - MenuTheme.HighlightInset * 2f;

        _highlight = _canvas.Rect(
            width, MenuTheme.TallRowHeight, MenuTheme.HighlightRadius, MenuTheme.RowHover);
        _highlight.Opacity = 0f;
        _content.Children.InsertAtTop(_highlight.Visual);

        _highlightTravel = _canvas.Animate(_highlight.Visual, "Offset.Y");
        _highlightSize = _canvas.Animate(_highlight.Geometry, "Size");
        _highlightFade = _canvas.Animate(_highlight.Visual, "Opacity");
    }

    /// <summary>
    /// An overlay scroll indicator, in the macOS idiom: no track, no arrows, no reserved width,
    /// and invisible until the pointer is in the panel.
    ///
    /// It is here for discoverability rather than for control. Without it a panel whose last row
    /// is cut off by the clip looks broken rather than scrollable, and there is nothing else on
    /// screen to say which of the two it is.
    /// </summary>
    private void BuildScrollBar(ContainerVisual viewport)
    {
        if (!Scrolls) return;

        float length = MathF.Max(
            MenuTheme.ScrollBarMinLength, ViewportHeight * (ViewportHeight / ContentHeight));

        _thumb = _canvas.Rect(
            MenuTheme.ScrollBarWidth, length, MenuTheme.ScrollBarWidth / 2f, MenuTheme.ScrollThumb);
        _thumb.Offset = new Vector3(
            PanelWidth - MenuTheme.ScrollBarInset - MenuTheme.ScrollBarWidth, 0f, 0f);
        _thumb.Opacity = 0f;
        viewport.Children.InsertAtTop(_thumb.Visual);

        _thumbTravel = _canvas.Animate(_thumb.Visual, "Offset.Y");
        _thumbFade = _canvas.Animate(_thumb.Visual, "Opacity");
    }

    /// <summary>Where the thumb belongs for a given scroll offset.</summary>
    private float ThumbY(float scroll)
    {
        if (_thumb is null) return 0f;

        float range = ContentHeight - ViewportHeight;
        float travel = ViewportHeight - _thumb.Geometry.Size.Y;
        return range <= 0f ? 0f : scroll / range * travel;
    }

    private void ShowScrollBar(bool shown)
    {
        if (_thumb is not RoundRect thumb || _thumbShown == shown) return;
        _thumbShown = shown;

        float target = shown ? 1f : 0f;
        ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, target, MenuTheme.EaseOut(_compositor));
        fade.Duration = TimeSpan.FromMilliseconds(shown ? 130 : 220);

        _thumbFade?.Run(fade, () => thumb.Opacity = target);
    }

    // ---- Pointer -------------------------------------------------------------------------

    /// <summary>
    /// Where a panel-space point lands in the scrolling content.
    ///
    /// Measured against the scroll target rather than the animated position. The two differ for
    /// the ~100ms a wheel notch takes to settle, and using the target means a row is hoverable
    /// from the moment it is on its way to the pointer rather than only once it has arrived.
    /// </summary>
    private float ContentY(float y) => y - MenuTheme.HeaderHeight + _scroll;

    /// <summary>Finds the row under a point given in panel coordinates, or null.</summary>
    private MenuRow? RowAt(float x, float y)
    {
        if (y < MenuTheme.HeaderHeight || y >= MenuTheme.HeaderHeight + ViewportHeight) return null;

        float contentY = ContentY(y);
        foreach (MenuRow row in _targets)
        {
            if (row.Contains(x, contentY)) return row;
        }

        return null;
    }

    /// <summary>Converts a panel-space point into the coordinates a row expects.</summary>
    private (float X, float Y) LocalTo(MenuRow row, float x, float y) =>
        (x - row.Left, ContentY(y) - row.Top);

    private bool OverCloseButton(float x, float y) =>
        x >= _closeLeft && x < _closeLeft + MenuTheme.CloseButtonSize &&
        y >= _closeTop && y < _closeTop + MenuTheme.CloseButtonSize;

    public void PointerMove(float x, float y)
    {
        ShowScrollBar(true);

        if (_pressed is not null)
        {
            if (_dragging)
            {
                _dragPointerY = y;
                PlaceDragged();
                return;
            }

            // A press only becomes a reorder once the pointer has actually gone somewhere.
            // Without the threshold every click on a row would shuffle the list by a pixel and
            // then put it back, which reads as the list being unable to sit still.
            if (_dragArmed && MathF.Abs(y - _pressY) >= MenuTheme.DragThreshold)
            {
                BeginDrag(y);
                return;
            }

            if (_pressed.Draggable)
            {
                (float dragX, float dragY) = LocalTo(_pressed, x, y);
                _pressed.OnDrag(dragX, dragY);
            }

            return;
        }

        SetCloseHovered(OverCloseButton(x, y));

        MenuRow? hovered = _closeHovered ? null : RowAt(x, y);
        SetHovered(hovered);

        if (hovered is null) return;

        (float localX, float localY) = LocalTo(hovered, x, y);
        hovered.OnMove(localX, localY);
    }

    public void PointerLeave()
    {
        if (_pressed is not null) return;
        ShowScrollBar(false);
        SetCloseHovered(false);
        SetHovered(null);
    }

    public void PointerDown(float x, float y)
    {
        if (OverCloseButton(x, y))
        {
            _closePressed = true;
            _closeButton!.Color = MenuTheme.RowPressed;
            return;
        }

        MenuRow? row = RowAt(x, y);
        if (row is null) return;

        _pressed = row;
        _pressY = y;
        SetHovered(row);

        if (_highlight is not null) _highlight.Color = MenuTheme.RowPressed;

        (float localX, float localY) = LocalTo(row, x, y);

        _dragArmed =
            _rowSection.TryGetValue(row, out MenuSection? section) &&
            section.Reorder is not null &&
            row.CanDragFrom(localX, localY);

        row.OnPress(localX, localY);
    }

    public void PointerUp(float x, float y)
    {
        if (_closePressed)
        {
            _closePressed = false;
            _closeButton!.Color = OverCloseButton(x, y) ? MenuTheme.RowHover : MenuTheme.SegmentWell;
            if (OverCloseButton(x, y)) Dismissed?.Invoke();
            return;
        }

        MenuRow? row = _pressed;
        _pressed = null;
        _dragArmed = false;
        if (row is null) return;

        if (_highlight is not null) _highlight.Color = MenuTheme.RowHover;

        if (_dragging)
        {
            // A reorder is not a click on the row it started from. Running OnRelease here as
            // well would delete the entry the user had just finished moving.
            EndDrag();

            // The highlight was faded out when the row was picked up, and the row under the
            // pointer is usually the one that was dragged - so SetHovered would see no change and
            // leave the highlight invisible. Place it outright instead, which is also what it
            // should do after a drop: appear where the row landed rather than fly there.
            MenuRow? under = RowAt(x, y);
            if (under is not null && ReferenceEquals(_hovered, under)) PlaceHighlight(under, instant: true);
            else SetHovered(under);

            return;
        }

        (float localX, float localY) = LocalTo(row, x, y);
        row.OnRelease(localX, localY, row.Contains(x, ContentY(y)));

        // Where the pointer ended up may be a different row entirely, after a drag.
        SetHovered(RowAt(x, y));
    }

    /// <summary>Ends a press that was interrupted - the window losing capture, say.</summary>
    public void CancelPress()
    {
        _closePressed = false;
        if (_closeButton is not null) _closeButton.Color = MenuTheme.SegmentWell;

        // A reorder interrupted this way is committed, not undone. The user has been watching
        // the list rearrange itself for the length of the drag; snapping it back to where it
        // started would be the surprising outcome, not the safe one.
        if (_dragging) EndDrag();

        _dragArmed = false;
        if (_pressed is null) return;
        _pressed = null;
        if (_highlight is not null) _highlight.Color = MenuTheme.RowHover;
    }

    // ---- Reorder ---------------------------------------------------------------------------

    /// <summary>
    /// Picks the row up.
    ///
    /// It leaves the highlight behind - a lifted row is its own emphasis, and a highlight chasing
    /// it around would be two things saying the same thing - and moves to the top of the content's
    /// z-order so it passes over its neighbours rather than under them.
    /// </summary>
    private void BeginDrag(float pointerY)
    {
        if (_pressed is not MenuRow row) return;
        if (!_rowSection.TryGetValue(row, out MenuSection? section)) return;
        if (row.Root is not ContainerVisual root) return;

        _dragArmed = false;
        _dragging = true;
        _dragSection = section;
        _dragRow = row;
        _dragOrder = [.. section.Rows];
        _dragFrom = _dragOrder.IndexOf(row);
        _dragIndex = _dragFrom;
        _dragGrab = ContentY(_pressY) - row.Top;
        _dragPointerY = pointerY;

        _content.Children.Remove(root);
        _content.Children.InsertAtTop(root);

        root.CenterPoint = new Vector3(root.Size.X / 2f, root.Size.Y / 2f, 0f);
        var lifted = new Vector3(MenuTheme.DragLift, MenuTheme.DragLift, 1f);
        Lift(row).Run(MenuTheme.Spring(_compositor, lifted), () => root.Scale = lifted);

        Fade(0f);
        PlaceDragged();
    }

    /// <summary>
    /// Puts the dragged row exactly where the pointer is, and decides which slot that means.
    ///
    /// No spring on the row itself, for the same reason the slider's knob has none: smoothing
    /// something a finger is already holding makes the control feel like it is arguing. The
    /// springs are on the neighbours, which are moving of their own accord.
    /// </summary>
    private void PlaceDragged()
    {
        if (_dragRow is not MenuRow row || _dragSection is not MenuSection section) return;
        if (row.Root is not ContainerVisual root) return;
        if (!_cardTops.TryGetValue(section, out float cardTop)) return;

        float cardHeight = section.Rows.Sum(other => other.Height);
        float wanted = ContentY(_dragPointerY) - _dragGrab;

        // Clamped for the picture, unclamped for the decision. Clamping both means a row dragged
        // to the very top of the card sits with its centre exactly on the first row's midpoint -
        // a tie, which resolves the wrong way and makes the first slot unreachable.
        root.Offset = new Vector3(
            row.Left, Math.Clamp(wanted, cardTop, cardTop + cardHeight - row.Height), 0f);

        int index = SlotFor(row, cardTop, wanted + row.Height / 2f);
        if (index == _dragIndex) return;

        _dragIndex = index;
        Reflow(cardTop, row);
    }

    /// <summary>
    /// Which slot the dragged row's centre has reached, measured against the others laid out
    /// without it. Walked rather than divided, so a section of rows with different heights would
    /// still land in the right place.
    ///
    /// The comparison has to include equality. With the row sitting exactly where it started, its
    /// centre lands precisely on the midpoint of the row that closed the gap behind it - so a
    /// strict test answers "one further down" before the user has moved anything, and every drag
    /// begins by shuffling the list by one.
    /// </summary>
    private int SlotFor(MenuRow dragged, float cardTop, float center)
    {
        float y = cardTop;
        int index = 0;

        foreach (MenuRow row in _dragOrder)
        {
            if (ReferenceEquals(row, dragged)) continue;
            if (center <= y + row.Height / 2f) return index;

            y += row.Height;
            index++;
        }

        return index;
    }

    /// <summary>Re-slots everything and springs the neighbours into their new places.</summary>
    private void Reflow(float cardTop, MenuRow dragged)
    {
        var order = new List<MenuRow>(_dragOrder.Count);
        foreach (MenuRow row in _dragOrder)
        {
            if (!ReferenceEquals(row, dragged)) order.Add(row);
        }

        order.Insert(_dragIndex, dragged);
        _dragOrder = order;

        float y = cardTop;
        foreach (MenuRow row in _dragOrder)
        {
            // The dragged row is given its slot too, even though its picture is under the
            // pointer. That is what makes it hit-testable in the right place the instant it is
            // dropped, rather than once the settle animation has finished.
            row.SetTop(y);
            if (!ReferenceEquals(row, dragged)) Slide(row, y);
            y += row.Height;
        }
    }

    private void EndDrag()
    {
        _dragging = false;

        if (_dragRow is not MenuRow row || _dragSection is not MenuSection section) return;

        Slide(row, row.Top);

        if (row.Root is ContainerVisual root)
            Lift(row).Run(MenuTheme.Spring(_compositor, Vector3.One), () => root.Scale = Vector3.One);

        section.Rows.Clear();
        section.Rows.AddRange(_dragOrder);

        int from = _dragFrom;
        int to = _dragIndex;

        _dragRow = null;
        _dragSection = null;
        _dragOrder = [];

        if (from != to) section.Reorder?.Invoke(from, to);
    }

    private void Slide(MenuRow row, float top)
    {
        if (row.Root is not ContainerVisual root) return;

        float left = row.Left;
        Travel(row).Run(
            MenuTheme.Spring(_compositor, top), () => root.Offset = new Vector3(left, top, 0f));
    }

    private AnimatedProperty Travel(MenuRow row) =>
        _rowTravel.TryGetValue(row, out AnimatedProperty? travel)
            ? travel
            : _rowTravel[row] = _canvas.Animate(row.Root!, "Offset.Y");

    private AnimatedProperty Lift(MenuRow row) =>
        _rowLift.TryGetValue(row, out AnimatedProperty? lift)
            ? lift
            : _rowLift[row] = _canvas.Animate(row.Root!, "Scale");

    /// <summary>
    /// Scrolls the list while a drag is held against the top or bottom of the viewport.
    ///
    /// Driven by a clock rather than by mouse movement, because the case that needs it is a
    /// pointer held still at the edge - which delivers no movement at all. The window runs the
    /// clock only between a press and its release, so it cannot outlive the gesture.
    /// </summary>
    public void DragTick()
    {
        if (!_dragging) return;

        const float Edge = 44f;
        const float Step = 9f;

        float range = ContentHeight - ViewportHeight;
        if (range <= 0f) return;

        float top = MenuTheme.HeaderHeight;
        float bottom = top + ViewportHeight;

        float delta = 0f;
        if (_dragPointerY < top + Edge) delta = -Step;
        else if (_dragPointerY > bottom - Edge) delta = Step;
        if (delta == 0f) return;

        float target = Math.Clamp(_scroll + delta, 0f, range);
        if (target == _scroll) return;

        SetScroll(target, animated: false);
        PlaceDragged();
    }

    private void SetCloseHovered(bool hovered)
    {
        if (_closeHovered == hovered || _closeButton is null) return;
        _closeHovered = hovered;
        _closeButton.Color = hovered ? MenuTheme.RowHover : MenuTheme.SegmentWell;
    }

    /// <summary>
    /// Moves the highlight, and tells the rows either side which one the pointer is on.
    ///
    /// Arriving from nothing places the highlight without animating and then fades it in;
    /// otherwise it springs across. Sliding in from wherever it was left twenty seconds ago is
    /// motion that describes history rather than intent, and it reads as the panel catching up.
    /// </summary>
    private void SetHovered(MenuRow? row)
    {
        if (ReferenceEquals(_hovered, row)) return;

        bool wasShowing = _hovered is not null;
        _hovered?.OnHover(false);
        _hovered = row;
        _hovered?.OnHover(true);

        if (row is null)
        {
            Fade(0f);
            return;
        }

        PlaceHighlight(row, instant: !wasShowing);
    }

    private void PlaceHighlight(MenuRow row, bool instant)
    {
        if (_highlight is not RoundRect highlight) return;

        float left = MenuTheme.PanelPadX + MenuTheme.HighlightInset;
        float targetY = row.Top + MenuTheme.HighlightInset;
        var targetSize = new Vector2(
            highlight.Geometry.Size.X, row.Height - MenuTheme.HighlightInset * 2f);

        if (instant)
        {
            highlight.Offset = new Vector3(left, targetY, 0f);
            highlight.Geometry.Size = targetSize;
            Fade(1f);
            return;
        }

        _highlightTravel?.Run(
            MenuTheme.Spring(_compositor, targetY),
            () => highlight.Offset = new Vector3(left, targetY, 0f));

        _highlightSize?.Run(
            MenuTheme.Spring(_compositor, targetSize),
            () => highlight.Geometry.Size = targetSize);
    }

    private void Fade(float target)
    {
        if (_highlight is not RoundRect highlight) return;

        ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, target, MenuTheme.EaseOut(_compositor));
        fade.Duration = TimeSpan.FromMilliseconds(110);

        _highlightFade?.Run(fade, () => highlight.Opacity = target);
    }

    /// <summary>
    /// Scrolls by whole wheel notches. Returns false when there is nothing to scroll, so the
    /// window can leave the message to DefWindowProc rather than swallowing it.
    /// </summary>
    public bool Wheel(int notches)
    {
        float range = ContentHeight - ViewportHeight;
        if (range <= 0f) return false;

        const float NotchPixels = 52f;
        float target = Math.Clamp(_scroll - notches * NotchPixels, 0f, range);
        if (target == _scroll) return true;

        SetScroll(target, animated: true);
        return true;
    }

    /// <summary>
    /// How far the list is scrolled, so a rebuild can put it back where the user left it.
    /// Adding an entry moves every row below it; landing back at the top as well would lose the
    /// place twice over.
    /// </summary>
    public float Scroll => _scroll;

    public void RestoreScroll(float scroll)
    {
        float range = ContentHeight - ViewportHeight;
        if (range <= 0f) return;

        SetScroll(Math.Clamp(scroll, 0f, range), animated: false);
    }

    /// <summary>
    /// Moves the content and the scroll thumb together.
    ///
    /// Springs for a wheel notch, and nothing at all for an auto-scroll during a drag: the row
    /// under the pointer is placed outright, so a viewport easing along behind it would slide the
    /// list out from under the very thing being aimed at.
    /// </summary>
    private void SetScroll(float target, bool animated)
    {
        _scroll = target;
        ContainerVisual content = _content;

        if (animated)
        {
            _scrollTravel?.Run(
                MenuTheme.Spring(_compositor, -target),
                () => content.Offset = new Vector3(0f, -target, 0f));
        }
        else
        {
            content.Offset = new Vector3(0f, -target, 0f);
        }

        if (_thumb is not RoundRect thumb) return;

        float thumbY = ThumbY(target);
        float thumbX = thumb.Offset.X;

        // The thumb rides the same spring rather than being placed outright, so it arrives with
        // the content instead of snapping ahead of it.
        if (animated)
        {
            _thumbTravel?.Run(
                MenuTheme.Spring(_compositor, thumbY),
                () => thumb.Offset = new Vector3(thumbX, thumbY, 0f));
        }
        else
        {
            thumb.Offset = new Vector3(thumbX, thumbY, 0f);
        }
    }

    /// <summary>Re-reads every row's value, for when the config changed from outside the panel.</summary>
    public void Refresh()
    {
        foreach (MenuRow row in _page.AllRows) row.Refresh();
    }

    public void Dispose()
    {
        _canvas.RetireAnimations();

        // Not in _resources, so not covered by the loop below. See the fields.
        _backdropBrush?.Dispose();
        _backdropSurface?.Dispose();

        for (int i = _resources.Count - 1; i >= 0; i--) _resources[i].Dispose();
        _resources.Clear();
    }
}
