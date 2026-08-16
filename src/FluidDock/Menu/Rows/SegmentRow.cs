using System.Numerics;
using FluidDock.Visuals;
using Windows.UI.Composition;

namespace FluidDock.Menu.Rows;

/// <summary>
/// A small set of mutually exclusive choices, drawn as macOS's segmented control: a recessed
/// well with a raised tile that slides to whichever option is picked.
///
/// The sliding tile is the whole point. Redrawing the selection in place - lighting up the new
/// segment and dimming the old - is a state change; moving it is a gesture, and it tells the eye
/// which option was left as well as which was taken.
/// </summary>
internal sealed class SegmentRow : MenuRow
{
    private readonly string[] _options;
    private readonly Func<int> _get;
    private readonly Action<int> _set;

    private int _index;
    private float _segmentWidth;
    private RoundRect? _selection;
    private AnimatedProperty? _travel;

    public SegmentRow(string title, string[] options, Func<int> get, Action<int> set) : base(title)
    {
        _options = options;
        _get = get;
        _set = set;
    }

    private float WellLeft => Width - MenuTheme.RowPadX - _segmentWidth * _options.Length;

    protected override void Build(MenuCanvas canvas)
    {
        _index = Math.Clamp(_get(), 0, _options.Length - 1);

        SpriteVisual title = canvas.Text(Title, MenuTheme.RowFont, MenuTheme.TextPrimary);
        title.Offset = new Vector3(MenuTheme.RowPadX, MenuCanvas.CenterY(Height, title.Size.Y), 0f);
        Root!.Children.InsertAtTop(title);

        // Every segment is as wide as the widest label needs, so the control is symmetrical and
        // the selection travels a constant distance per step.
        SpriteVisual[] labels = _options
            .Select(option => canvas.Text(option, MenuTheme.SegmentFont, MenuTheme.TextPrimary))
            .ToArray();

        float widest = labels.Length == 0 ? 0f : labels.Max(label => label.Size.X);
        _segmentWidth = MathF.Ceiling(widest + MenuTheme.SegmentPadX * 2f);

        // If the caption and the control cannot both fit, the control gives ground - a clipped
        // label is unreadable, whereas a tighter segment is merely tighter.
        float available = Width - MenuTheme.RowPadX * 2f - title.Size.X - 12f;
        if (_segmentWidth * _options.Length > available && _options.Length > 0)
            _segmentWidth = MathF.Floor(available / _options.Length);

        float wellWidth = _segmentWidth * _options.Length;
        RoundRect well = canvas.Rect(
            wellWidth, MenuTheme.SegmentHeight, MenuTheme.SegmentRadius, MenuTheme.SegmentWell);
        well.Offset = new Vector3(
            WellLeft, MenuCanvas.CenterY(Height, MenuTheme.SegmentHeight), 0f);
        Root.Children.InsertAtTop(well.Visual);

        const float Inset = 2f;
        _selection = canvas.Rect(
            _segmentWidth - Inset * 2f, MenuTheme.SegmentHeight - Inset * 2f,
            MenuTheme.SegmentRadius - 1f, MenuTheme.SegmentSelected);
        _selection.Offset = new Vector3(SelectionX(_index), Inset, 0f);
        well.Visual.Children.InsertAtTop(_selection.Visual);

        for (int i = 0; i < labels.Length; i++)
        {
            labels[i].Offset = new Vector3(
                MathF.Round(i * _segmentWidth + (_segmentWidth - labels[i].Size.X) / 2f),
                MenuCanvas.CenterY(MenuTheme.SegmentHeight, labels[i].Size.Y),
                0f);
            well.Visual.Children.InsertAtTop(labels[i]);
        }

        _travel = canvas.Animate(_selection.Visual, "Offset.X");
    }

    private float SelectionX(int index) => index * _segmentWidth + 2f;

    public override void OnRelease(float x, float y, bool inside)
    {
        if (!inside || _segmentWidth <= 0f) return;

        int picked = (int)MathF.Floor((x - WellLeft) / _segmentWidth);
        if (picked < 0 || picked >= _options.Length || picked == _index) return;

        Select(picked);
        Changed?.Invoke();
    }

    private void Select(int index)
    {
        if (_selection is null) return;

        _index = index;
        _set(index);

        float target = SelectionX(index);
        _travel?.Run(
            MenuTheme.Spring(_selection.Visual.Compositor, target),
            () => _selection.Offset = new Vector3(target, 2f, 0f));
    }

    public override void Refresh()
    {
        int actual = Math.Clamp(_get(), 0, _options.Length - 1);
        if (actual == _index || _selection is null) return;

        _index = actual;
        _selection.Offset = new Vector3(SelectionX(actual), 2f, 0f);
    }
}
