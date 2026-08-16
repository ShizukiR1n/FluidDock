using System.Numerics;
using System.Runtime.InteropServices;
using FluidDock.Graphics;
using FluidDock.Native;
using FluidDock.Visuals;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace FluidDock;

/// <summary>
/// The dock window: one borderless, click-through-where-empty, always-on-top HWND hosting a
/// Composition tree.
///
/// The window is created at the size the dock needs when fully magnified and never resizes.
/// That is deliberate - moving or sizing an HWND is a UI-thread operation, so a dock that
/// grew its window per frame would drag the animation back onto the thread we are trying to
/// keep out of the loop.
/// </summary>
internal sealed class DockWindow : IDisposable
{
    private const string ClassName = "FluidDockWindow";

    public const int HotkeyStallTest = 2;
    public const int HotkeyBounceTest = 3;

    private static readonly IntPtr TimerShrinkRegion = new(1);
    private static readonly IntPtr TimerRejoinDesktop = new(2);

    /// <summary>
    /// How long the dock stays at full size after the last thing that needed the room ended.
    ///
    /// Has to outlast the longest tail: the 280ms magnify-out, and the landing spring a bounce
    /// runs when it is cut short. Overshooting costs nothing - the window is only oversized
    /// against a desktop the user is not currently dragging a selection across.
    /// </summary>
    private const uint RegionSettleMs = 500;

    /// <summary>
    /// How long to keep looking for a desktop to sink into, and how often. Ten seconds is
    /// generous - the shell is usually ready within one or two - but giving up early costs the
    /// user a dock that floats over their windows for the rest of the session, whereas waiting
    /// costs a few seconds during which the dock is on screen and working regardless.
    /// </summary>
    private const uint RejoinIntervalMs = 500;
    private const int RejoinAttempts = 20;

    private readonly CompositionHost _host;
    private readonly string _configPath;

    private WndProc? _wndProc;
    private IntPtr _hwnd;

    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private SurfaceFactory? _surfaces;

    private DockConfig _config = new();
    private DockMetrics _metrics = new();
    private MagnificationEngine? _magnification;
    private BounceAnimator? _bounce;
    private Layout? _layout;

    private readonly List<Visual> _iconOuter = [];
    private readonly List<Visual> _iconInner = [];

    // Every Composition object the current tree owns, in creation order. See ReleaseTree.
    private readonly List<IDisposable> _treeResources = [];

    // Bumped on every rebuild, so work that outlives the tree it was started against - a launch
    // bounce waiting on a process that has not shown a window yet - can tell that it is stale.
    private int _treeGeneration;

    private FileSystemWatcher? _watcher;
    private Timer? _reloadDebounce;

    /// <summary>The desktop window we are parented to, or Zero when floating as a normal window.</summary>
    private IntPtr _desktop;

    // Held in a field, not just passed to SetWinEventHook: the callback is invoked from native
    // code, so nothing else keeps the delegate alive and it would be collected out from under
    // the hook.
    private Win32.WinEventProc? _foregroundHook;
    private IntPtr _foregroundHookHandle;

    // Client-space geometry, recomputed on every rebuild. The run widths are cached rather than
    // recomputed on demand because MaxRunWidth solves by sweeping the cursor across the dock -
    // fine once at startup, not fine inside a hit test that runs on every mouse message.
    private float _centerX;
    private float _iconBottom;
    private float _hotTop;
    private float _restRunWidth;
    private float _maxRunWidth;

    private bool _hovered;
    private bool _tracking;
    private bool _testBouncing;

    /// <summary>
    /// Whether the user wants the dock on screen. Not the same as the window's actual state:
    /// the window is shown and hidden in several places, and a rebuild - or a whole new window
    /// after Explorer restarts - has to put back what the user chose rather than what the last
    /// SetWindowPos happened to leave behind.
    /// </summary>
    private bool _visible = true;

    /// <summary>Rejoin attempts left before settling for whatever desktop window exists.</summary>
    private int _rejoinLeft;

    // Window size in client pixels, kept so the idle region can be clamped to it.
    private int _windowW;
    private int _windowH;

    // How many icons are mid-launch. A bounce outlives the hover that started it - it runs until
    // the launched process shows a window, up to five seconds - so the hover flag alone cannot
    // say whether the dock still needs room to draw in.
    private int _bounceCount;

