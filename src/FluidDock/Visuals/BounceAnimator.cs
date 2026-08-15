using System.Numerics;
using Windows.UI.Composition;

namespace FluidDock.Visuals;

/// <summary>
/// The launch bounce.
///
/// The detail that matters is that the easing is asymmetric. A symmetric ease-in-out makes the
/// icon look like it is floating on a string; gravity means the rise decelerates and the fall
/// accelerates. Getting those two curves right is most of what separates this from every
/// "bouncing icon" that looks wrong.
/// </summary>
internal sealed class BounceAnimator
{
    private readonly Compositor _compositor;
    private readonly DockMetrics _metrics;
    private readonly ScalarKeyFrameAnimation _bounce;
    private readonly SpringScalarNaturalMotionAnimation _land;

    public BounceAnimator(Compositor compositor, DockMetrics metrics)
    {
        _compositor = compositor;
        _metrics = metrics;

        // Rising: fast off the mark, decelerating into the apex.
        CubicBezierEasingFunction up = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.20f, 0.60f), new Vector2(0.40f, 1.00f));

        // Falling: slow off the apex, accelerating into the landing.
        CubicBezierEasingFunction down = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.60f, 0.00f), new Vector2(0.80f, 0.40f));

        LinearEasingFunction hold = compositor.CreateLinearEasingFunction();

        _bounce = compositor.CreateScalarKeyFrameAnimation();
        _bounce.InsertKeyFrame(0.00f, 0f, hold);
        _bounce.InsertKeyFrame(0.40f, -metrics.BounceHeight, up);
        _bounce.InsertKeyFrame(0.80f, 0f, down);
        // A beat on the ground before the next hop, the way the macOS Dock paces it.
        _bounce.InsertKeyFrame(1.00f, 0f, hold);
        _bounce.Duration = TimeSpan.FromMilliseconds(700);
        _bounce.IterationBehavior = AnimationIterationBehavior.Forever;

        // Landing mid-air by simply stopping would cut the motion off. A spring to rest lets
        // the icon arrive from wherever it happens to be.
        _land = compositor.CreateSpringScalarAnimation();
        _land.FinalValue = 0f;
        _land.DampingRatio = 0.72f;
        _land.Period = TimeSpan.FromMilliseconds(55);
    }

    public void Start(Visual inner) => inner.StartAnimation("Offset.Y", _bounce);

    public void Stop(Visual inner) => StartAndRelease(inner, _land);

    /// <summary>A single hop, for feedback on things that do not start a process.</summary>
    public void Nudge(Visual inner)
    {
        ScalarKeyFrameAnimation once = _compositor.CreateScalarKeyFrameAnimation();
        once.InsertKeyFrame(0.45f, -_metrics.BounceHeight * 0.45f,
            _compositor.CreateCubicBezierEasingFunction(new Vector2(0.20f, 0.60f), new Vector2(0.40f, 1.00f)));
        once.InsertKeyFrame(1.00f, 0f,
            _compositor.CreateCubicBezierEasingFunction(new Vector2(0.60f, 0.00f), new Vector2(0.80f, 0.40f)));
        once.Duration = TimeSpan.FromMilliseconds(360);
        StartAndRelease(inner, once);
    }

    /// <summary>
    /// Runs a finite animation, then hands the property back to static control.
    ///
    /// Composition keeps requesting a per-frame tick for as long as a property is under
    /// animation control - settling on the final value is not the same as releasing the
    /// property. Left alone that is ~240 wake-ups a second on this display, forever, for an
    /// icon that is visibly standing still. Measured: 0ms of CPU per 10s idle before a bounce,
    /// 190ms per 10s after one. Expression animations do not have this problem because they
    /// are dependency-tracked and only re-evaluate when an input changes.
    /// </summary>
    private void StartAndRelease(Visual inner, CompositionAnimation animation)
    {
        CompositionScopedBatch batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        inner.StartAnimation("Offset.Y", animation);
        batch.End();

        batch.Completed += (_, _) =>
        {
            try
            {
                inner.StopAnimation("Offset.Y");
                inner.Offset = new Vector3(inner.Offset.X, 0f, inner.Offset.Z);
            }
            catch (ObjectDisposedException)
            {
                // A config reload closes the whole tree, and it can land between the batch
                // ending and this callback. The property went with the visual, so there is
                // nothing left to release.
            }
        };
    }
}
