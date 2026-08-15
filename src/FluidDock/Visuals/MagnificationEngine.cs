using System.Globalization;
using System.Numerics;
using System.Text;
using Windows.UI.Composition;

namespace FluidDock.Visuals;

/// <summary>
/// The magnification, expressed entirely as ExpressionAnimations.
///
/// Nothing in here runs per frame on our thread. The UI thread's only job is to push a cursor
/// position into a property set on mouse-move; the compositor re-evaluates the whole layout
/// at display refresh rate on its own thread. That is the entire reason this project is built
/// on Composition rather than a UI framework: the animation cannot be stalled by our code.
///
/// The values are staged across three property sets, because an ExpressionAnimation may not
/// read a property from the object it is animating:
///
///   _input  : CursorX, Magnify        - written by the UI thread
///   _scales : S0..Sn-1                - one cosine each, reads _input
///   _layout : RunWidth, P0..Pn-1      - prefix sums, reads _scales
///
/// Staging keeps the cosine count at one per icon. Inlining the sums into each icon's offset
/// would make it O(n^2) cosines per frame for no gain.
/// </summary>
internal sealed class MagnificationEngine : IDisposable
{
    private readonly Compositor _compositor;
    private readonly DockMetrics _metrics;
    private readonly Layout _layout;
    private readonly int _count;

    private readonly CompositionPropertySet _input;
    private readonly CompositionPropertySet _scales;
    private readonly CompositionPropertySet _prefix;

    private readonly ScalarKeyFrameAnimation _magnifyIn;
    private readonly ScalarKeyFrameAnimation _magnifyOut;

    private bool _hovered;
    private bool _disposed;

    // Bumped on every magnify transition, so a settling animation cannot release a property a
    // newer one has since taken over. See StartAndRelease.
    private int _magnifyGeneration;

