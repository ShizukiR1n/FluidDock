using System.Numerics;
using Windows.UI.Composition;

namespace FluidDock.Menu;

/// <summary>
/// One line of the settings panel.
///
/// This is the extension point the whole panel is built around. A new kind of control is a new
/// subclass with a Build method and, if it reacts to the pointer, two or three of the handlers
/// below - it does not need to know how the panel is laid out, scrolled, hit-tested, or how its
/// animations are released. Everything a row is given comes through <see cref="MenuCanvas"/>,
/// and everything it reports goes back through <see cref="Changed"/>.
///
/// Coordinates handed to the pointer handlers are local to the row: (0,0) is its top-left corner
/// whatever the panel has been scrolled to.
/// </summary>
internal abstract class MenuRow
{
    protected MenuRow(string title)
    {
        Title = title;
    }

    public string Title { get; }

    public virtual float Height => MenuTheme.RowHeight;

    /// <summary>Whether the row responds to the pointer at all. A read-only row does not light up.</summary>
    public virtual bool Interactive => true;

    /// <summary>
    /// Whether a press on this row should hold the mouse until it is released. Set by anything
    /// dragged rather than clicked, so a slider does not lose the pointer at the panel's edge.
    /// </summary>
    public virtual bool Draggable => false;

    /// <summary>
    /// Whether a press here may turn into a reorder, for rows in a section that allows it.
    ///
    /// False over a row's own controls. Without it a row that is both the drag handle and a
    /// collection of buttons has to guess which the user meant, and it guesses wrong on exactly
    /// the press that matters - the one aimed at the small target.
    /// </summary>
    public virtual bool CanDragFrom(float x, float y) => true;

    /// <summary>Raised when the row has changed a value the config should be written for.</summary>
    public Action? Changed { get; set; }

    /// <summary>Raised by a row whose action only makes sense with the panel out of the way.</summary>
    public Action? Dismiss { get; set; }

    /// <summary>Where this row sits inside the scrolling content, in content coordinates.</summary>
    public float Top { get; private set; }

    public float Left { get; private set; }

    public ContainerVisual? Root { get; private set; }

    protected float Width { get; private set; }

    /// <summary>Whether a point in content coordinates falls on this row.</summary>
    public bool Contains(float x, float y) =>
        x >= Left && x < Left + Width && y >= Top && y < Top + Height;

    /// <summary>
    /// Records where the row now sits, without moving it.
    ///
    /// Split from the visual placement because a reorder animates: the row is hit-testable at its
    /// new position from the moment the decision is made, while the picture of it is still on its
    /// way there. Doing both at once means a row you can see but cannot click for 200ms.
    /// </summary>
    internal void SetTop(float top) => Top = top;

    internal void Attach(MenuCanvas canvas, ContainerVisual parent, float left, float top, float width)
    {
        Left = left;
        Top = top;
        Width = width;

        Root = canvas.Container(width, Height);
        Root.Offset = new Vector3(left, top, 0f);
        parent.Children.InsertAtTop(Root);

        Build(canvas);
    }

    protected abstract void Build(MenuCanvas canvas);

    /// <summary>Re-reads the underlying value. Called when the config changed from outside the panel.</summary>
    public virtual void Refresh() { }

    public virtual void OnHover(bool hovered) { }

    /// <summary>
    /// The pointer moved inside this row while nothing was pressed.
    ///
    /// The panel's shared highlight says which row the pointer is on and nothing finer, so a row
    /// with more than one target of its own needs this to know which of them is under the
    /// cursor. Rows that are one target ignore it, which is most of them.
    /// </summary>
    public virtual void OnMove(float x, float y) { }

    public virtual void OnPress(float x, float y) { }

    public virtual void OnDrag(float x, float y) { }

    /// <summary>
    /// Ends a press. <paramref name="inside"/> is false when the pointer was released away from
    /// the row, which is a cancel for anything click-like and merely the end of the gesture for
    /// anything dragged.
    /// </summary>
    public virtual void OnRelease(float x, float y, bool inside) { }
}

/// <summary>
/// A group of rows under one caption, drawn as a rounded card with hairlines between the rows.
/// Straight out of macOS System Settings, and the reason the panel reads as organised rather
/// than as a list of controls.
/// </summary>
internal sealed class MenuSection
{
    public MenuSection(string? header = null)
    {
        Header = header;
    }

    public string? Header { get; }

    public List<MenuRow> Rows { get; } = [];

    /// <summary>
    /// Set to make the section's rows draggable into a different order, and to be told when one
    /// lands somewhere new. The two indices are positions in <see cref="Rows"/>.
    ///
    /// Only sections whose rows are all interchangeable may set this - the panel will happily
    /// drag a slider past a button if asked to, and the result is a card whose contents have
    /// shuffled for no reason the user can undo. In practice that means a section holding one
    /// kind of row and nothing else.
    /// </summary>
    public Action<int, int>? Reorder { get; set; }
}

/// <summary>
/// The whole panel's content. Built in one place - see MenuDefinition - so that adding a setting
/// is a line in a list rather than a change to anything that draws.
/// </summary>
internal sealed class MenuPage
{
    public MenuPage(string title, string? subtitle = null)
    {
        Title = title;
        Subtitle = subtitle;
    }

    public string Title { get; }

    public string? Subtitle { get; }

    public List<MenuSection> Sections { get; } = [];

    public IEnumerable<MenuRow> AllRows => Sections.SelectMany(section => section.Rows);
}
