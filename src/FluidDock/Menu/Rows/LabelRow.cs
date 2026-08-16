using System.Numerics;
using Windows.UI.Composition;

namespace FluidDock.Menu.Rows;

/// <summary>
/// A read-only line: caption on the left, value on the right.
///
/// The simplest row there is, and the one worth reading first if you are about to write a new
/// kind of control - it shows the whole contract in twenty lines.
/// </summary>
internal sealed class LabelRow : MenuRow
{
    private readonly Func<string> _value;
    private MutableText? _text;

    public LabelRow(string title, string value) : this(title, () => value) { }

    public LabelRow(string title, Func<string> value) : base(title)
    {
        _value = value;
    }

    public override bool Interactive => false;

    protected override void Build(MenuCanvas canvas)
    {
        SpriteVisual title = canvas.Text(Title, MenuTheme.RowFont, MenuTheme.TextPrimary);
        title.Offset = new Vector3(MenuTheme.RowPadX, MenuCanvas.CenterY(Height, title.Size.Y), 0f);
        Root!.Children.InsertAtTop(title);

        _text = canvas.Changing(MenuTheme.ValueFont, MenuTheme.TextSecondary, RightAlign);
        Root.Children.InsertAtTop(_text.Visual);
        _text.Set(_value());
    }

    /// <summary>Re-run whenever the string changes, since a new value is a new width.</summary>
    private void RightAlign(SpriteVisual visual) =>
        visual.Offset = new Vector3(
            Width - MenuTheme.RowPadX - visual.Size.X,
            MenuCanvas.CenterY(Height, visual.Size.Y),
            0f);

    public override void Refresh() => _text?.Set(_value());
}
