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

    public float PaddingX { get; init; } = 18f;
    public float PaddingY { get; init; } = 10f;
    public float CornerRadius { get; init; } = 20f;

    /// <summary>Gap between the pill and the bottom of the work area.</summary>
    public float ScreenMargin { get; init; } = 12f;

    /// <summary>Peak height of the launch bounce.</summary>
    public float BounceHeight { get; init; } = 30f;

    /// <summary>
    /// false: the pill is built at its widest possible size and never changes, so icons
    /// spread inside a container that holds still.
    /// true: the pill tracks the icon run, which is what macOS actually does.
    /// </summary>
    public bool PillGrowsWithIcons { get; init; }

    public float Cell => IconSize + IconGap;
    public float Influence => InfluenceCells * Cell;
    public float MaxGrow => MaxScale - 1f;
    public float PillHeight => IconSize + PaddingY * 2f;

    /// <summary>
    /// Headroom above the pill for a magnified, mid-bounce icon. The icon grows upward from
    /// its bottom edge, so only the extra height counts.
    /// </summary>
    public float TopOverflow => IconSize * MaxGrow + BounceHeight + 12f;

    /// <summary>Room around the pill for its drop shadow.</summary>
    public const float ShadowMargin = 28f;

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

    /// <summary>
    /// Pill width. In fixed mode this is the worst case, so magnified icons always have room
    /// and never spill past the rounded ends.
    /// </summary>
    public float PillWidth(int count) =>
        (PillGrowsWithIcons ? RestRunWidth(count) : MaxRunWidth(count)) + PaddingX * 2f;

    /// <summary>The pill is always painted at its widest; a nine-grid brush shrinks it if it animates.</summary>
    public float PillTextureWidth(int count) => MaxRunWidth(count) + PaddingX * 2f;
}