    public MagnificationEngine(Compositor compositor, DockMetrics metrics, Layout layout)
    {
        _compositor = compositor;
        _metrics = metrics;
        _layout = layout;
        _count = layout.Count;

        _input = compositor.CreatePropertySet();
        _input.InsertScalar("CursorX", layout.PillCenterX);
        _input.InsertScalar("Magnify", 0f);

        _scales = compositor.CreatePropertySet();
        _prefix = compositor.CreatePropertySet();

        BuildScales();
        BuildPrefixSums();

        _magnifyIn = compositor.CreateScalarKeyFrameAnimation();
        _magnifyIn.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f)));
        _magnifyIn.Duration = TimeSpan.FromMilliseconds(180);

        // Leaving is slower than arriving. Snapping back to rest reads as a glitch; easing out
        // reads as the dock settling.
        _magnifyOut = compositor.CreateScalarKeyFrameAnimation();
        _magnifyOut.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.33f, 0.0f), new Vector2(0.30f, 1.0f)));
        _magnifyOut.Duration = TimeSpan.FromMilliseconds(280);
    }

    /// <summary>Current magnification, for the CPU-side hit test.</summary>
    public float Magnify => _hovered ? 1f : 0f;

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// S_i = 1 + Magnify * MaxGrow * Falloff((restCentre_i - CursorX) / Influence)
    ///
    /// Distance is always measured from rest centres, never from the magnified ones. Feeding
    /// magnified positions back in would let the layout chase its own tail.
    /// </summary>
    private void BuildScales()
    {
        for (int i = 0; i < _count; i++)
        {
            _scales.InsertScalar($"S{i}", 1f);

            string falloff = DockMetrics.FalloffExpr("(C - D.CursorX) / R");
            ExpressionAnimation expression = _compositor.CreateExpressionAnimation($"1 + D.Magnify * G * {falloff}");
            expression.SetReferenceParameter("D", _input);
            expression.SetScalarParameter("C", _layout.RestCenterX(i));
            expression.SetScalarParameter("R", _metrics.Influence);
            expression.SetScalarParameter("G", _metrics.MaxGrow);

            _scales.StartAnimation($"S{i}", expression);
        }
    }

    /// <summary>
    /// RunWidth = IconSize * sum(S) + Gap * (n-1)
    /// P_i      = S_0 + ... + S_i-1
    /// </summary>
    private void BuildPrefixSums()
    {
        var sum = new StringBuilder();
        for (int i = 0; i < _count; i++)
        {
            _prefix.InsertScalar($"P{i}", i);

            if (i > 0)
            {
                ExpressionAnimation prefixExpression = _compositor.CreateExpressionAnimation(sum.ToString());
                prefixExpression.SetReferenceParameter("S", _scales);
                _prefix.StartAnimation($"P{i}", prefixExpression);
            }

            sum.Append(i > 0 ? " + " : string.Empty).Append("S.S").Append(i);
        }

        _prefix.InsertScalar("RunWidth", _metrics.RestRunWidth(_count));
        if (_count == 0) return;

        string runWidth = $"{F(_metrics.IconSize)} * ({sum}) + {F(_metrics.IconGap * (_count - 1))}";
        ExpressionAnimation runWidthExpression = _compositor.CreateExpressionAnimation(runWidth);
        runWidthExpression.SetReferenceParameter("S", _scales);
        _prefix.StartAnimation("RunWidth", runWidthExpression);
    }

    /// <summary>
    /// Binds one icon's two visuals. The split is load-bearing: Offset is a single Vector3
    /// property, so an expression driving X would own Y as well and the launch bounce would
    /// have nowhere to live. The outer visual is positioned; the inner one is scaled and bounced.
    /// </summary>
    public void Attach(int index, Visual outer, Visual inner)
    {
        // Left edge of the run, plus the widths of everything before this icon. The trailing
        // term converts a left edge into an offset, since Scale expands about CenterPoint.X.
        string offset =
            $"PC - L.RunWidth * 0.5 + W * L.P{index} + {F(_metrics.IconGap * index)} + W * (S.S{index} - 1) * 0.5";

        ExpressionAnimation offsetExpression = _compositor.CreateExpressionAnimation(offset);
        offsetExpression.SetReferenceParameter("L", _prefix);
        offsetExpression.SetReferenceParameter("S", _scales);
        offsetExpression.SetScalarParameter("PC", _layout.PillCenterX);
        offsetExpression.SetScalarParameter("W", _metrics.IconSize);
        outer.StartAnimation("Offset.X", offsetExpression);

        ExpressionAnimation scaleExpression =
            _compositor.CreateExpressionAnimation($"Vector3(S.S{index}, S.S{index}, 1)");
        scaleExpression.SetReferenceParameter("S", _scales);
        inner.StartAnimation("Scale", scaleExpression);
    }

    /// <summary>Drives the pill's own width from the same run width, when configured to grow.</summary>
    public void AttachPill(Visual pill)
    {
        string size = $"Vector2(L.RunWidth + {F(_metrics.PaddingX * 2f)}, {F(_metrics.PillHeight)})";
        ExpressionAnimation expression = _compositor.CreateExpressionAnimation(size);
        expression.SetReferenceParameter("L", _prefix);
        pill.StartAnimation("Size", expression);

        ExpressionAnimation offset =
            _compositor.CreateExpressionAnimation($"PC - (L.RunWidth + {F(_metrics.PaddingX * 2f)}) * 0.5");
        offset.SetReferenceParameter("L", _prefix);
        offset.SetScalarParameter("PC", _layout.PillCenterX);
        pill.StartAnimation("Offset.X", offset);
    }

    /// <summary>
    /// The single per-mouse-move write: one static value into one property set.
    ///
    /// This used to run the cursor through a spring to smooth out mouse-message coalescing, and
    /// that was a mistake twice over. The spring restarted on every move, so its velocity was
    /// reset to zero before it had converged and it trailed the cursor by a speed-proportional
    /// distance that never closed. Worse, releasing it needed a CompositionScopedBatch per move,
    /// and a batch cannot report Completed until the compositor commits a frame - which pinned
    /// input processing to one move per frame. Measured: of 2000 moves delivered at 1000Hz, the
    /// dock saw 481. Three quarters of the user's motion was being coalesced away while the
    /// thread sat 99% idle waiting on frame boundaries.
    ///
    /// A plain write has none of that. It is also not an animation, so it never puts the
    /// property under animation control and cannot leak an idle per-frame tick.
    /// </summary>
    public void SetCursor(float x) => _input.InsertScalar("CursorX", x);

    public void SetHovered(bool hovered)
    {
        if (_hovered == hovered) return;
        _hovered = hovered;
        StartAndRelease(hovered ? _magnifyIn : _magnifyOut, hovered ? 1f : 0f);
    }

    /// <summary>
    /// Runs the magnify transition, then hands the property back to static control.
    ///
    /// Settling on the final value is not the same as releasing the property: Composition keeps
    /// asking for a per-frame tick while any property is under animation control, so a dock the
    /// user merely hovered once went on costing ~240 wake-ups a second forever. Measured at
    /// 1.8% of a core, idle, indefinitely. The expression animations above do not have this
    /// problem - they are dependency-tracked and only re-evaluate when an input changes.
    ///
    /// This fires twice per hover, so the batch's frame-boundary cost does not matter here the
    /// way it did on the cursor path. The generation guard covers a fast in-out flick, where
    /// the superseded animation still reports Completed and would otherwise stop its successor.
    /// </summary>
    private void StartAndRelease(CompositionAnimation animation, float finalValue)
    {
        int generation = ++_magnifyGeneration;

        CompositionScopedBatch batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _input.StartAnimation("Magnify", animation);
        batch.End();

        batch.Completed += (_, _) =>
        {
            // A config reload disposes the engine and builds a fresh one, which can happen
            // between the batch ending and this callback - by then _input is closed.
            if (_disposed || _magnifyGeneration != generation) return;
            _input.StopAnimation("Magnify");
            _input.InsertScalar("Magnify", finalValue);
        };
    }

    public void Dispose()
    {
        _disposed = true;
        _input.Dispose();
        _scales.Dispose();
        _prefix.Dispose();
        _magnifyIn.Dispose();
        _magnifyOut.Dispose();
    }
}
