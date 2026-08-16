using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;
using GdiColor = System.Drawing.Color;
using GdiFont = System.Drawing.Font;
using GdiFontStyle = System.Drawing.FontStyle;

namespace FluidDock.Menu;

/// <summary>
/// Every number and colour the settings panel is made of, in one place.
///
/// This is the file to edit to restyle the panel, and nothing below it may hard-code a colour,
/// a padding or a duration. That is not tidiness for its own sake: the whole point of the row
/// types being interchangeable is that a new one written six months from now lands looking like
/// it was always there, and it only does that if it had no choice about where its constants
/// came from.
///
/// The palette is macOS dark mode - Apple's system greys and the #0A84FF dark-mode accent -
/// because the dock it configures is already aiming there and a panel in Windows' own idiom
/// would read as a different program's window.
/// </summary>
internal static class MenuTheme
{
    // ---- Layout -----------------------------------------------------------------------------

    /// <summary>Panel width. Everything else lays out inside this, so it is the one free choice.</summary>
    public const float PanelWidth = 344f;

    /// <summary>Tallest the scrolling area is allowed to get before it starts scrolling instead.</summary>
    public const float MaxViewportHeight = 560f;

    public const float PanelRadius = 16f;

    /// <summary>Room around the panel for its drop shadow. The window is this much bigger on each side.</summary>
    public const float ShadowMargin = 34f;

    /// <summary>Horizontal inset from the panel edge to the cards.</summary>
    public const float PanelPadX = 14f;

    /// <summary>The title bar: app name, version, close button.</summary>
    public const float HeaderHeight = 54f;

    public const float FooterHeight = 12f;

    /// <summary>Grey caption above each card.</summary>
    public const float SectionHeaderHeight = 28f;

    /// <summary>Gap below one card before the next section's caption.</summary>
    public const float SectionGap = 14f;

    public const float CardRadius = 10f;

    /// <summary>Inset from the card's left edge to a row's text, and to the start of a separator.</summary>
    public const float RowPadX = 14f;

    public const float RowHeight = 44f;

    /// <summary>Rows that stack a control under their title need the extra line.</summary>
    public const float TallRowHeight = 64f;

    /// <summary>Corner radius of the hover highlight, and of a pressed row.</summary>
    public const float HighlightRadius = 8f;

    /// <summary>How far the highlight is inset from the card edges, so it reads as inside it.</summary>
    public const float HighlightInset = 3f;

    // ---- Controls ---------------------------------------------------------------------------

    public static readonly Vector2 SwitchSize = new(40f, 24f);
    public const float SwitchKnob = 20f;
    public const float SwitchInset = 2f;

    public const float SliderTrackHeight = 4f;
    public const float SliderKnob = 16f;

    public const float SegmentHeight = 26f;
    public const float SegmentRadius = 7f;
    public const float SegmentPadX = 10f;

    public const float CloseButtonSize = 22f;

    // ---- The dock's item list ---------------------------------------------------------------

    /// <summary>Thumbnail edge in the item list. Small enough that the list is a list, not a shelf.</summary>
    public const float ItemIconSize = 26f;

    /// <summary>Gap between a thumbnail and its label.</summary>
    public const float ItemIconGap = 12f;

    /// <summary>Gap between the per-item actions revealed on hover.</summary>
    public const float ItemActionGap = 12f;

    public const float RemoveButtonSize = 20f;

    /// <summary>Shortest gap the eye reads as a fade rather than as a flicker.</summary>
    public static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// How far the pointer may wander before a press on an item becomes a drag rather than a
    /// click. Small enough not to feel sticky, large enough that a click delivered by a hand
    /// resting on the mouse is not one.
    /// </summary>
    public const float DragThreshold = 4f;

    /// <summary>How much a row grows while it is being dragged. Felt more than seen, as usual.</summary>
    public const float DragLift = 1.03f;

    /// <summary>
    /// Breathing room below the scrolling area, and only when it actually scrolls. Without it the
    /// clip lands exactly on the panel's bottom border and a half-visible row looks like the
    /// border cut it off rather than like there is more to see.
    /// </summary>
    public const float ScrollPad = 10f;

    /// <summary>An overlay scroll indicator, macOS-style: no track, no buttons, no reserved width.</summary>
    public const float ScrollBarWidth = 3f;
    public const float ScrollBarInset = 5f;
    public const float ScrollBarMinLength = 28f;

    // ---- Colours ----------------------------------------------------------------------------

    /// <summary>Panel fill. Alpha carries the translucency; there is no backdrop blur behind it.</summary>
    public static readonly GdiColor PanelTint = GdiColor.FromArgb(238, 28, 28, 30);
    public const float PanelBorder = 0.14f;
    public const float PanelTopHighlight = 0.22f;
    public const float PanelNoise = 0.022f;
    public const float PanelShadowOpacity = 0.62f;
    public const float PanelShadowBlur = 44f;
    public const float PanelShadowOffsetY = 12f;

    public static readonly Color CardFill = Rgba(255, 255, 255, 0.055f);
    public static readonly Color CardBorder = Rgba(255, 255, 255, 0.075f);
    public static readonly Color Separator = Rgba(255, 255, 255, 0.085f);

