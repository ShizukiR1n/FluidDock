using System.Globalization;
using System.Numerics;
using Windows.UI.Composition;

namespace FluidDock.Menu.Rows;

/// <summary>
/// A continuous value: caption and readout on the first line, track and knob on the second.
///
/// The knob follows the pointer exactly, with no spring and no easing. That is a deliberate
/// exception to the rest of the panel - smoothing anything a finger is already holding is how a
/// control comes to feel like it is arguing with you. The motion budget goes to things that move
/// on their own.
///
/// The value is written back only when the drag ends. Saving continuously would be correct and
/// unusable: the dock watches its config file and rebuilds its entire visual tree on every
/// change, so a two-second drag would ask it to re-rasterise every icon a hundred times.
/// </summary>
internal sealed class SliderRow : MenuRow
{
    private readonly float _min;
    private readonly float _max;
    private readonly float _step;
    private readonly Func<float> _get;
    private readonly Action<float> _set;
    private readonly Func<float, string> _format;

    private float _value;
    private RoundRect? _fill;
    private RoundRect? _knob;
    private MutableText? _readout;

    public SliderRow(
        string title, float min, float max, float step,
        Func<float> get, Action<float> set, Func<float, string>? format = null)
        : base(title)
    {
        _min = min;
        _max = max;
        _step = step;
        _get = get;
        _set = set;
        _format = format ?? DefaultFormat(step);
    }

    public override float Height => MenuTheme.TallRowHeight;

    public override bool Draggable => true;

    /// <summary>Enough decimals to distinguish two adjacent steps, and not one more.</summary>
    private static Func<float, string> DefaultFormat(float step) =>
        step >= 1f
            ? v => v.ToString("0", CultureInfo.InvariantCulture)
            : v => v.ToString("0.0#", CultureInfo.InvariantCulture);

    private float TrackLeft => MenuTheme.RowPadX;
    private float TrackWidth => Width - MenuTheme.RowPadX * 2f;
    private float TravelLeft => TrackLeft + MenuTheme.SliderKnob / 2f;
    private float TravelWidth => TrackWidth - MenuTheme.SliderKnob;

    protected override void Build(MenuCanvas canvas)
    {
        _value = Snap(_get());

        const float TitleCenterY = 20f;
        const float TrackCenterY = 44f;

        SpriteVisual title = canvas.Text(Title, MenuTheme.RowFont, MenuTheme.TextPrimary);
        title.Offset = new Vector3(
            MenuTheme.RowPadX, MathF.Round(TitleCenterY - title.Size.Y / 2f), 0f);
        Root!.Children.InsertAtTop(title);

        _readout = canvas.Changing(MenuTheme.ValueFont, MenuTheme.TextSecondary, visual =>
            visual.Offset = new Vector3(
                Width - MenuTheme.RowPadX - visual.Size.X,
                MathF.Round(TitleCenterY - visual.Size.Y / 2f),
                0f));
        Root.Children.InsertAtTop(_readout.Visual);

        float trackTop = MathF.Round(TrackCenterY - MenuTheme.SliderTrackHeight / 2f);
        float radius = MenuTheme.SliderTrackHeight / 2f;

        RoundRect groove = canvas.Rect(TrackWidth, MenuTheme.SliderTrackHeight, radius, MenuTheme.SliderTrack);
        groove.Offset = new Vector3(TrackLeft, trackTop, 0f);
        Root.Children.InsertAtTop(groove.Visual);

        _fill = canvas.Rect(1f, MenuTheme.SliderTrackHeight, radius, MenuTheme.Accent);
        _fill.Offset = new Vector3(TrackLeft, trackTop, 0f);
        Root.Children.InsertAtTop(_fill.Visual);

        float knob = MenuTheme.SliderKnob;
        _knob = canvas.Rect(knob, knob, knob / 2f, MenuTheme.Knob);
        Root.Children.InsertAtTop(_knob.Visual);

        Place();
    }

    /// <summary>Moves the knob, the fill and the readout to wherever the value currently is.</summary>
    private void Place()
    {
        if (_knob is null || _fill is null) return;

        float t = _max > _min ? (_value - _min) / (_max - _min) : 0f;
        float centerX = TravelLeft + t * TravelWidth;
        float knob = MenuTheme.SliderKnob;

        _knob.Offset = new Vector3(MathF.Round(centerX - knob / 2f), MathF.Round(44f - knob / 2f), 0f);

        // The fill stops under the knob rather than at its centre, so no sliver of groove shows
        // through the gap between them at either end of the travel.
        _fill.Size = new Vector2(MathF.Max(centerX - TrackLeft, 0.1f), MenuTheme.SliderTrackHeight);

        _readout?.Set(_format(_value));
    }

    private float Snap(float value)
    {
        value = Math.Clamp(value, _min, _max);
        if (_step <= 0f) return value;
        return Math.Clamp(_min + MathF.Round((value - _min) / _step) * _step, _min, _max);
    }

    private void Track(float x)
    {
        float t = TravelWidth > 0f ? Math.Clamp((x - TravelLeft) / TravelWidth, 0f, 1f) : 0f;
        float next = Snap(_min + t * (_max - _min));
        if (next == _value) return;

        _value = next;
        Place();
    }

    public override void OnPress(float x, float y) => Track(x);

    public override void OnDrag(float x, float y) => Track(x);

    public override void OnRelease(float x, float y, bool inside)
    {
        // Not gated on `inside`. A drag that ends off the row is still a drag the user meant;
        // the pointer left because they threw the knob at the end of the track, not because they
        // changed their mind.
        _set(_value);
        Changed?.Invoke();
    }

    public override void Refresh()
    {
        float actual = Snap(_get());
        if (actual == _value) return;

        _value = actual;
        Place();
    }
}
