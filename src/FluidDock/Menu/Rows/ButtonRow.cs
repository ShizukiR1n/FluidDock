using System.Numerics;
using Windows.UI.Composition;
using GdiColor = System.Drawing.Color;

namespace FluidDock.Menu.Rows;

/// <summary>
/// A row that does something when clicked.
///
/// It draws no button. In macOS's settings cards an action is a line of accent-coloured text
/// that lights up under the pointer, and the row itself is the target - which is both larger to
/// hit and quieter to look at than a bordered control would be. The lighting-up is the panel's
/// shared hover highlight, so there is nothing here but a label and an action.
/// </summary>
internal sealed class ButtonRow : MenuRow
{
    private readonly Action _action;
    private readonly string? _detail;
    private readonly bool _closes;
    private readonly GdiColor _color;

    /// <param name="closesPanel">
    /// For actions that only make sense with the panel out of the way - opening a folder, or
    /// quitting. Dismissal runs first so the closing animation is not competing with a window
    /// appearing over it, or racing a shutdown.
    /// </param>
    public ButtonRow(string title, Action action, bool danger = false, bool closesPanel = false, string? detail = null)
        : base(title)
    {
        _action = action;
        _detail = detail;
        _closes = closesPanel;
        _color = danger ? MenuTheme.TextDanger : MenuTheme.TextAccent;
    }

    protected override void Build(MenuCanvas canvas)
    {
        SpriteVisual title = canvas.Text(Title, MenuTheme.RowFont, _color);
        title.Offset = new Vector3(MenuTheme.RowPadX, MenuCanvas.CenterY(Height, title.Size.Y), 0f);
        Root!.Children.InsertAtTop(title);

        if (string.IsNullOrEmpty(_detail)) return;

        SpriteVisual detail = canvas.Text(_detail, MenuTheme.ValueFont, MenuTheme.TextSecondary);
        detail.Offset = new Vector3(
            Width - MenuTheme.RowPadX - detail.Size.X, MenuCanvas.CenterY(Height, detail.Size.Y), 0f);
        Root.Children.InsertAtTop(detail);
    }

    public override void OnRelease(float x, float y, bool inside)
    {
        if (!inside) return;

        if (_closes) Dismiss?.Invoke();
        _action();
    }
}
