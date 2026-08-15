using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace FluidDock.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct DispatcherQueueOptions
{
    public int dwSize;
    public int threadType;
    public int apartmentType;
}

/// <summary>
/// Bridges the Win32 HWND world to Windows.UI.Composition.
///
/// We call ICompositorDesktopInterop through its raw vtable rather than declaring a
/// [ComImport] interface. The vtable layout is fixed by COM, so this sidesteps any
/// question of how built-in COM marshalling behaves under modern .NET / CsWinRT.
/// </summary>
internal static unsafe class CompositorInterop
{
    // ICompositorDesktopInterop
    private static readonly Guid IID_ICompositorDesktopInterop = new("29E691FA-4567-4DCA-B319-D0F207EB6807");

    private const int DQTYPE_THREAD_CURRENT = 2;
    private const int DQTAT_COM_NONE = 0;

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    /// <summary>
    /// Windows.UI.Composition requires a DispatcherQueue on the calling thread.
    /// This MUST be called before constructing a Compositor, or the ctor throws.
    /// </summary>
    public static IntPtr EnsureDispatcherQueueOnCurrentThread()
    {
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = DQTYPE_THREAD_CURRENT,
            apartmentType = DQTAT_COM_NONE,
        };

        int hr = CreateDispatcherQueueController(options, out IntPtr controller);
        Marshal.ThrowExceptionForHR(hr);
        return controller;
    }

    // ICompositorInterop - lets us hand Composition a Direct2D device so it can hold surfaces we draw into.
    private static readonly Guid IID_ICompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");

    // ICompositionDrawingSurfaceInterop - BeginDraw/EndDraw around a CompositionDrawingSurface.
    private static readonly Guid IID_ICompositionDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");

    // ID2D1DeviceContext, the object we ask BeginDraw to hand back.
    public static readonly Guid IID_ID2D1DeviceContext = new("E8F7FE7A-191C-466D-AD95-975678BDA998");

    /// <summary>
    /// Wraps ICompositorInterop::CreateGraphicsDevice (vtable slot 5).
    /// </summary>
    public static CompositionGraphicsDevice CreateGraphicsDevice(Compositor compositor, IntPtr d2dDevice)
    {
        IntPtr inspectable = MarshalInspectable<Compositor>.FromManaged(compositor);
        try
        {
            Guid iid = IID_ICompositorInterop;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in iid, out IntPtr interop));

            try
            {
                // 3=CreateCompositionSurfaceForHandle 4=...ForSwapChain 5=CreateGraphicsDevice
                void** vtbl = *(void***)interop;
                var createGraphicsDevice =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtbl[5];

                IntPtr deviceAbi;
                Marshal.ThrowExceptionForHR(createGraphicsDevice(interop, d2dDevice, &deviceAbi));

                try { return MarshalInterface<CompositionGraphicsDevice>.FromAbi(deviceAbi); }
                finally { Marshal.Release(deviceAbi); }
            }
            finally { Marshal.Release(interop); }
        }
        finally { Marshal.Release(inspectable); }
    }

    /// <summary>
    /// Wraps ICompositionDrawingSurfaceInterop::BeginDraw (slot 3). The returned offset matters:
    /// Composition atlases surfaces, so drawing must be translated by it rather than starting at 0,0.
    /// </summary>
    public static IntPtr BeginDraw(CompositionDrawingSurface surface, out POINT offset)
    {
        IntPtr inspectable = MarshalInspectable<CompositionDrawingSurface>.FromManaged(surface);
        try
        {
            Guid iid = IID_ICompositionDrawingSurfaceInterop;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in iid, out IntPtr interop));

            try
            {
                void** vtbl = *(void***)interop;
                var beginDraw =
                    (delegate* unmanaged[Stdcall]<IntPtr, RECT*, Guid*, IntPtr*, POINT*, int>)vtbl[3];

                Guid contextIid = IID_ID2D1DeviceContext;
                IntPtr contextPtr;
                POINT localOffset;
                Marshal.ThrowExceptionForHR(beginDraw(interop, null, &contextIid, &contextPtr, &localOffset));

                offset = localOffset;
                return contextPtr;
            }
            finally { Marshal.Release(interop); }
        }
        finally { Marshal.Release(inspectable); }
    }

    /// <summary>Wraps ICompositionDrawingSurfaceInterop::EndDraw (slot 4).</summary>
    public static void EndDraw(CompositionDrawingSurface surface)
    {
        IntPtr inspectable = MarshalInspectable<CompositionDrawingSurface>.FromManaged(surface);
        try
        {
            Guid iid = IID_ICompositionDrawingSurfaceInterop;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in iid, out IntPtr interop));

            try
            {
                void** vtbl = *(void***)interop;
                var endDraw = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[4];
                Marshal.ThrowExceptionForHR(endDraw(interop));
            }
            finally { Marshal.Release(interop); }
        }
        finally { Marshal.Release(inspectable); }
    }

    public static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, IntPtr hwnd, bool isTopmost)
    {
        IntPtr inspectable = MarshalInspectable<Compositor>.FromManaged(compositor);
        try
        {
            Guid iid = IID_ICompositorDesktopInterop;
            int hr = Marshal.QueryInterface(inspectable, in iid, out IntPtr interop);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                // vtable: 0=QueryInterface 1=AddRef 2=Release 3=CreateDesktopWindowTarget 4=EnsureOnThread
                void** vtbl = *(void***)interop;
                var createDesktopWindowTarget =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)vtbl[3];

                IntPtr targetAbi;
                hr = createDesktopWindowTarget(interop, hwnd, isTopmost ? 1 : 0, &targetAbi);
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    return MarshalInterface<DesktopWindowTarget>.FromAbi(targetAbi);
                }
                finally
                {
                    Marshal.Release(targetAbi);
                }
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }
}
