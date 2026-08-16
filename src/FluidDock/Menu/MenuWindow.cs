using System.Numerics;
using System.Runtime.InteropServices;
using FluidDock.Graphics;
using FluidDock.Native;
using FluidDock.Visuals;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace FluidDock.Menu;

/// <summary>
/// The window the settings panel lives in, and the only part of the menu that knows about Win32.
///
/// It is almost the opposite of the dock's window. The dock refuses activation, sinks itself into
/// the desktop and never takes focus; this one takes focus deliberately, because that is what
/// makes it dismiss the way a macOS popover does - click anywhere else and it goes away, with no
/// need to watch the mouse globally or guess at what counts as "elsewhere". WM_ACTIVATE answers
/// that question for us, and the shell has already worked out the edge cases.
///
/// The panel is built on first open, not at startup, and kept afterwards. Rasterising twenty-odd
/// strings costs a few milliseconds that a user who never opens the panel should not pay, and one
/// that does should pay only once.
/// </summary>
internal sealed class MenuWindow : IDisposable
{
    private const string ClassName = "FluidDockMenu";

    /// <summary>Gap between the panel and the point it was opened from.</summary>
    private const int AnchorGap = 12;

    /// <summary>Closest the panel is allowed to get to the edge of the work area.</summary>
    private const int ScreenMargin = 8;

    /// <summary>How small the card starts and ends. Small enough to feel, too small to watch.</summary>
    private const float OpenFromScale = 0.94f;
    private const float CloseToScale = 0.97f;

    /// <summary>Ticks while the mouse is down, so a reorder held at the edge keeps scrolling.</summary>
    private static readonly IntPtr TimerDragScroll = new(1);
    private const uint DragScrollIntervalMs = 15;

    private readonly CompositionHost _host;
    private readonly Func<MenuPage> _buildPage;

    /// <summary>Work posted to run after the message that asked for it. See <see cref="Defer"/>.</summary>
    private readonly Queue<Action> _deferred = new();

    private WndProc? _wndProc;
    private IntPtr _hwnd;
    private DesktopWindowTarget? _target;
    private MenuPanel? _panel;

    private AnimatedProperty? _fade;
    private AnimatedProperty? _scale;

    private bool _open;
    private bool _closing;
    private bool _tracking;
    private bool _capturing;

    /// <summary>
    /// True while a deferred action is running, which is when a system dialog may be on screen.
    ///
    /// The panel dismisses itself on losing activation, and that is exactly what a file dialog
    /// takes. Without this, choosing an icon would close the panel the moment the dialog opened
    /// and reopen it into nothing.
    /// </summary>
    private bool _modal;

    /// <summary>
    /// Screen position of the panel's bottom-right corner, kept from the last time it was placed.
    ///
    /// A rebuild changes the panel's height, and it has to grow from the corner it grew out of -
    /// upward, away from the notification area. Anchoring the top-left instead would push the
    /// bottom of a lengthening list down behind the taskbar.
    /// </summary>
    private int _anchorRight;
    private int _anchorBottom;

    /// <summary>Raised when a row changed something, so the config can be written.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised just before the panel appears, so whoever owns the settings can re-read them.
    /// The config file is editable by hand and by the panel both, and the panel is the one that
    /// has been looking away.
    /// </summary>
    public event Action? Opening;

    public IntPtr Handle => _hwnd;

    public bool IsOpen => _open;

    public MenuWindow(CompositionHost host, Func<MenuPage> buildPage)
    {
        _host = host;
        _buildPage = buildPage;
    }

    public void Create()
    {
        IntPtr hInstance = Win32.GetModuleHandleW(null);
        _wndProc = WindowProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = Win32.LoadCursorW(IntPtr.Zero, Win32.IDC_ARROW),
            lpszClassName = ClassName,
        };

