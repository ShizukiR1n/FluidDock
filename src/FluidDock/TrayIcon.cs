using System.Runtime.InteropServices;
using FluidDock.Native;

namespace FluidDock;

/// <summary>
/// The notification-area icon and its right-click menu.
///
/// It owns a hidden top-level window of its own rather than hanging off the dock's HWND, for two
/// reasons. The dock is a child of Progman with WS_EX_NOACTIVATE, and a popup menu will not
/// dismiss correctly unless its owner can be brought to the foreground first - the shell's own
/// documented workaround for exactly this. Forcing that on the dock window would mean handing it
/// focus, which is the one thing it is built never to take. The second reason is lifetime: when
/// Explorer restarts it destroys Progman and every child, the dock included. This window survives
/// that, so it is where both halves of the recovery live - the TaskbarCreated handler that puts
/// the dock back, and the exit route, which has to keep working even if the rebuild does not.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string ClassName = "FluidDockTray";
    private const uint IconId = 1;

    private const int CmdMenu = 1;
    private const int CmdShowDock = 2;
    private const int CmdExit = 3;

    private WndProc? _wndProc;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;
    private uint _taskbarCreated;

    /// <summary>Raised when the user picks the show/hide item, with the state they asked for.</summary>
    public event Action<bool>? VisibilityToggled;

    public event Action? ExitRequested;

    /// <summary>Raised when the user picks the settings item. The panel decides whether that
    /// means opening or closing - it is a toggle, and only it knows which it currently is.</summary>
    public event Action? MenuRequested;

    /// <summary>
    /// Raised once Explorer has restarted and the icon is back in the rebuilt notification area.
    /// The dock does not survive that, so this is its cue to build itself a new window.
    /// </summary>
    public event Action? ShellRestarted;

    /// <summary>
    /// The screen changed shape - a resolution change, or the taskbar moving or resizing - and
    /// the dock has to be placed again.
    ///
    /// Heard here and not by the dock because the dock cannot hear it. WM_DISPLAYCHANGE and
    /// WM_SETTINGCHANGE are broadcast to top-level windows, and on the desktop layer the dock is
    /// a child of the desktop: the resolution changed, Explorer resized its windows, and the dock
    /// sat where 1920x1080 had put it, half over the taskbar of a 1680x1050 screen. This window
    /// is top-level for the whole life of the process, so it is the ear.
    /// </summary>
    public event Action? DisplayChanged;

    /// <summary>Whether the menu's show item is currently ticked.</summary>
    public bool DockVisible { get; set; } = true;

    /// <summary>Where to aim a shutdown request from outside the message loop.</summary>
    public IntPtr Handle => _hwnd;

    public void Create()
    {
        IntPtr hInstance = Win32.GetModuleHandleW(null);
        _wndProc = WindowProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = ClassName,
        };

        if (Win32.RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException($"tray RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

        // Not a message-only window (HWND_MESSAGE): those cannot be foregrounded, and the menu
        // needs that. Zero-sized, never shown, and WS_EX_TOOLWINDOW keeps it out of Alt+Tab.
        _hwnd = Win32.CreateWindowExW(
            Win32.WS_EX_TOOLWINDOW,
            ClassName, "FluidDock", Win32.WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"tray CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        _taskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");
        _icon = LoadIcon();
        Add();
    }

    /// <summary>
    /// The tray image, at the size the shell is actually going to draw it.
    ///
    /// Asking LoadImage for SM_CXSMICON rather than passing LR_DEFAULTSIZE matters: the default
    /// is 32x32, which the shell then shrinks to 16 with a bilinear filter. The .ico carries a
    /// hand-made 16px entry precisely so that scaling never happens, and requesting the wrong
    /// size throws it away.
    /// </summary>
    private static IntPtr LoadIcon()
    {
        int cx = Win32.GetSystemMetrics(Win32.SM_CXSMICON);
        int cy = Win32.GetSystemMetrics(Win32.SM_CYSMICON);

        string path = Path.Combine(AppContext.BaseDirectory, "assets", "tray.ico");
        if (File.Exists(path))
        {
            IntPtr fromFile = Win32.LoadImageW(IntPtr.Zero, path, Win32.IMAGE_ICON, cx, cy, Win32.LR_LOADFROMFILE);
            if (fromFile != IntPtr.Zero) return fromFile;
        }

        // Fall back to the icon compiled into our own exe, so a missing or corrupt assets file
        // costs the user a different picture rather than a blank space in the tray.
        string exe = Environment.ProcessPath ?? string.Empty;
        if (exe.Length > 0 && Win32.ExtractIconExW(exe, 0, out IntPtr large, out IntPtr small, 1) > 0)
        {
            if (large != IntPtr.Zero && large != small) Win32.DestroyIcon(large);
            if (small != IntPtr.Zero) return small;
        }

        return IntPtr.Zero;
    }

    private void Add()
    {
        NOTIFYICONDATAW data = Data();
        data.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP;
        data.uCallbackMessage = Win32.WM_APP_TRAY;
        data.hIcon = _icon;
        data.szTip = "FluidDock";

        _added = Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data);
    }

    private NOTIFYICONDATAW Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Explorer restarted and threw away every registered icon. Add ours back, then say so -
        // the icon is the easy half, and the dock's window went down with Progman.
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _added = false;
            Add();
            ShellRestarted?.Invoke();
            return IntPtr.Zero;
        }

        if (msg == Win32.WM_APP_TRAY)
        {
            // Left-click is deliberately unhandled: a single click on a tray icon is ambiguous,
            // and the two things it could mean both have unambiguous homes in the menu.
            if ((uint)(long)lParam == Win32.WM_RBUTTONUP) ShowMenu();
            else if ((uint)(long)lParam == Win32.WM_LBUTTONDBLCLK) MenuRequested?.Invoke();
            return IntPtr.Zero;
        }

        // The one place a shutdown can be aimed at from off the message loop, since this window
        // outlives the dock's. DestroyWindow does not send WM_CLOSE, so Dispose cannot re-enter.
        if (msg == Win32.WM_CLOSE)
        {
            ExitRequested?.Invoke();
            return IntPtr.Zero;
        }

        // See DisplayChanged. WM_SETTINGCHANGE arrives for a great many things - wallpaper,
        // environment variables, colours - and only the work area is the dock's business.
        if (msg == Win32.WM_DISPLAYCHANGE ||
            (msg == Win32.WM_SETTINGCHANGE && (uint)(long)wParam == Win32.SPI_SETWORKAREA))
        {
            DisplayChanged?.Invoke();
            return IntPtr.Zero;
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = Win32.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            // Settings first, and separated: it is the item that opens something rather than
            // doing something, and it is where every new feature is going to end up living.
            Win32.AppendMenuW(menu, Win32.MF_STRING, new IntPtr(CmdMenu), "菜单…");
            Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, IntPtr.Zero, null);
            Win32.AppendMenuW(menu, Win32.MF_STRING | (DockVisible ? Win32.MF_CHECKED : 0),
                new IntPtr(CmdShowDock), "显示 Dock");
            Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, IntPtr.Zero, null);
            Win32.AppendMenuW(menu, Win32.MF_STRING, new IntPtr(CmdExit), "退出");

            // Both halves of Microsoft's tray-menu workaround. Without the foreground call the
            // menu will not close when the user clicks away from it; without the trailing post it
            // sticks around after a selection. They look like superstition and are not.
            Win32.SetForegroundWindow(_hwnd);
            Win32.GetCursorPos(out POINT cursor);

            int command = Win32.TrackPopupMenu(
                menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD,
                cursor.x, cursor.y, 0, _hwnd, IntPtr.Zero);

            Win32.PostMessageW(_hwnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CmdMenu: MenuRequested?.Invoke(); break;
                case CmdShowDock: Toggle(); break;
                case CmdExit: ExitRequested?.Invoke(); break;
            }
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    private void Toggle()
    {
        DockVisible = !DockVisible;
        VisibilityToggled?.Invoke(DockVisible);
    }

    public void Dispose()
    {
        if (_added)
        {
            NOTIFYICONDATAW data = Data();
            Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
