using System.Numerics;
using FluidDock.Visuals;
using Windows.UI.Composition;

namespace FluidDock.Menu.Rows;

/// <summary>
/// One entry in the dock, as it appears in the settings list: its icon, its name, and - only
/// while the pointer is on it - the three things that can be done to it.
///
/// The actions hide at rest on purpose. A list of a dozen apps each carrying three permanently
/// visible controls is a control panel; the same list with the controls arriving under the
/// pointer is a list of apps. Finder's sidebar, Music's track list and Photos' album grid all do
/// this, and it is most of why they read as calm.
///
/// The row is also the drag handle. <see cref="CanDragFrom"/> is what keeps the two gestures
/// apart: a press anywhere but the action cluster can become a reorder, and a press on the
/// cluster stays a click on whatever it landed on.
/// </summary>
internal sealed class DockItemRow : MenuRow
{
    /// <summary>What the pointer is over inside the row, which is not something a highlight can say.</summary>
    private enum Target
    {
        None,
        ChooseIcon,
        ResetIcon,
        Remove,
    }

    private readonly DockItemConfig _item;
    private readonly DockItems _items;

    private ContainerVisual? _actions;
    private AnimatedProperty? _reveal;

    private RoundRect? _removeWell;
    private SpriteVisual? _chooseText;
    private SpriteVisual? _resetText;

    private float _chooseLeft;
    private float _chooseRight;
    private float _resetLeft;
    private float _resetRight;
    private float _removeLeft;

    private Target _over;
    private bool _hovered;

    public DockItemRow(DockItemConfig item, DockItems items)
        : base(item.Label ?? item.Path)
    {
        _item = item;
        _items = items;
    }

    public DockItemConfig Item => _item;

    /// <summary>Whether this entry is showing an icon the user chose rather than the shell's.</summary>
    private bool HasCustomIcon => !string.IsNullOrWhiteSpace(_item.Icon);

    protected override void Build(MenuCanvas canvas)
    {
        SpriteVisual icon = canvas.Icon(_item, MenuTheme.ItemIconSize);
        icon.Offset = new Vector3(
            MenuTheme.RowPadX, MenuCanvas.CenterY(Height, MenuTheme.ItemIconSize), 0f);
        Root!.Children.InsertAtTop(icon);

        // Right to left, because the actions are anchored to the row's right edge and the label
        // gets whatever is left over. Laying it out the other way round means guessing how wide
        // the actions will be, and the guess is wrong the moment a label is in Chinese.
        float right = Width - MenuTheme.RowPadX;

        _removeLeft = right - MenuTheme.RemoveButtonSize;
        right = _removeLeft - MenuTheme.ItemActionGap;

        _actions = canvas.Container(Width, Height);
        _actions.Opacity = 0f;
        Root.Children.InsertAtTop(_actions);
        _reveal = canvas.Animate(_actions, "Opacity");

        if (HasCustomIcon)
        {
            _resetText = canvas.Text("默认", MenuTheme.ValueFont, MenuTheme.TextSecondary);
            _resetRight = right;
            _resetLeft = right - _resetText.Size.X;
            _resetText.Offset = new Vector3(
                _resetLeft, MenuCanvas.CenterY(Height, _resetText.Size.Y), 0f);
            _actions.Children.InsertAtTop(_resetText);

            right = _resetLeft - MenuTheme.ItemActionGap;
        }

        _chooseText = canvas.Text("图标", MenuTheme.ValueFont, MenuTheme.TextAccent);
        _chooseRight = right;
        _chooseLeft = right - _chooseText.Size.X;
        _chooseText.Offset = new Vector3(
            _chooseLeft, MenuCanvas.CenterY(Height, _chooseText.Size.Y), 0f);
        _actions.Children.InsertAtTop(_chooseText);

        float size = MenuTheme.RemoveButtonSize;
        _removeWell = canvas.Rect(size, size, size / 2f, MenuTheme.SegmentWell);
        _removeWell.Offset = new Vector3(_removeLeft, MenuCanvas.CenterY(Height, size), 0f);
        _actions.Children.InsertAtTop(_removeWell.Visual);

        // The same multiplication sign the panel's own close button uses, for the same reason:
        // it is in every font Windows ships, so it cannot fall back to a substitute of a
        // different weight halfway down a list.
        SpriteVisual cross = canvas.Text("×", MenuTheme.GlyphFont, MenuTheme.TextPrimary);
        cross.Offset = new Vector3(
            MathF.Round((size - cross.Size.X) / 2f), MathF.Round((size - cross.Size.Y) / 2f), 0f);
        _removeWell.Visual.Children.InsertAtTop(cross);

        float labelLeft = MenuTheme.RowPadX + MenuTheme.ItemIconSize + MenuTheme.ItemIconGap;
        SpriteVisual label = canvas.Text(
            MenuCanvas.Fit(Title, MenuTheme.RowFont, _chooseLeft - labelLeft - MenuTheme.ItemActionGap),
            MenuTheme.RowFont,
            MenuTheme.TextPrimary);
        label.Offset = new Vector3(labelLeft, MenuCanvas.CenterY(Height, label.Size.Y), 0f);
        Root.Children.InsertAtTop(label);
    }

    /// <summary>Anywhere but the actions. A press on those is a click, not the start of a drag.</summary>
    public override bool CanDragFrom(float x, float y) => !_hovered || Hit(x) == Target.None;

    private Target Hit(float x)
    {
        if (x >= _removeLeft && x < _removeLeft + MenuTheme.RemoveButtonSize) return Target.Remove;
        if (_resetText is not null && x >= _resetLeft && x < _resetRight) return Target.ResetIcon;
        if (_chooseText is not null && x >= _chooseLeft && x < _chooseRight) return Target.ChooseIcon;
        return Target.None;
    }

    public override void OnHover(bool hovered)
    {
        if (_hovered == hovered || _actions is null) return;
        _hovered = hovered;

        if (!hovered) SetOver(Target.None);

        float target = hovered ? 1f : 0f;
        Compositor compositor = _actions.Compositor;

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, target, MenuTheme.EaseOut(compositor));
        fade.Duration = MenuTheme.RevealDuration;

        ContainerVisual actions = _actions;
        _reveal?.Run(fade, () => actions.Opacity = target);
    }

    public override void OnMove(float x, float y) => SetOver(_hovered ? Hit(x) : Target.None);

    private void SetOver(Target target)
    {
        if (_over == target) return;
        _over = target;

        // Only the remove button has a shape to tint. The two text actions are already accent
        // and grey against the row highlight, and giving them a hover state as well would be
        // three things lighting up under one pointer.
        if (_removeWell is not null)
            _removeWell.Color = target == Target.Remove ? MenuTheme.Danger : MenuTheme.SegmentWell;
    }

    public override void OnRelease(float x, float y, bool inside)
    {
        if (!inside || !_hovered) return;

        switch (Hit(x))
        {
            case Target.Remove: _items.Remove(_item); break;
            case Target.ResetIcon: _items.ResetIcon(_item); break;
            case Target.ChooseIcon: _items.ChooseIcon(_item); break;
        }
    }
}