    public static readonly Color RowHover = Rgba(255, 255, 255, 0.07f);
    public static readonly Color RowPressed = Rgba(255, 255, 255, 0.13f);
    public static readonly Color Transparent = Rgba(255, 255, 255, 0f);

    /// <summary>macOS dark-mode accent. The light-mode #007AFF is too dark to read on this panel.</summary>
    public static readonly Color Accent = Rgb(10, 132, 255);
    public static readonly Color Danger = Rgb(255, 69, 58);

    public static readonly Color SwitchOff = Rgba(255, 255, 255, 0.18f);
    public static readonly Color Knob = Rgb(255, 255, 255);
    public static readonly Color SliderTrack = Rgba(255, 255, 255, 0.16f);
    public static readonly Color SegmentWell = Rgba(255, 255, 255, 0.07f);
    public static readonly Color SegmentSelected = Rgba(255, 255, 255, 0.20f);
    public static readonly Color ScrollThumb = Rgba(255, 255, 255, 0.30f);

    public static readonly GdiColor TextPrimary = GdiColor.FromArgb(245, 245, 247);
    public static readonly GdiColor TextSecondary = GdiColor.FromArgb(152, 152, 157);
    public static readonly GdiColor TextHeader = GdiColor.FromArgb(142, 142, 147);
    public static readonly GdiColor TextAccent = GdiColor.FromArgb(10, 132, 255);
    public static readonly GdiColor TextDanger = GdiColor.FromArgb(255, 69, 58);

    // ---- Type -------------------------------------------------------------------------------

    /// <summary>
    /// One family for the whole panel. Segoe UI is the closest Windows ships to SF Pro; the
    /// Chinese labels come through GDI+'s font linking rather than a second family, which keeps
    /// the Latin text from switching metrics halfway along a mixed string.
    /// </summary>
    private const string Family = "Segoe UI";

    private const string SemiboldFamily = "Segoe UI Semibold";

    public static readonly GdiFont TitleFont = Pixels(SemiboldFamily, 15.5f);
    public static readonly GdiFont SubtitleFont = Pixels(Family, 11.5f);
    public static readonly GdiFont SectionFont = Pixels(SemiboldFamily, 11f);
    public static readonly GdiFont RowFont = Pixels(Family, 13.5f);
    public static readonly GdiFont ValueFont = Pixels(Family, 12.5f);
    public static readonly GdiFont SegmentFont = Pixels(SemiboldFamily, 11.5f);
    public static readonly GdiFont GlyphFont = Pixels(Family, 12f);

    /// <summary>
    /// Sizes in pixels, not points. Points would be scaled by the bitmap's DPI, which is 96 here
    /// and would silently become something else the day this is rendered at a different scale -
    /// a bug that shows up as a layout that is right on one machine and wrong on the next.
    /// </summary>
    private static GdiFont Pixels(string family, float size)
    {
        try
        {
            return new GdiFont(family, size, GdiFontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        }
        catch
        {
            // A missing family throws rather than substituting. Segoe UI Semibold in particular
            // is absent from some stripped Windows images.
            return new GdiFont(System.Drawing.FontFamily.GenericSansSerif, size, GdiFontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        }
    }

    // ---- Motion -----------------------------------------------------------------------------

    /// <summary>
    /// The panel's one spring. Barely under-damped, and fast enough that the overshoot is felt
    /// rather than seen - which is the difference between Apple's motion and a bouncy imitation
    /// of it. Used for anything that moves from one resting place to another: the hover
    /// highlight, a switch knob, a segmented control's selection.
    /// </summary>
    public const float SpringDamping = 0.86f;
    public static readonly TimeSpan SpringPeriod = TimeSpan.FromMilliseconds(34);

    public static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(130);
    public static readonly TimeSpan TintDuration = TimeSpan.FromMilliseconds(160);

    /// <summary>Decelerating hard into the target. The dock's magnify-in uses the same curve.</summary>
    public static CubicBezierEasingFunction EaseOut(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f));

    public static CubicBezierEasingFunction EaseIn(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.40f, 0.0f), new Vector2(0.90f, 0.35f));

    public static SpringScalarNaturalMotionAnimation Spring(Compositor compositor, float finalValue)
    {
        SpringScalarNaturalMotionAnimation spring = compositor.CreateSpringScalarAnimation();
        spring.FinalValue = finalValue;
        spring.DampingRatio = SpringDamping;
        spring.Period = SpringPeriod;
        return spring;
    }

    public static SpringVector2NaturalMotionAnimation Spring(Compositor compositor, Vector2 finalValue)
    {
        SpringVector2NaturalMotionAnimation spring = compositor.CreateSpringVector2Animation();
        spring.FinalValue = finalValue;
        spring.DampingRatio = SpringDamping;
        spring.Period = SpringPeriod;
        return spring;
    }

    public static SpringVector3NaturalMotionAnimation Spring(Compositor compositor, Vector3 finalValue)
    {
        SpringVector3NaturalMotionAnimation spring = compositor.CreateSpringVector3Animation();
        spring.FinalValue = finalValue;
        spring.DampingRatio = SpringDamping;
        spring.Period = SpringPeriod;
        return spring;
    }

    public static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    public static Color Rgba(byte r, byte g, byte b, float alpha) =>
        Color.FromArgb((byte)MathF.Round(Math.Clamp(alpha, 0f, 1f) * 255f), r, g, b);
}
