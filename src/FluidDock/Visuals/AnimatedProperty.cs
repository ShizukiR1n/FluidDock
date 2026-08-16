using Windows.UI.Composition;

namespace FluidDock.Visuals;

/// <summary>
/// One animatable Composition property, with the two rules that surround every finite animation
/// in this project already applied.
///
/// **Release.** Composition asks for a per-frame tick for as long as a property is under
/// animation control. Settling on the final value is not the same as letting go, so an animation
/// left attached costs ~240 wake-ups a second on this display, forever, for something visibly
/// standing still. It was measured at 190ms of CPU per 10 seconds idle after a single bounce.
/// Every animation here therefore runs inside a scoped batch and hands the property back when
/// the batch completes.
///
/// **Supersession.** StopAnimation reverts a property to whatever was last written to it
/// statically - not to where the animation finished - so the settle value has to be written by
/// hand. And a superseded animation still reports Completed, so without the generation counter
/// an old batch would arrive late and stop the animation that replaced it. That is not
/// hypothetical here: a toggle flicked twice quickly, or a hover highlight chasing the pointer
/// down a list, restarts the same property several times a second.
///
/// Expression animations need none of this - they are dependency-tracked and only re-evaluate
/// when an input changes - which is why the dock's magnification is built out of them and only
/// the discrete, one-shot motion goes through this type.
/// </summary>
internal sealed class AnimatedProperty
{
    private readonly Compositor _compositor;
    private readonly CompositionObject _target;
    private readonly string _property;

    private int _generation;
    private bool _dead;

    public AnimatedProperty(Compositor compositor, CompositionObject target, string property)
    {
        _compositor = compositor;
        _target = target;
        _property = property;
    }

    /// <summary>
    /// Runs a finite animation, then writes <paramref name="settle"/> and releases the property.
    /// The settle value must be where the animation ends, or the property will visibly jump back
    /// at the moment it is let go.
    /// </summary>
    public void Run(CompositionAnimation animation, Action settle)
    {
        if (_dead) return;

        int generation = ++_generation;

        CompositionScopedBatch batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _target.StartAnimation(_property, animation);
        batch.End();

        batch.Completed += (_, _) =>
        {
            if (_dead || generation != _generation) return;

            try
            {
                _target.StopAnimation(_property);
                settle();
            }
            catch (ObjectDisposedException)
            {
                // The tree was torn down between the batch ending and this callback. The
                // property went with the object that owned it, so there is nothing to release.
                _dead = true;
            }
        };
    }

    /// <summary>
    /// Abandons any in-flight completion, for use when the tree is about to be closed.
    /// Cheaper and more honest than letting each callback discover the disposal by exception.
    /// </summary>
    public void Retire() => _dead = true;
}