        if (Win32.RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException($"menu RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

        // No WS_EX_NOACTIVATE, unlike the dock: this window wants focus, both for the Escape key
        // and so that losing it is what dismisses the panel.
        _hwnd = Win32.CreateWindowExW(
            Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOREDIRECTIONBITMAP,
            ClassName, "FluidDock Settings", Win32.WS_POPUP,
            0, 0, 100, 100,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"menu CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        _target = CompositorInterop.CreateDesktopWindowTarget(_host.Compositor, _hwnd, isTopmost: true);
    }

    public void Toggle()
    {
        if (_open) Close();
        else Open();
    }

    public void Open()
    {
        if (_hwnd == IntPtr.Zero) return;

        Opening?.Invoke();

        MenuPanel panel = EnsurePanel();

        // Values may have been changed from the config file since the panel was last on screen.
        panel.Refresh();

        Place(panel);

        _open = true;
        _closing = false;

        Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
        Win32.SetForegroundWindow(_hwnd);

        AnimateOpen(panel);
    }

    public void Close()
    {
        if (!_open || _panel is null) return;

        _open = false;
        _closing = true;

        ReleasePointer();
        _panel.CancelPress();
        _panel.PointerLeave();

        AnimateClose(_panel);
    }

    /// <summary>
    /// Runs an action once the current message has been handled.
    ///
    /// Anything that opens a dialog has to go through here. A file picker started from inside a
    /// mouse-up handler runs its own message loop while this window still holds the mouse capture
    /// and the panel still believes a button is pressed - so the dialog appears, and behind it the
    /// panel is stuck mid-click with a row that will never be released.
    /// </summary>
    public void Defer(Action action)
    {
        if (_hwnd == IntPtr.Zero) return;

        _deferred.Enqueue(action);
        Win32.PostMessageW(_hwnd, Win32.WM_APP_MENU_RUN, IntPtr.Zero, IntPtr.Zero);
    }

    private void RunDeferred()
    {
        if (_deferred.Count == 0) return;

        Action action = _deferred.Dequeue();
        bool wasOpen = _open;

        _modal = true;

        try
        {
            action();
        }
        catch (Exception)
        {
            // Same rule as everywhere else in a window procedure: this is called from native
            // code, and an exception escaping it takes the process down with the tray icon.
        }
        finally
        {
            _modal = false;
        }

        // A dialog leaves the foreground somewhere else. Taking it back is what makes the panel
        // still be there - and still dismissable by clicking away - once the dialog is gone.
        if (wasOpen && _open) Win32.SetForegroundWindow(_hwnd);
    }

    /// <summary>
    /// Builds the panel again, in place.
    ///
    /// Adding or removing an entry changes how tall the panel is, and its height is decided once
    /// when the tree is built - the background is a baked bitmap of exactly that size and the
    /// window is sized to match. So a list that got longer is a new panel, not a moved row. The
    /// scroll position is carried across; the anchor corner does not move.
    /// </summary>
    public void Reload()
    {
        if (_panel is null) return;

        float scroll = _panel.Scroll;

        _fade?.Retire();
        _scale?.Retire();
        _fade = null;
        _scale = null;

        if (_target is not null) _target.Root = null;
        _panel.Dispose();
        _panel = null;

        MenuPanel panel = EnsurePanel();
        panel.RestoreScroll(scroll);

        if (!_open) return;

        Position(panel);

        // Straight to fully open. The card that was on screen a moment ago was already there;
        // replaying the entrance animation would say something appeared that did not.
        panel.Card.Opacity = 1f;
        panel.Card.Scale = Vector3.One;
    }

    private MenuPanel EnsurePanel()
    {
        if (_panel is not null) return _panel;

        _panel = new MenuPanel(_host, _buildPage());
        _panel.Changed += () => Changed?.Invoke();
        _panel.Dismissed += Close;

        _target!.Root = _panel.Root;

        ContainerVisual card = _panel.Card;

        // The card grows out of the corner nearest whatever opened it, which is the bottom-right
        // for anything launched from the notification area. Scaling about the centre instead
        // makes the panel look like it faded in from nowhere in particular.
        card.CenterPoint = new Vector3(_panel.PanelWidth, _panel.PanelHeight, 0f);

        _fade = new AnimatedProperty(_host.Compositor, card, "Opacity");
        _scale = new AnimatedProperty(_host.Compositor, card, "Scale");

        return _panel;
    }

    /// <summary>
    /// Puts the window where the panel should appear, given where the pointer is.
    ///
    /// The window is bigger than the panel by the shadow margin on every side, so the arithmetic
    /// is done in panel coordinates and only converted at the last step. Getting that backwards
    /// puts the panel a shadow's width away from where it was asked to be, which looks like a
    /// rounding bug and is not one.
    /// </summary>
    private void Place(MenuPanel panel)
    {
        Win32.GetCursorPos(out POINT cursor);

        // Up and to the left of the pointer: opened from the notification area, that is the only
        // direction with room. Recorded as a corner rather than a position so that a rebuild -
        // which changes the height - grows the panel away from the taskbar rather than into it.
        _anchorRight = cursor.x;
        _anchorBottom = cursor.y - AnchorGap;

        Position(panel);
    }

    private void Position(MenuPanel panel)
    {
        RECT work = Win32.GetWorkArea();

        int panelW = (int)MathF.Ceiling(panel.PanelWidth);
        int panelH = (int)MathF.Ceiling(panel.PanelHeight);

        int x = _anchorRight - panelW;
        int y = _anchorBottom - panelH;

        // Clamped afterwards, so it still behaves on a taskbar moved elsewhere - and so a list
        // long enough to outgrow the screen ends up against the top edge rather than off it.
        x = Math.Clamp(x, work.Left + ScreenMargin, Math.Max(work.Right - ScreenMargin - panelW, work.Left + ScreenMargin));
        y = Math.Clamp(y, work.Top + ScreenMargin, Math.Max(work.Bottom - ScreenMargin - panelH, work.Top + ScreenMargin));

        int margin = (int)MenuTheme.ShadowMargin;
        Win32.SetWindowPos(
            _hwnd, Win32.HWND_TOPMOST,
            x - margin, y - margin,
            (int)MathF.Ceiling(panel.WindowWidth), (int)MathF.Ceiling(panel.WindowHeight),
            Win32.SWP_NOACTIVATE);
    }

    private void AnimateOpen(MenuPanel panel)
    {
        Compositor compositor = _host.Compositor;
        ContainerVisual card = panel.Card;

        card.Opacity = 0f;
        card.Scale = new Vector3(OpenFromScale, OpenFromScale, 1f);

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, MenuTheme.EaseOut(compositor));
        fade.Duration = MenuTheme.OpenDuration;
        _fade?.Run(fade, () => card.Opacity = 1f);

        // A spring rather than a curve, so the card arrives with a trace of overshoot. It is the
        // difference between a panel that appears and one that is put there.
        _scale?.Run(MenuTheme.Spring(compositor, Vector3.One), () => card.Scale = Vector3.One);
    }

    private void AnimateClose(MenuPanel panel)
    {
        Compositor compositor = _host.Compositor;
        ContainerVisual card = panel.Card;

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f, MenuTheme.EaseIn(compositor));
        fade.Duration = MenuTheme.CloseDuration;

        // Hiding the window is the last thing that happens, and it happens from the fade rather
        // than the shrink: the two animations complete independently, and the visible one is the
        // one that has to decide when there is nothing left to see.
        _fade?.Run(fade, () =>
        {
            card.Opacity = 0f;
            _closing = false;
            Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
        });

        var shrink = new Vector3(CloseToScale, CloseToScale, 1f);
        Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, shrink, MenuTheme.EaseIn(compositor));
        scale.Duration = MenuTheme.CloseDuration;
        _scale?.Run(scale, () => card.Scale = shrink);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_NCHITTEST:
                return HitTest(lParam);

