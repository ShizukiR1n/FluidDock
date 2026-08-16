namespace FluidDock;

/// <summary>
/// Every number the dock's geometry depends on, plus the magnification curve itself.
///
/// The magnification math exists in two places: as ExpressionAnimation strings evaluated by
/// the compositor, and as C# used for click hit-testing. Both derive from this one type, so
/// they cannot drift apart. Nothing here may be duplicated as a literal anywhere else.
/// </summary>
internal sealed class DockMetrics
{
    /// <summary>Icon edge length at rest, in pixels.</summary>
    public float IconSize { get; init; } = 48f;

    /// <summary>Gap between icons. Stays constant during magnification; only icons grow.</summary>
    public float IconGap { get; init; } = 14f;

    /// <summary>How large the icon directly under the cursor gets. macOS ships roughly 2x.</summary>
    public float MaxScale { get; init; } = 2.0f;

    /// <summary>Magnification reach, measured in icon cells to either side.</summary>
    public float InfluenceCells { get; init; } = 2.5f;

    /// <summary>Gap between the icons and the bottom of the work area.</summary>
    public float ScreenMargin { get; init; } = 12f;

    /// <summary>Peak height of the launch bounce.</summary>
    public float BounceHeight { get; init; } = 30f;

    public float Cell => IconSize + IconGap;
    public float Influence => InfluenceCells * Cell;
    public float MaxGrow => MaxScale - 1f;

    /// <summary>
    /// Headroom above the icon row for a magnified, mid-bounce icon. The icon grows upward from
    /// its bottom edge, so only the extra height counts.
    /// </summary>
    public float TopOverflow => IconSize * MaxGrow + BounceHeight + 12f;

    /// <summary>
    /// Slack between the widest the icons can get and the edge of the window.
    ///
    /// The window never resizes - moving an HWND is UI-thread work and would drag the animation
    /// back onto the thread this whole design keeps it off - so it is built once at the size the
    /// dock reaches when fully magnified, plus this. What it buys now that there is no backplate
    /// to cast a shadow is room for the soft edges of an icon bitmap, and somewhere for a
    /// rounding error to land that is not the outermost icon's last column of pixels.
    /// </summary>
    public const float EdgeSlack = 28f;

    // The expression language has no Pi constant we can rely on, so the literal is shared here
    // rather than written twice.
    private const string PiLiteral = "3.14159265";
    private const float Pi = 3.14159265f;

    /// <summary>
    /// Raised cosine falloff over a normalised distance. Its first derivative is zero at
    /// d = +/-1, so an icon entering or leaving the influence radius does not visibly click
    /// into motion the way a linear or gaussian-clipped falloff does.
    /// </summary>
    public static float Falloff(float d)
    {
        d = Math.Clamp(d, -1f, 1f);
        return 0.5f * (1f + MathF.Cos(d * Pi));
    }

    /// <summary>The expression-language twin of <see cref="Falloff"/>.</summary>
    /// <param name="distanceExpr">An expression yielding the already-normalised distance.</param>
    public static string FalloffExpr(string distanceExpr) =>
        $"(0.5 * (1 + Cos(Clamp({distanceExpr}, -1, 1) * {PiLiteral})))";

    public float RestRunWidth(int count) =>
        count <= 0 ? 0f : count * IconSize + (count - 1) * IconGap;

    /// <summary>Rest centre of icon i, relative to the left edge of the icon run.</summary>
    public float RestCenter(int i) => i * Cell + IconSize / 2f;

    /// <summary>
    /// Widest the icon run can ever get. Found by sweeping the cursor rather than solving
    /// analytically: the sum of shifted cosines has no tidy closed-form maximum, and this
    /// runs once at startup.
    /// </summary>
    public float MaxRunWidth(int count)
    {
        if (count <= 0) return 0f;

        float rest = RestRunWidth(count);
        float widest = rest;

        for (float cursor = -Influence; cursor <= rest + Influence; cursor += 1f)
        {
            float icons = 0f;
            for (int i = 0; i < count; i++)
                icons += IconSize * (1f + MaxGrow * Falloff((RestCenter(i) - cursor) / Influence));

            float total = icons + IconGap * (count - 1);
            if (total > widest) widest = total;
        }

        return widest;
    }
}
