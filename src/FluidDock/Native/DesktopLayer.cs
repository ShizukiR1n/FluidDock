namespace FluidDock.Native;

/// <summary>
/// Sinks a window into the desktop, below every application window.
///
/// Dropping WS_EX_TOPMOST is not enough on its own. A plain top-level window still sits in the
/// normal z-order band, so it drifts above whatever the user opens next, and Win+D minimises it
/// along with everything else - hiding the dock at exactly the moment it is wanted.
///
/// What the shell actually supports is reparenting into the desktop itself. Progman is the
/// desktop window; it owns a SHELLDLL_DefView that holds the icon list. A window parented to
/// Progman and raised above that view is drawn as part of the desktop: applications always
/// cover it, Win+D ignores it because it is no longer top-level, and it reappears the instant
/// the desktop does.
///
/// Note this is deliberately *not* the WorkerW recipe that wallpaper tools use. That one asks
/// Explorer (via the undocumented 0x052C message) for a surface *behind* the icon view, which
/// is right for painting a wallpaper and wrong for a dock: the icon view spans the whole screen
/// and handles its own clicks, so anything parented behind it is visible but dead to the mouse.
/// </summary>
internal static class DesktopLayer
{
    /// <summary>
    /// The desktop window to parent into, or IntPtr.Zero if the shell is not laid out the way
    /// we expect. Callers should carry on as an ordinary window in that case rather than fail.
    /// </summary>
    public static IntPtr Find()
    {
        IntPtr owner = IconViewOwner();
        return owner != IntPtr.Zero ? owner : Win32.FindWindowW("Progman", null);
    }

    /// <summary>
    /// Whether the desktop is finished enough to be worth parenting into.
    ///
    /// Progman exists from the moment Explorer starts, but the icon view under it is created
    /// separately and a little later - and TaskbarCreated, the only notice we get that the shell
    /// came back, can arrive in between. Joining during that gap is worse than waiting: a newly
    /// created child goes to the top of its parent's z-order, so the view appears above the dock
    /// and leaves it drawn but dead to the mouse. Waiting for the view means never landing there.
    /// </summary>
    public static bool Ready() => IconViewOwner() != IntPtr.Zero;

    /// <summary>The window holding the desktop icon view, or Zero if there is not one yet.</summary>
    private static IntPtr IconViewOwner()
    {
        // Normally the icon view lives directly under Progman. During a wallpaper slideshow
        // Explorer sometimes moves it into a WorkerW instead, and then that WorkerW is the
        // window applications sit above, so it is the one to join.
        IntPtr progman = Win32.FindWindowW("Progman", null);
        if (progman != IntPtr.Zero &&
            Win32.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            return progman;

        IntPtr owner = IntPtr.Zero;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (Win32.FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                return true;

            owner = hwnd;
            return false;
        }, IntPtr.Zero);

        return owner;
    }
}