            case Win32.WM_ERASEBKGND:
                return new IntPtr(1);

            case Win32.WM_MOUSEMOVE:
                OnMouseMove(Win32.GET_X_LPARAM(lParam), Win32.GET_Y_LPARAM(lParam));
                return IntPtr.Zero;

            case Win32.WM_MOUSELEAVE:
                _tracking = false;
                _panel?.PointerLeave();
                return IntPtr.Zero;

            case Win32.WM_LBUTTONDOWN:
                OnMouseDown(Win32.GET_X_LPARAM(lParam), Win32.GET_Y_LPARAM(lParam));
                return IntPtr.Zero;

            case Win32.WM_LBUTTONUP:
                OnMouseUp(Win32.GET_X_LPARAM(lParam), Win32.GET_Y_LPARAM(lParam));
                return IntPtr.Zero;

            case Win32.WM_CAPTURECHANGED:
                // Something else took the mouse - a system gesture, or a window appearing. The
                // press will never be released, so it has to be abandoned rather than left half
                // done with a row stuck in its pressed state.
                _capturing = false;
                Win32.KillTimer(_hwnd, TimerDragScroll);
                _panel?.CancelPress();
                return IntPtr.Zero;

            case Win32.WM_MOUSEWHEEL:
                _panel?.Wheel(Win32.GET_WHEEL_DELTA_WPARAM(wParam) / Win32.WHEEL_DELTA);
                return IntPtr.Zero;

