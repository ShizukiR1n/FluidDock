using Windows.UI.Composition;

namespace FluidDock.Graphics;

/// <summary>
/// The Compositor and the D3D-backed surface factory behind it, owned once for the process.
///
/// Both are per-thread, not per-window: a Compositor belongs to the thread's DispatcherQueue, and
/// the surface factory holds a live D3D11 device that pins a GPU adapter awake for as long as it
/// exists. Giving the settings panel its own pair would mean a second D3D device on a machine
/// whose whole point was to keep the dock off the discrete GPU, so the windows share these and
/// own only what is genuinely theirs - their HWND and the DesktopWindowTarget bound to it.
///
/// The factory is created on first use rather than in the constructor. Standing up a D3D device
/// takes real time, and a session where the user never opens the panel should not pay for one.
/// In practice the dock asks for it during startup anyway; the laziness is there so that order
/// is a fact about the dock rather than a rule this type enforces.
/// </summary>
internal sealed class CompositionHost : IDisposable
{
    private SurfaceFactory? _surfaces;

    public Compositor Compositor { get; } = new();

    public SurfaceFactory Surfaces => _surfaces ??= new SurfaceFactory(Compositor);

    /// <summary>The GPU the rasteriser landed on, or null if nothing has needed one yet.</summary>
    public string? AdapterName => _surfaces?.AdapterName;

    /// <summary>
    /// Releases the surface factory, and deliberately not the Compositor.
    ///
    /// Closing a Compositor while any visual, brush or animation it produced is still alive is
    /// undefined, and at this point in shutdown the windows have been destroyed but their trees
    /// have only been handed to the collector. The process is about to end, so the one thing
    /// worth reclaiming by hand is the D3D device - it holds an adapter, which outlives a
    /// process's memory in a way that heap does not.
    /// </summary>
    public void Dispose()
    {
        _surfaces?.Dispose();
        _surfaces = null;
    }
}
