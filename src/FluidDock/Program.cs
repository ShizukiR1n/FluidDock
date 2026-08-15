using System.Text;
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

        DockWindow? dock = null;
        TrayIcon? tray = null;

        try
        {
            Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            // Must precede `new Compositor()`.
            CompositorInterop.EnsureDispatcherQueueOnCurrentThread();
            Note("Dispatcher queue ready");

            // Named locally as well as stored in the outer variable: the handlers below close
            // over it, and a captured nullable is not something the compiler will let them
            // dereference. The outer one exists only so the finally block can still see it.
            var window = new DockWindow(DockConfig.DefaultPath);
            dock = window;
            window.Create();
            Note($"Dock created, hwnd=0x{window.Handle:X}, gpu={window.AdapterName}");

            // Same thread as the dock, so both windows are served by the one message loop below
            // and neither needs to be thread-safe with respect to the other.
            tray = new TrayIcon();
            tray.VisibilityToggled += window.SetVisible;

            // Ending the loop directly rather than closing the dock's window. The dock lives on
            // the desktop, so Explorer owns its lifetime and it is simply absent after a restart -
            // and posting WM_CLOSE to a handle that no longer names anything is how this used to
            // leave a process with a tray icon, no window, and no way out but Task Manager.
            tray.ExitRequested += () => Win32.PostQuitMessage(0);
            tray.ShellRestarted += () => RebuildDock(window);
            tray.Create();
            Note("Tray icon added");

            if (exitAfterSeconds > 0)
                ScheduleExit(tray.Handle, exitAfterSeconds);

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
            dock?.Dispose();
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