            case Win32.WM_APP_MENU_RUN:
                RunDeferred();
                return IntPtr.Zero;

            case Win32.WM_TIMER:
                if (wParam == TimerDragScroll) _panel?.DragTick();
                return IntPtr.Zero;

            case Win32.WM_ACTIVATE:
                // The whole dismissal story, in one line. Anything the user clicks that is not
                // this window takes the foreground, and that is exactly when the panel should go.
                // Except while a dialog we opened has it, which is the one case where losing
                // activation means the panel is being used rather than abandoned.
                if (!_modal && ((long)wParam & 0xFFFF) == Win32.WA_INACTIVE) Close();
                return IntPtr.Zero;

            case Win32.WM_KEYDOWN:
                if ((int)wParam == Win32.VK_ESCAPE) Close();
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                return IntPtr.Zero;
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// The shadow margin is not part of the panel, so clicks there fall through to whatever is
    /// behind - otherwise the panel would swallow a band of clicks 34 pixels wide that it does
    /// not draw anything in.
    /// </summary>
    private IntPtr HitTest(IntPtr lParam)
    {
        // Transparent while closing as well as while absent. The window stays on screen for the
        // length of the fade, and a panel the user has already dismissed should not be able to
        // catch the next click on its way out.
        if (_panel is null || _closing) return new IntPtr(Win32.HTTRANSPARENT);

        var point = new POINT { x = Win32.GET_X_LPARAM(lParam), y = Win32.GET_Y_LPARAM(lParam) };
        Win32.ScreenToClient(_hwnd, ref point);

        float margin = MenuTheme.ShadowMargin;
        bool inside =
            point.x >= margin && point.x < margin + _panel.PanelWidth &&
            point.y >= margin && point.y < margin + _panel.PanelHeight;

        return new IntPtr(inside ? Win32.HTCLIENT : Win32.HTTRANSPARENT);
    }

    private void OnMouseMove(int clientX, int clientY)
    {
        if (_panel is null) return;

        _panel.PointerMove(clientX - MenuTheme.ShadowMargin, clientY - MenuTheme.ShadowMargin);

        if (_tracking || _capturing) return;

        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = Win32.TME_LEAVE,
            hwndTrack = _hwnd,
            dwHoverTime = 0,
        };
        _tracking = Win32.TrackMouseEvent(ref track);
    }

    private void OnMouseDown(int clientX, int clientY)
    {
        if (_panel is null) return;

        // Captured on every press, not only on the draggable rows. A click that starts on a
        // switch and ends somewhere else has to be able to cancel, and it can only do that if
        // the release comes back here.
        Win32.SetCapture(_hwnd);
        _capturing = true;

        // Started on every press rather than when a reorder actually begins, because the panel
        // is the only thing that knows a drag has started and it finds out from a mouse message -
        // which is exactly the event a pointer held still at the edge of the list does not send.
        // It costs 66 ticks a second for as long as a button is down, and DragTick returns
        // immediately unless something is being dragged.
        Win32.SetTimer(_hwnd, TimerDragScroll, DragScrollIntervalMs, IntPtr.Zero);

        _panel.PointerDown(clientX - MenuTheme.ShadowMargin, clientY - MenuTheme.ShadowMargin);
    }

    private void OnMouseUp(int clientX, int clientY)
    {
        if (_panel is null) return;

        // The panel is told first: releasing capture posts WM_CAPTURECHANGED, whose handler
        // cancels the press, and a press cancelled before it is delivered never happened.
        _panel.PointerUp(clientX - MenuTheme.ShadowMargin, clientY - MenuTheme.ShadowMargin);
        ReleasePointer();
    }

    private void ReleasePointer()
    {
        Win32.KillTimer(_hwnd, TimerDragScroll);

        if (!_capturing) return;
        _capturing = false;
        Win32.ReleaseCapture();
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) Win32.KillTimer(_hwnd, TimerDragScroll);

        _fade?.Retire();
        _scale?.Retire();

        // Detached before the tree is closed: the target holds its own reference to the root, and
        // closing the root while it is still attached leaves the target pointing at a dead visual.
        if (_target is not null) _target.Root = null;

        _panel?.Dispose();
        _target?.Dispose();

        if (_hwnd != IntPtr.Zero)
        {
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
