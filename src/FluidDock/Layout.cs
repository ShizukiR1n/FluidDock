namespace FluidDock;

/// <summary>
/// The magnification layout, solved on the CPU.
///
/// The compositor already computes this every frame from ExpressionAnimations; this exists
/// only so a click can be attributed to an icon. It runs on mouse-up, not per frame.
/// Both paths are generated from <see cref="DockMetrics"/>, so they agree by construction.
/// </summary>
internal sealed class Layout
{
    private readonly DockMetrics _metrics;
    private readonly float[] _scales;
    private readonly float[] _lefts;

    public int Count { get; }

    /// <summary>Window-space X of the centre the icon run is balanced around.</summary>
    public float PillCenterX { get; }

    public Layout(DockMetrics metrics, int count, float pillCenterX)
    {
        _metrics = metrics;
        Count = count;
        PillCenterX = pillCenterX;
        _scales = new float[count];
        _lefts = new float[count];
    }

    /// <summary>Window-space X of icon i's centre when nothing is magnified.</summary>
    public float RestCenterX(int i) =>
        PillCenterX - _metrics.RestRunWidth(Count) / 2f + _metrics.RestCenter(i);

    /// <summary>
    /// Reproduces the compositor's layout for one cursor position and returns the icon under
    /// the cursor, or -1.
    /// </summary>
    public int HitTest(float cursorX, float magnify)
    {
        Solve(cursorX, magnify);

        for (int i = 0; i < Count; i++)
        {
            float width = _metrics.IconSize * _scales[i];
            if (cursorX >= _lefts[i] && cursorX < _lefts[i] + width)
                return i;
        }

        return -1;
    }

    /// <summary>Fills the scale and left-edge arrays for a cursor position.</summary>
    private void Solve(float cursorX, float magnify)
    {
        float runWidth = _metrics.IconGap * (Count - 1);

        for (int i = 0; i < Count; i++)
        {
            // Distance is measured from rest centres, never from the magnified ones. Feeding
            // magnified positions back in would make the layout chase itself.
            float d = (RestCenterX(i) - cursorX) / _metrics.Influence;
            _scales[i] = 1f + magnify * _metrics.MaxGrow * DockMetrics.Falloff(d);
            runWidth += _metrics.IconSize * _scales[i];
        }

        float x = PillCenterX - runWidth / 2f;
        for (int i = 0; i < Count; i++)
        {
            _lefts[i] = x;
            x += _metrics.IconSize * _scales[i] + _metrics.IconGap;
        }
    }
}
