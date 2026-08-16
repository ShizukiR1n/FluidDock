using System.Numerics;
using FluidDock.Visuals;
using Windows.UI;
using Windows.UI.Composition;

namespace FluidDock.Menu.Rows;

/// <summary>
/// A macOS switch.
///
/// The two things that make it read as Apple's rather than as a generic toggle are both motion:
/// the knob travels on a spring that overshoots by a pixel or so and settles, and the track
/// crossfades between grey and blue over a slightly longer beat than the knob takes to arrive.
/// Moving both on the same linear curve is what makes a switch look like a checkbox with extra
/// steps.
/// </summary>
internal sealed class ToggleRow : MenuRow
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;

    private bool _on;
    private RoundRect? _track;
    private RoundRect? _knob;
    private AnimatedProperty? _knobTravel;
    private AnimatedProperty? _trackTint;

    public ToggleRow(string title, Func<bool> get, Action<bool> set) : base(title)
    {
        _get = get;
        _set = set;
    }

    protected override void Build(MenuCanvas canvas)
    {
        _on = _get();

        SpriteVisual title = canvas.Text(Title, MenuTheme.RowFont, MenuTheme.TextPrimary);
        title.Offset = new Vector3(MenuTheme.RowPadX, MenuCanvas.CenterY(Height, title.Size.Y), 0f);
        Root!.Children.InsertAtTop(title);

        float width = MenuTheme.SwitchSize.X;
        float height = MenuTheme.SwitchSize.Y;

        _track = canvas.Rect(width, height, height / 2f, _on ? MenuTheme.Accent : MenuTheme.SwitchOff);
        _track.Offset = new Vector3(
            Width - MenuTheme.RowPadX - width, MenuCanvas.CenterY(Height, height), 0f);
        Root.Children.InsertAtTop(_track.Visual);

        float knob = MenuTheme.SwitchKnob;
        _knob = canvas.Rect(knob, knob, knob / 2f, MenuTheme.Knob);
        _knob.Offset = new Vector3(KnobX(_on), MenuTheme.SwitchInset, 0f);
        _track.Visual.Children.InsertAtTop(_knob.Visual);

        _knobTravel = canvas.Animate(_knob.Visual, "Offset.X");
        _trackTint = canvas.Animate(_track.Fill, "Color");
    }

    private static float KnobX(bool on) =>
        on ? MenuTheme.SwitchSize.X - MenuTheme.SwitchInset - MenuTheme.SwitchKnob : MenuTheme.SwitchInset;

    public override void OnRelease(float x, float y, bool inside)
    {
        if (!inside) return;
        Set(!_on);
        Changed?.Invoke();
    }

    private void Set(bool on)
    {
        if (_on == on || _track is null || _knob is null) return;
        _on = on;
        _set(on);

        float target = KnobX(on);
        _knobTravel?.Run(MenuTheme.Spring(_track.Visual.Compositor, target),
            () => _knob.Offset = new Vector3(target, MenuTheme.SwitchInset, 0f));

        Color tint = on ? MenuTheme.Accent : MenuTheme.SwitchOff;
        Compositor compositor = _track.Visual.Compositor;

        ColorKeyFrameAnimation fade = compositor.CreateColorKeyFrameAnimation();
        // Rgb, not the default. Interpolating a translucent white to an opaque blue through HSL
        // sweeps the hue the long way round and the track flashes green on its way to blue.
        fade.InterpolationColorSpace = CompositionColorSpace.Rgb;
        fade.InsertKeyFrame(1f, tint, MenuTheme.EaseOut(compositor));
        fade.Duration = MenuTheme.TintDuration;

        _trackTint?.Run(fade, () => _track.Color = tint);
    }

    public override void Refresh()
    {
        bool actual = _get();
        if (actual == _on || _track is null || _knob is null) return;

        // Straight to the new state, no animation: this is the config having changed underneath
        // the panel, not the user flicking the switch, and motion would imply they did something.
        _on = actual;
        _knob.Offset = new Vector3(KnobX(actual), MenuTheme.SwitchInset, 0f);
        _track.Color = actual ? MenuTheme.Accent : MenuTheme.SwitchOff;
    }
}
