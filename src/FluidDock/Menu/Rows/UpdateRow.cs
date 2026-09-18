using System.Numerics;
using Windows.UI.Composition;

namespace FluidDock.Menu.Rows;

/// <summary>
/// The in-app update, as one row: an action on the left that says what clicking it would do now,
/// and a detail on the right that says where things are.
///
/// Both strings change while the panel is open - "检查更新" becomes "正在检查…" becomes
/// "更新到 V0.7", and the right-hand side counts the download up. Neither is owned here: the row
/// asks the <see cref="Updater"/> every time it is refreshed, so a row rebuilt in the middle of a
/// download shows the download where it is rather than starting over from "检查更新".
/// </summary>
internal sealed class UpdateRow : MenuRow
{
    private readonly Updater _updater;
    private MutableText? _action;
    private MutableText? _status;

    public UpdateRow(Updater updater) : base("检查更新")
    {
        _updater = updater;
    }

    protected override void Build(MenuCanvas canvas)
    {
        _action = canvas.Changing(MenuTheme.RowFont, MenuTheme.TextAccent, LeftAlign);
        Root!.Children.InsertAtTop(_action.Visual);

        _status = canvas.Changing(MenuTheme.ValueFont, MenuTheme.TextSecondary, RightAlign);
        Root.Children.InsertAtTop(_status.Visual);

        Refresh();
    }

    private void LeftAlign(SpriteVisual visual) =>
        visual.Offset = new Vector3(MenuTheme.RowPadX, MenuCanvas.CenterY(Height, visual.Size.Y), 0f);

    private void RightAlign(SpriteVisual visual) =>
        visual.Offset = new Vector3(
            Width - MenuTheme.RowPadX - visual.Size.X,
            MenuCanvas.CenterY(Height, visual.Size.Y),
            0f);

    public override void Refresh()
    {
        _action?.Set(_updater.ActionText);
        _status?.Set(_updater.StatusText);
    }

    public override void OnRelease(float x, float y, bool inside)
    {
        if (inside) _updater.Run();
    }
}
