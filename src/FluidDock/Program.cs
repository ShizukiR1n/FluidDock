using System.Text;
using FluidDock.Graphics;
using FluidDock.Menu;
using FluidDock.Native;

namespace FluidDock;

/// <summary>
/// Entry point and message loop.
///
/// Two things must happen in order before anything else: DPI awareness, so the dock is not
/// bitmap-stretched on a scaled display, and the DispatcherQueue, without which the Compositor
/// constructor throws. Both are easy to get wrong by moving a line.
/// </summary>
internal static class Program
{
    private static readonly StringBuilder Log = new();

    [STAThread]
    private static int Main(string[] args)
    {
        int exitAfterSeconds = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--exit-after") int.TryParse(args[i + 1], out exitAfterSeconds);
        }

        // Once this is a shortcut people double-click, launching it twice is a matter of when,
        // not if - and two docks stacked on the same pixels look like one dock with broken
        // animation. Session-scoped rather than global: a second desktop session is a second
        // desktop, and is entitled to its own dock.
        using var single = new Mutex(true, @"Local\FluidDock.SingleInstance", out bool ours);
        if (!ours) return 0;

        CompositionHost? graphics = null;
        DockWindow? dock = null;
        MenuWindow? menu = null;
        TrayIcon? tray = null;

        try
        {
            Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            // Nothing this thread owns accepts typed text. The dock is clicks and the settings
            // panel reads exactly one key - Escape - so there is no field here for an input method
            // to serve.
            //
            // Left on, there is one anyway. The panel takes the keyboard focus deliberately, and
            // the first time it does, Windows attaches the user's IME to the process: on this
            // machine that is Sogou, which brings twenty DLLs and about 37 MB with it - measured,
            // and by a wide margin the largest single cost in the program. It was diagnosed for a
            // long time as the font stack, because it arrives at the same moment DirectWrite does
            // and both were charged to "opening the settings panel".
            //
            // This thread only, not 0xFFFFFFFF for the process. There is exactly one place in the
            // app where typing is the user's to do and not ours to refuse - the file name box in
            // the shell's own open dialog - and disabling the IME process-wide made that box
            // unable to accept Chinese, which is not a trade a Chinese user should be asked to
            // make. So the dialog runs on a thread of its own, where the IME is untouched: see
            // MenuWindow.Dialog. Opening it does load the 37 MB, and only then.
            //
            // Must be before the first CreateWindowEx on this thread; afterwards it silently does
            // nothing.
            Win32.ImmDisableIME(Win32.GetCurrentThreadId());

            // Must precede `new Compositor()`.
            CompositorInterop.EnsureDispatcherQueueOnCurrentThread();
            Note("Dispatcher queue ready");

            // One Compositor and one D3D device for the process. Both windows below draw from
            // these, and both outlive any single HWND.
            var host = new CompositionHost();
            graphics = host;

            // Named locally as well as stored in the outer variable: the handlers below close
            // over it, and a captured nullable is not something the compiler will let them
            // dereference. The outer one exists only so the finally block can still see it.
            var window = new DockWindow(host, DockConfig.DefaultPath);
            dock = window;
            window.Create();
            Note($"Dock created, hwnd=0x{window.Handle:X}, gpu={window.AdapterName}");

            // The panel edits this and saves; the dock's own file watcher picks the change up and
            // rebuilds. Deliberately the same route a hand-edited config takes - see SettingsStore.
            var settings = new SettingsStore(DockConfig.DefaultPath);

            // Declared before the panel so the panel's rows can close over it, created before the
            // dock is shown so the two never disagree about whether the dock is on screen. Its
            // window is not made until further down; nothing here touches one.
            var notify = new TrayIcon();
            tray = notify;

            // Named locally for the same reason `window` is: the closures below need something
            // the compiler will let them dereference.
            MenuWindow? built = null;
            var items = new DockItems(
                settings,
                (ask, apply) => built?.Dialog(ask, apply),
                action => built?.Defer(action));

            // The page factory, and the one place a panel build begins - so it is also the place
            // the theme is settled, before a single row asks MenuTheme what colour it is.
            var panel = new MenuWindow(host, () =>
            {
                MenuTheme.Use(settings.Config.Theme);
                return MenuDefinition.Build(new MenuContext
            {
                Store = settings,
                Items = items,
                Adapter = () => window.AdapterName,
                DockVisible = () => notify.DockVisible,
                SetDockVisible = visible =>
                {
                    notify.DockVisible = visible;
                    window.SetVisible(visible);
                },
                AutoStart = () => Native.AutoStart.Enabled,
                SetAutoStart = value => Native.AutoStart.Enabled = value,
                SetTheme = theme =>
                {
                    settings.Config.Theme = theme;
                    settings.Save();

                    // Deferred, because rebuilding the panel disposes the tree that is currently
                    // dispatching the click that asked for it. Same rule as the item list's.
                    built?.Defer(() => built.Restyle());
                },
                OpenConfig = () => OpenConfig(settings.Path),
                Quit = () => Win32.PostQuitMessage(0),
                });
            });

            built = panel;
            menu = panel;

            // An external edit means the item rows are holding entries from a list that has been
            // replaced, so the panel has to be built again rather than merely refreshed. Reload
            // says which happened; see SettingsStore.
            panel.Opening += () =>
            {
                if (settings.Reload()) panel.Reload();
            };

            panel.Changed += settings.Save;
            items.Changed += settings.Save;
            items.StructureChanged += panel.Reload;

            panel.Create();
            Note($"Menu window created, hwnd=0x{panel.Handle:X}");

            // Same thread as the dock, so all three windows are served by the one message loop
            // below and none needs to be thread-safe with respect to the others.
            notify.VisibilityToggled += window.SetVisible;
            notify.MenuRequested += () => ShowMenu(panel);

            // Ending the loop directly rather than closing the dock's window. The dock lives on
            // the desktop, so Explorer owns its lifetime and it is simply absent after a restart -
            // and posting WM_CLOSE to a handle that no longer names anything is how this used to
            // leave a process with a tray icon, no window, and no way out but Task Manager.
            notify.ExitRequested += () => Win32.PostQuitMessage(0);
            notify.ShellRestarted += () => RebuildDock(window);
            notify.Create();
            Note("Tray icon added");

            if (exitAfterSeconds > 0)
                ScheduleExit(notify.Handle, exitAfterSeconds);

            PumpMessages();
            return 0;
        }
        catch (Exception ex)
        {
            Note($"FATAL {ex.GetType().Name}: {ex.Message}");
            Note(ex.StackTrace ?? string.Empty);
            Win32.MessageBoxW(IntPtr.Zero, $"{ex.GetType().Name}: {ex.Message}", "FluidDock", 0x10);
            return 1;
        }
        finally
        {
            if (dock is not null) Note(dock.MessageReport());
            Note(PumpReport());

            // Before the dock, and unconditionally: an icon that is not explicitly removed stays
            // in the notification area as a dead entry until something makes the shell re-poll it.
            tray?.Dispose();
            menu?.Dispose();
            dock?.Dispose();

            // Last: it holds the D3D device the other two drew through.
            graphics?.Dispose();
            Flush();
        }
    }

    // Split so the two halves can be told apart. When input feels slow, the question that decides
    // where to look is whether the thread is busy dispatching or idle inside GetMessage waiting to
    // be handed something - and from the outside those look identical.
    private static long _waitingTicks;
    private static long _dispatchTicks;

    private static void PumpMessages()
    {
        while (true)
        {
            long before = System.Diagnostics.Stopwatch.GetTimestamp();
            int result = Win32.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0);
            long got = System.Diagnostics.Stopwatch.GetTimestamp();
            if (result == 0 || result == -1) break;

            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);

            _waitingTicks += got - before;
            _dispatchTicks += System.Diagnostics.Stopwatch.GetTimestamp() - got;
        }
    }

    private static string PumpReport()
    {
        double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return $"pump: {_waitingTicks * toMs:N0}ms waiting in GetMessage, {_dispatchTicks * toMs:N0}ms dispatching";
    }

    /// <summary>
    /// Puts the dock back after Explorer restarted.
    ///
    /// Failure here is survivable and must stay that way. This runs inside a window procedure
    /// called from native code, so an exception escaping it would tear the process down - and it
    /// would take the tray icon with it, which is the one thing still working at that point and
    /// the user's only way to close the app cleanly.
    /// </summary>
    private static void RebuildDock(DockWindow dock)
    {
        try
        {
            dock.Recreate();
            Note($"Explorer restarted; dock rebuilt, hwnd=0x{dock.Handle:X}");
        }
        catch (Exception ex)
        {
            Note($"Explorer restarted; dock rebuild failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens or closes the settings panel.
    ///
    /// Wrapped for the same reason the Explorer rebuild is: this runs inside a window procedure
    /// called from native code, so an exception escaping it would take the process down - and the
    /// panel is by far the most elaborate thing in the app, built lazily on first use out of
    /// fonts, bitmaps and GPU surfaces that a stripped Windows image might not all provide. A
    /// dock that cannot open its settings is a nuisance; a dock that dies trying is not.
    /// </summary>
    private static void ShowMenu(MenuWindow panel)
    {
        try
        {
            panel.Toggle();
        }
        catch (Exception ex)
        {
            Note($"menu failed: {ex.GetType().Name}: {ex.Message}");
            Note(ex.StackTrace ?? string.Empty);
        }
    }

    /// <summary>
    /// Reveals dock.json in Explorer, from the panel's "open config" row.
    ///
    /// Selected rather than opened: there is no telling what the user has .json associated with,
    /// and a settings button that silently launches an unknown editor - or nothing at all - is
    /// worse than one that shows them the file and lets them decide.
    /// </summary>
    private static void OpenConfig(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = false,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Note($"open config failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Used by the screenshot harness so a test run cannot leave a window behind.</summary>
    private static void ScheduleExit(IntPtr hwnd, int seconds)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            // Aimed at the tray window, which handles WM_CLOSE by ending the message loop. The
            // dock's window is not a reliable target: it may have been rebuilt, or be missing.
            Win32.PostMessageW(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        });
    }

    private static void Note(string message)
    {
        lock (Log) Log.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    private static void Flush()
    {
        try
        {
            lock (Log)
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fluiddock.log"), Log.ToString());
        }
        catch
        {
            // Diagnostics are not worth failing shutdown over.
        }
    }
}