    private bool _regionShrunk;

    // Diagnostics. An idle dock should deliver essentially no messages; if it ever burns CPU
    // while nothing is happening, this says which message is responsible instead of leaving it
    // to guesswork. Costs one dictionary bump per message.
    private readonly Dictionary<uint, int> _messageCounts = [];
    private readonly DateTime _started = DateTime.Now;

    // Mouse-move cost. This path has to stay cheap: Windows coalesces the moves that pile up
    // behind a slow handler, so expense here does not merely add latency, it discards input.
    // Measured at 33.5us with a 1000Hz mouse, which delivers every sample the hardware sends.
    private long _moveTicks;
    private int _moveCount;

    public IntPtr Handle => _hwnd;

    /// <summary>The GPU the icon rasteriser landed on. Worth logging: see SurfaceFactory.</summary>
    public string AdapterName => _surfaces?.AdapterName ?? "(none)";

    public DockWindow(CompositionHost host, string configPath)
    {
        _host = host;
        _configPath = configPath;
    }

    public void Create()
    {
        _config = DockConfig.Load(_configPath);
        _metrics = _config.Metrics.ToMetrics();

        IntPtr hInstance = Win32.GetModuleHandleW(null);
        _wndProc = WindowProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = Win32.LoadCursorW(IntPtr.Zero, Win32.IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName,
        };

        if (Win32.RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

        OpenWindow(hInstance);

        StartWatchingConfig();
        WatchForeground();
    }

    /// <summary>
    /// Builds a fresh window and visual tree after Explorer took the old one down with it.
    ///
    /// The dock is a child of Progman, so restarting Explorer destroys it and leaves the process
    /// alive with nothing on screen and no way to draw. Everything not bound to the HWND is kept
    /// deliberately: the Compositor, the surface factory and the D3D device behind it, the config
    /// watcher and the foreground hook all outlive the window, and tearing them down would cost a
    /// GPU device reset to solve a problem they do not have. Only the HWND and the
    /// DesktopWindowTarget bound to it have to be replaced.
    ///
    /// The window class is not re-registered - it belongs to the process, not the window, and
    /// survives DestroyWindow.
    /// </summary>
    public void Recreate()
    {
        ReleaseTree();

        _target?.Dispose();
        _target = null;

        // Normally already gone, taken out from under us. Handled anyway so that calling this
        // when the window is still alive is a rebuild rather than a leak.
        if (_hwnd != IntPtr.Zero && Win32.IsWindow(_hwnd)) Win32.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _desktop = IntPtr.Zero;
        _hovered = false;
        _tracking = false;
        _layout = null;

        OpenWindow(Win32.GetModuleHandleW(null));
    }

    /// <summary>Creates the HWND, binds a composition target to it, and puts the dock in it.</summary>
    private void OpenWindow(IntPtr hInstance)
    {
        // WS_EX_NOREDIRECTIONBITMAP: no GDI redirection surface, so the window has real
        // per-pixel alpha and Composition is the only thing painting it.
        uint exStyle = Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_NOREDIRECTIONBITMAP;
        if (_config.Layer == DockLayer.Top) exStyle |= Win32.WS_EX_TOPMOST;

        _hwnd = Win32.CreateWindowExW(
            exStyle,
            ClassName, "FluidDock", Win32.WS_POPUP,
            0, 0, 100, 100,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        // Borrowed, not owned. The settings panel draws from the same pair, and both outlive any
        // one window - which is what makes an Explorer restart a matter of rebuilding an HWND
        // rather than resetting a GPU device.
        _compositor = _host.Compositor;
        _target = CompositorInterop.CreateDesktopWindowTarget(_compositor, _hwnd, _config.Layer == DockLayer.Top);
        _surfaces = _host.Surfaces;

        // Reparent only after the composition target exists. The target binds to the HWND, and
        // creating one against a window that is already a child is the part with no guarantee
        // behind it - this way the risky ordering is avoided entirely.
        _rejoinLeft = RejoinAttempts;
        if (_config.Layer == DockLayer.Desktop) JoinDesktop();

        Rebuild();

        Win32.ShowWindow(_hwnd, _visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
        Win32.RegisterHotKey(_hwnd, HotkeyStallTest, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, Win32.VK_S);
        Win32.RegisterHotKey(_hwnd, HotkeyBounceTest, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, Win32.VK_B);
    }

    /// <summary>
    /// Keeps the dock above the desktop without making it topmost.
    ///
    /// Clicking the desktop activates Progman, and activation raises it - over an ordinary
    /// window like ours. So the dock vanished the moment the user clicked the wallpaper and
    /// stayed gone, which is the one place it is supposed to be. Watching for the desktop
    /// taking the foreground and lifting ourselves back over it fixes that without reaching for
    /// WS_EX_TOPMOST, which would put us over the user's applications again.
    ///
    /// Only foreground changes fire this, so it costs nothing while the user works.
    /// </summary>
    private void WatchForeground()
    {
        // Idempotent, because the layer is settable at runtime and this has to be able to run
        // again after a change - installing a second hook rather than replacing the first would
        // leave the dock raising itself twice per desktop click, forever.
        if (_foregroundHookHandle != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_foregroundHookHandle);
            _foregroundHookHandle = IntPtr.Zero;
            _foregroundHook = null;
        }

        if (_config.Layer != DockLayer.Normal) return;

        _foregroundHook = (_, _, hwnd, idObject, _, _, _) =>
        {
            // idObject == OBJID_WINDOW. The event also fires for accessibility child objects,
            // which are not window activations and would have us re-raising constantly.
            if (idObject != 0 || !IsDesktopWindow(hwnd)) return;

            Win32.SetWindowPos(_hwnd, Win32.HWND_TOP, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        };

        _foregroundHookHandle = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundHook, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
    }

    private static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        var name = new System.Text.StringBuilder(64);
        Win32.GetClassNameW(hwnd, name, name.Capacity);
        string className = name.ToString();

        return className is "Progman" or "WorkerW";
    }

    /// <summary>Tears down and rebuilds the visual tree. Used at startup and on config reload.</summary>
    private void Rebuild()
    {
        if (_compositor is null || _target is null || _surfaces is null) return;

        ReleaseTree();

        int count = _config.Items.Count;

        _restRunWidth = _metrics.RestRunWidth(count);
        _maxRunWidth = _metrics.MaxRunWidth(count);

        int windowW = (int)MathF.Ceiling(_maxRunWidth + DockMetrics.EdgeSlack * 2f);
        int windowH = (int)MathF.Ceiling(
            _metrics.TopOverflow + _metrics.IconSize + DockMetrics.EdgeSlack);
        (_windowW, _windowH) = (windowW, windowH);

        // The region is in client coordinates, so a resize would leave a stale shape clipping the
        // new tree. Drop it and let UpdateRegion decide from scratch below.
        Win32.SetWindowRgn(_hwnd, IntPtr.Zero, false);
        _regionShrunk = false;
        _bounceCount = 0;

        _centerX = windowW / 2f;
        _iconBottom = windowH - DockMetrics.EdgeSlack;
        _hotTop = _iconBottom - _metrics.IconSize - _metrics.TopOverflow;

        PositionWindow(windowW, windowH);

        ContainerVisual root = Own(_compositor.CreateContainerVisual());
        root.Size = new Vector2(windowW, windowH);
        _target.Root = root;

        _layout = new Layout(_metrics, count, _centerX);
        _magnification = new MagnificationEngine(_compositor, _metrics, _layout);
        _bounce ??= new BounceAnimator(_compositor, _metrics);

        BuildIcons(root, count);
        ResyncHover();
        UpdateRegion();
    }

    /// <summary>Hands a Composition object to the tree, to be closed at the next rebuild.</summary>
    private T Own<T>(T resource) where T : IDisposable
    {
        _treeResources.Add(resource);
        return resource;
    }

    /// <summary>
    /// Closes everything the previous tree owned.
    ///
    /// Composition objects are not ordinary managed garbage. A CompositionDrawingSurface is a
    /// few dozen bytes of wrapper in front of a GPU allocation the collector cannot see, so a
    /// rebuild that only replaced _target.Root put no pressure on the heap and freed nothing:
    /// the old surfaces, brushes and visuals stayed live until something else happened to
    /// trigger a collection. Measured over a 40-minute session with a handful of config edits,
    /// 47.3MB private / 1305 handles at start became 85.3MB / 1663 - and a 60-cycle hover test
    /// moved neither number, which is what pinned it on the rebuild path.
    ///
    /// Released newest first, so a brush is always closed before the surface it draws from and
    /// a child visual before its parent.
    /// </summary>
    private void ReleaseTree()
    {
        _magnification?.Dispose();
        _magnification = null;

        // Detach first: the target holds its own reference, and closing the root while it is
        // still attached would leave the target pointing at a dead visual until the next
        // assignment. Both happen inside one message, so the compositor commits them together
        // and nothing blank is ever presented.
        if (_target is not null) _target.Root = null;

        for (int i = _treeResources.Count - 1; i >= 0; i--)
            _treeResources[i].Dispose();

        _treeResources.Clear();
        _iconOuter.Clear();
        _iconInner.Clear();
        _treeGeneration++;
    }

    /// <summary>
    /// A rebuild throws away the property sets the magnification lives in, so hover state has
    /// to be re-derived from where the cursor actually is. Without this, editing the config
    /// while pointing at the dock leaves it flat until the mouse moves again.
    /// </summary>
    private void ResyncHover()
    {
        if (_magnification is null) return;
        if (!Win32.GetCursorPos(out POINT screen)) return;

        POINT client = screen;
        Win32.ScreenToClient(_hwnd, ref client);

        _hovered = false;
        if (!Contains(client.x, client.y)) return;

        _magnification.SetCursor(client.x);
        SetHovered(true);
    }

    /// <summary>
    /// The region that accepts the mouse, in client space.
    ///
    /// It grows once hovered - upward to cover icons standing proud of their rest height, and
    /// outward because magnification pushes the end icons past the rest run. Shrinking it again
    /// on leave is what keeps the dock from silently claiming a band of empty desktop: there is
    /// nothing drawn between the icons to tell the user where that dead zone would be.
    /// </summary>
    private bool Contains(float x, float y)
    {
        float width = _hovered ? _maxRunWidth : _restRunWidth;
        float left = _centerX - width / 2f;
        float right = _centerX + width / 2f;
        float top = _hovered ? _hotTop : _iconBottom - _metrics.IconSize;

        return x >= left && x <= right && y >= top && y <= _iconBottom;
    }

    private void BuildIcons(ContainerVisual root, int count)
    {
        if (_compositor is null || _surfaces is null || _magnification is null) return;

        float iconTop = _iconBottom - _metrics.IconSize;

        for (int i = 0; i < count; i++)
        {
            DockItemConfig item = _config.Items[i];

            // Rasterise at the size the icon reaches when fully magnified, so the peak of the
            // magnification is the one moment the icon is pixel-exact rather than upscaled.
            int rasterSize = (int)MathF.Ceiling(_metrics.IconSize * _metrics.MaxScale);

            CompositionDrawingSurface surface;
            using (System.Drawing.Bitmap bitmap = IconLoader.Load(item))
                surface = Own(_surfaces.CreateSurface(bitmap, rasterSize, rasterSize));

            CompositionSurfaceBrush iconBrush = Own(_compositor.CreateSurfaceBrush(surface));
            iconBrush.Stretch = CompositionStretch.Uniform;

            // Outer: position only, driven by the magnification expression.
            ContainerVisual outer = Own(_compositor.CreateContainerVisual());
            outer.Size = new Vector2(_metrics.IconSize, _metrics.IconSize);
            outer.Offset = new Vector3(0f, iconTop, 0f);

            // Inner: scale and bounce. CenterPoint sits on the bottom edge so growth pushes the
            // icon upward, away from the screen edge, rather than down through it.
            SpriteVisual inner = Own(_compositor.CreateSpriteVisual());
            inner.Size = new Vector2(_metrics.IconSize, _metrics.IconSize);
            inner.CenterPoint = new Vector3(_metrics.IconSize / 2f, _metrics.IconSize, 0f);
            inner.Brush = iconBrush;

            outer.Children.InsertAtTop(inner);
            root.Children.InsertAtTop(outer);

            _iconOuter.Add(outer);
            _iconInner.Add(inner);
            _magnification.Attach(i, outer, inner);
        }
    }

    /// <summary>
    /// Makes the dock part of the desktop rather than a window floating over it.
    ///
    /// Raised to the top of the desktop's own children so it sits above the icon view and keeps
    /// receiving clicks; application windows are above the desktop entirely, so they still
    /// cover it. If the shell is not laid out as expected we stay a normal window - a dock in
    /// the wrong z-order is a nuisance, a dock that refused to start is worse.
    ///
    /// The desktop is not always there to be joined at the moment we ask. TaskbarCreated arrives
    /// while Explorer is still assembling itself, and launching at sign-in gets in earlier
    /// still, so this waits for the icon view to exist before committing (see
    /// DesktopLayer.Ready) and looks again on a timer until it does. Meanwhile the dock is an
    /// ordinary window - on screen and usable, just not sunk in - which is the right thing to be
    /// while waiting.
    /// </summary>
    private void JoinDesktop()
    {
        if (!DesktopLayer.Ready() && _rejoinLeft > 0)
        {
            Win32.SetTimer(_hwnd, TimerRejoinDesktop, RejoinIntervalMs, IntPtr.Zero);
            return;
        }

        _desktop = DesktopLayer.Find();
        if (_desktop == IntPtr.Zero) return;

        if (Win32.SetParent(_hwnd, _desktop) == IntPtr.Zero)
        {
            _desktop = IntPtr.Zero;
            return;
        }

        Win32.SetWindowPos(_hwnd, Win32.HWND_TOP, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        // Once parented, window coordinates mean the desktop's client space rather than the
        // screen's. A join that lands after the tree was laid out has therefore invalidated the
        // position the window was placed at, and has to place it again.
        if (_layout is not null) PositionWindow(_windowW, _windowH);
    }

    private void PositionWindow(int width, int height)
    {
        RECT work = Win32.GetWorkArea();
        int x = work.Left + (work.Width - width) / 2;

        // Anchor the icon row's bottom edge, not the window's - the window carries edge slack.
        int y = (int)(work.Bottom - _metrics.ScreenMargin - _iconBottom);

        // Once parented, SetWindowPos speaks the desktop's client coordinates rather than the
        // screen's. They coincide on a single monitor at the origin and diverge the moment a
        // second display sits to the left, which is exactly the case that would look like a
        // mystery bug later.
        if (_desktop != IntPtr.Zero && Win32.GetWindowRect(_desktop, out RECT parent))
        {
            x -= parent.Left;
            y -= parent.Top;
        }

        // Normal mode deliberately leaves the z-order alone. Forcing HWND_TOP would shove the
        // dock in front of whatever the user is working in every time the config reloads, and
        // HWND_BOTTOM would drop it behind the desktop itself, which hides it completely.
        // SWP_SHOWWINDOW only when the dock is meant to be on screen. Unconditionally, a config
        // reload - or the rebuild after an Explorer restart - would drag a dock the user had
        // hidden back into view while the tray menu still showed it as hidden.
        uint flags = Win32.SWP_NOACTIVATE | (_visible ? Win32.SWP_SHOWWINDOW : 0);
        IntPtr insertAfter = IntPtr.Zero;

        switch (_config.Layer)
        {
            case DockLayer.Top: insertAfter = Win32.HWND_TOPMOST; break;
            case DockLayer.Desktop: insertAfter = Win32.HWND_TOP; break;
            default: flags |= Win32.SWP_NOZORDER; break;
        }

        Win32.SetWindowPos(_hwnd, insertAfter, x, y, width, height, flags);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        _messageCounts[msg] = _messageCounts.TryGetValue(msg, out int seen) ? seen + 1 : 1;

        switch (msg)
        {
            case Win32.WM_NCHITTEST:
                return HitTestWindow(lParam);

            case Win32.WM_MOUSEACTIVATE:
                // Clicking the dock must never take focus from whatever the user was using.
                return new IntPtr(Win32.MA_NOACTIVATE);

            case Win32.WM_SYSCOMMAND:
                // "Show desktop" minimises every top-level window, which would hide the dock at
                // precisely the moment it is wanted. Refuse, and fall through for everything else.
                if (((long)wParam & 0xFFF0) == Win32.SC_MINIMIZE) return IntPtr.Zero;
                break;

            case Win32.WM_SIZE:
                // Belt and braces: the shell does not always route "show desktop" through
                // WM_SYSCOMMAND, and a minimise that slips past leaves the dock invisible with
                // no way back. Undoing it here costs nothing when it never happens.
                if ((long)wParam == Win32.SIZE_MINIMIZED)
                    Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
                return IntPtr.Zero;

            case Win32.WM_ERASEBKGND:
                return new IntPtr(1);

            case Win32.WM_TIMER:
                if (wParam == TimerShrinkRegion)
                {
                    Win32.KillTimer(_hwnd, TimerShrinkRegion);
                    ShrinkRegion();
                }
                else if (wParam == TimerRejoinDesktop)
                {
                    Win32.KillTimer(_hwnd, TimerRejoinDesktop);
                    _rejoinLeft--;
                    JoinDesktop();
                }
                return IntPtr.Zero;

            case Win32.WM_MOUSEMOVE:
                OnMouseMove(Win32.GET_X_LPARAM(lParam));
                return IntPtr.Zero;

            case Win32.WM_MOUSELEAVE:
                _tracking = false;
                SetHovered(false);
                return IntPtr.Zero;

            case Win32.WM_LBUTTONUP:
                OnClick(Win32.GET_X_LPARAM(lParam), Win32.GET_Y_LPARAM(lParam));
                return IntPtr.Zero;

            case Win32.WM_HOTKEY:
                OnHotkey((int)wParam);
                return IntPtr.Zero;

            case Win32.WM_DISPLAYCHANGE:
            case Win32.WM_SETTINGCHANGE:
                Rebuild();
                return IntPtr.Zero;

            case Win32.WM_APP_RELOAD_CONFIG:
                ReloadConfig();
                return IntPtr.Zero;

            case Win32.WM_APP_BOUNCE_DONE:
                OnBounceFinished((int)wParam, (int)lParam);
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                // Deliberately does not end the process. This window's death is not the app's:
                // Explorer destroys it on every restart, and Recreate builds another. Exit runs
                // through the tray window, which nothing outside this process can take away.
                Win32.KillTimer(_hwnd, TimerShrinkRegion);
                Win32.KillTimer(_hwnd, TimerRejoinDesktop);
                return IntPtr.Zero;
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Everything outside the dock's live area reports as transparent, so clicks land on
    /// whatever is behind us.
    /// </summary>
    private IntPtr HitTestWindow(IntPtr lParam)
    {
        var point = new POINT { x = Win32.GET_X_LPARAM(lParam), y = Win32.GET_Y_LPARAM(lParam) };
        Win32.ScreenToClient(_hwnd, ref point);

        return new IntPtr(Contains(point.x, point.y) ? Win32.HTCLIENT : Win32.HTTRANSPARENT);
    }

    private void OnMouseMove(int clientX)
    {
        long entered = System.Diagnostics.Stopwatch.GetTimestamp();
        MoveCursor(clientX);
        _moveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - entered;
        _moveCount++;
    }

    private void MoveCursor(int clientX)
    {
        // Position first, then magnification. On arrival this means the wave opens where the
        // cursor actually is, rather than sweeping across the dock from wherever it last was.
        _magnification?.SetCursor(clientX);
        if (!_hovered) SetHovered(true);

        if (_tracking) return;

        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = Win32.TME_LEAVE,
            hwndTrack = _hwnd,
            dwHoverTime = 0,
        };
        _tracking = Win32.TrackMouseEvent(ref track);
    }

    private void SetHovered(bool hovered)
    {
        _hovered = hovered;
        _magnification?.SetHovered(hovered);
        UpdateRegion();
    }

    /// <summary>
    /// Shows or hides the dock from the tray menu.
    ///
    /// Hiding drops the hover state as well. The dock cannot receive WM_MOUSELEAVE while it is
    /// hidden, so a dock hidden mid-hover would come back still believing the cursor was on it -
    /// magnified, with its window region released, and no event coming that would fix either.
    /// </summary>
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible) SetHovered(false);
        Win32.ShowWindow(_hwnd, visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
        if (visible) ResyncHover();
    }

    /// <summary>Anything on screen that needs more room than the icons' rest rectangles.</summary>
    private bool NeedsFullRegion => _hovered || _bounceCount > 0 || _testBouncing;

    /// <summary>
    /// Sizes the window region to whatever the dock is currently drawing.
    ///
    /// On the desktop layer the dock is a child of Progman, and every window in that chain -
    /// Progman, SHELLDLL_DefView, SysListView32 - carries both WS_CLIPSIBLINGS and
    /// WS_CLIPCHILDREN. Measured, not assumed: the desktop will not paint a single pixel our
    /// window owns, at any level, which is why a rubber-band selection dragged across a 632x186
    /// dock came out with a rectangular bite missing. Reparenting deeper does not help - the
    /// clip styles are set the whole way down. Shrinking our own region does, because sibling
    /// and child clipping both honour it.
    ///
    /// The subtlety is *when* to shrink. Tying it to the hover flag was wrong: hover ends the
    /// instant the cursor leaves, but the magnification is still collapsing for another 280ms
    /// and a launch bounce runs for seconds afterwards. Both are evaluated on the compositor
    /// thread, so the region - a UI-thread window property - chopped them off mid-motion. That
    /// is the tearing on a fast sweep and the clipped bounce.
    ///
    /// So: grow immediately, shrink only on a timer, and only if nothing has asked for the room
    /// back in the meantime. A drag-select is unaffected either way, because the desktop holds
    /// the mouse capture throughout and the dock never sees a hover to grow for.
    /// </summary>
    private void UpdateRegion()
    {
        if (_config.Layer != DockLayer.Desktop || _layout is null) return;

        Win32.KillTimer(_hwnd, TimerShrinkRegion);

        if (NeedsFullRegion)
        {
            if (!_regionShrunk) return;
            Win32.SetWindowRgn(_hwnd, IntPtr.Zero, true);
            _regionShrunk = false;
            return;
        }

        Win32.SetTimer(_hwnd, TimerShrinkRegion, RegionSettleMs, IntPtr.Zero);
    }

    private void ShrinkRegion()
    {
        if (_config.Layer != DockLayer.Desktop || _layout is null) return;
        if (NeedsFullRegion || _regionShrunk) return;

        // A pixel of slack on each side. The rest rectangles are computed in floats and the
        // region in ints, and rounding the wrong way shaves a sliver off an icon's edge.
        const int Slack = 1;

        IntPtr region = Win32.CreateRectRgn(0, 0, 0, 0);
        int top = Math.Max((int)(_iconBottom - _metrics.IconSize) - Slack, 0);
        int bottom = Math.Min((int)MathF.Ceiling(_iconBottom) + Slack, _windowH);

        for (int i = 0; i < _layout.Count; i++)
        {
            int left = Math.Max((int)(_layout.RestCenterX(i) - _metrics.IconSize / 2f) - Slack, 0);
            int right = Math.Min(left + (int)MathF.Ceiling(_metrics.IconSize) + Slack * 2, _windowW);
            IntPtr icon = Win32.CreateRectRgn(left, top, right, bottom);
            Win32.CombineRgn(region, region, icon, Win32.RGN_OR);
            Win32.DeleteObject(icon);
        }

        // SetWindowRgn takes ownership of the region on success; deleting it here would be a
        // double free, and on failure the region is ours to clean up.
        if (Win32.SetWindowRgn(_hwnd, region, true) == 0) Win32.DeleteObject(region);
        else _regionShrunk = true;
    }

    private void OnClick(int clientX, int clientY)
    {
        if (_magnification is null || _bounce is null || _layout is null) return;
        if (!Contains(clientX, clientY)) return;

        // The compositor's smoothed cursor lags the real one by a few milliseconds. Hit testing
        // against the raw position is what the user aimed at, so that is what we use.
        int index = _layout.HitTest(clientX, _magnification.Magnify);
        if (index < 0 || index >= _iconInner.Count) return;

        Visual inner = _iconInner[index];
        _bounce.Start(inner);
        _bounceCount++;
        UpdateRegion();

        int generation = _treeGeneration;
        bool started = Launcher.Launch(_config.Items[index], () =>
            Win32.PostMessageW(_hwnd, Win32.WM_APP_BOUNCE_DONE, new IntPtr(generation), new IntPtr(index)));

        if (!started)
        {
            _bounce.Stop(inner);
            _bounce.Nudge(inner);
            EndBounce();
        }
    }

    /// <summary>
    /// Retires one launch bounce and lets the region shrink again once the last one is done.
    /// Clamped at zero because a rebuild resets the count while reports are still in flight.
    /// </summary>
    private void EndBounce()
    {
        _bounceCount = Math.Max(_bounceCount - 1, 0);
        UpdateRegion();
    }

    /// <summary>
    /// Ends a launch bounce.
    ///
    /// Launcher reports on a pool thread, up to five seconds after the click, and a config
    /// reload in that window closes the visual it was bouncing - so the callback cannot hold
    /// one. It carries an index and the generation it was issued against instead, and comes
    /// back through the message queue: on the UI thread nothing can rebuild underneath us, and
    /// a report from a tree that no longer exists is simply dropped.
    /// </summary>
    private void OnBounceFinished(int generation, int index)
    {
        if (_bounce is null || generation != _treeGeneration) return;
        if (index < 0 || index >= _iconInner.Count) return;

        _bounce.Stop(_iconInner[index]);
        EndBounce();
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyStallTest:
                // Proof of the architecture. Block this thread hard: any animation already in
                // flight keeps running at full frame rate, because the compositor evaluates it
                // on its own thread and never calls back into ours.
                Thread.Sleep(2000);
                break;

            case HotkeyBounceTest:
                ToggleTestBounce();
                break;
        }
    }

    /// <summary>Bounces every icon without launching anything, so the stall test has motion to watch.</summary>
    private void ToggleTestBounce()
    {
        if (_bounce is null) return;

        _testBouncing = !_testBouncing;
        foreach (Visual inner in _iconInner)
        {
            if (_testBouncing) _bounce.Start(inner);
            else _bounce.Stop(inner);
        }

        UpdateRegion();
    }

    private void StartWatchingConfig()
    {
        string? directory = Path.GetDirectoryName(_configPath);
        if (directory is null || !Directory.Exists(directory)) return;

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        // Editors write config files in several bursts. Debounce, then hand the work to the UI
        // thread - the watcher fires on a pool thread and Composition objects are not free-threaded.
        void Schedule(object? sender, FileSystemEventArgs e) =>
            _reloadDebounce?.Change(200, Timeout.Infinite);

        _reloadDebounce = new Timer(_ => Win32.PostMessageW(_hwnd, Win32.WM_APP_RELOAD_CONFIG, IntPtr.Zero, IntPtr.Zero));
        _watcher.Changed += Schedule;
        _watcher.Created += Schedule;
        _watcher.Renamed += (_, _) => _reloadDebounce?.Change(200, Timeout.Infinite);
    }

    private void ReloadConfig()
    {
        try
        {
            DockLayer previous = _config.Layer;

            _config = DockConfig.Load(_configPath);
            _metrics = _config.Metrics.ToMetrics();
            _hovered = false;
            _tracking = false;

            // The layer is the one setting a rebuild cannot deliver. It decides the window's
            // extended styles and whether the window is a child of the desktop, and both are
            // fixed at CreateWindowEx - so changing it means a new window, not a new tree. This
            // path exists because the settings panel offers the layer as a control, and a
            // control that needs the user to restart the app is a control that lies.
            if (_config.Layer != previous)
            {
                Recreate();
                WatchForeground();
                return;
            }

            Rebuild();
        }
        catch (Exception)
        {
            // A half-written file during an editor save is expected; the next event will land.
        }
    }

    /// <summary>The busiest messages this window handled, as a rate. Written to the log on exit.</summary>
    public string MessageReport()
    {
        double seconds = Math.Max((DateTime.Now - _started).TotalSeconds, 0.001);
        IEnumerable<KeyValuePair<uint, int>> top = _messageCounts.OrderByDescending(p => p.Value).Take(10);

        double microsPerMove = _moveCount == 0
            ? 0
            : _moveTicks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency / _moveCount;

        return $"messages over {seconds:N1}s: "
             + string.Join(", ", top.Select(p => $"0x{p.Key:X4}={p.Value} ({p.Value / seconds:N1}/s)"))
             + $" | mousemove: {_moveCount} handled, {microsPerMove:N1}us each";
    }

    public void Dispose()
    {
        if (_foregroundHookHandle != IntPtr.Zero) Win32.UnhookWinEvent(_foregroundHookHandle);
        _watcher?.Dispose();
        _reloadDebounce?.Dispose();

        // Tree before target: the target holds the root the tree hangs from. The surface factory
        // is not closed here - it belongs to the CompositionHost, which the panel also draws from.
        ReleaseTree();
        _target?.Dispose();

        if (_hwnd != IntPtr.Zero)
        {
            Win32.UnregisterHotKey(_hwnd, HotkeyStallTest);
            Win32.UnregisterHotKey(_hwnd, HotkeyBounceTest);
        }
    }
}
