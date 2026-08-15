using System.Drawing;
using System.Drawing.Imaging;
using FluidDock.Native;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;

namespace FluidDock.Graphics;

/// <summary>
/// Turns bitmaps into Composition surfaces.
///
/// Composition cannot load an image by itself in a desktop app - LoadedImageSurface lives in
/// Windows.UI.Xaml.Media, which the desktop projection does not have - so we stand up a
/// Direct3D11 -> Direct2D device, hand it to the Compositor, and draw into the surfaces it
/// gives back. This runs once per image at load time; nothing here touches a frame.
/// </summary>
internal sealed class SurfaceFactory : IDisposable
{
    private readonly ID3D11Device _d3dDevice;
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID2D1Device _d2dDevice;

    public CompositionGraphicsDevice GraphicsDevice { get; }

    /// <summary>Which GPU the device landed on. Logged at startup; see the note in the constructor.</summary>
    public string AdapterName { get; private set; } = "(default adapter)";

    public SurfaceFactory(Compositor compositor)
    {
        using IDXGIAdapter1? adapter = PickLowPowerAdapter();
        if (adapter is not null)
            AdapterName = adapter.Description1.Description;

        // BgraSupport is required for Direct2D interop.
        // DriverType must be Unknown whenever an explicit adapter is passed - pairing an
        // adapter with DriverType.Hardware fails with E_INVALIDARG.
        D3D11.D3D11CreateDevice(
            adapter,
            adapter is null ? DriverType.Hardware : DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            null,
            out _d3dDevice!).CheckError();

        using IDXGIDevice dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);

        GraphicsDevice = CompositorInterop.CreateGraphicsDevice(compositor, _d2dDevice.NativePointer);
    }

    /// <summary>
    /// Picks the integrated GPU on a hybrid machine, falling back to the system default.
    ///
    /// This matters more than it looks. The device here only rasterises icons at startup, but
    /// it stays alive for the process lifetime because the CompositionGraphicsDevice needs it -
    /// and a live D3D device pins its adapter awake. On this laptop the default adapter is the
    /// RTX 5070 Ti, so an always-running dock was holding the discrete GPU up for no work at
    /// all. Verified against the GPU Engine perf counters: the process's LUID moves from the
    /// NVIDIA adapter to the AMD 610M.
    /// </summary>
    private static IDXGIAdapter1? PickLowPowerAdapter()
    {
        try
        {
            // IDXGIFactory6 is Win10 1803+. Older builds fall through to the default adapter.
            using IDXGIFactory6 factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            IDXGIAdapter1 candidate = factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(0, GpuPreference.MinimumPower);

            // A software adapter would be a large step backwards; better the default GPU.
            if ((candidate.Description1.Flags & AdapterFlags.Software) != 0)
            {
                candidate.Dispose();
                return null;
            }

            return candidate;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Draws a bitmap into a surface of the same size.</summary>
    public CompositionDrawingSurface CreateSurface(Bitmap source) =>
        CreateSurface(source, source.Width, source.Height);

    /// <summary>
    /// Draws a bitmap into a new CompositionDrawingSurface, resampling to the requested size.
    /// GDI+ does the resample and the straight -> premultiplied alpha conversion, because
    /// Format32bppPArgb is bit-for-bit what Direct2D calls B8G8R8A8 / Premultiplied.
    /// </summary>
    public CompositionDrawingSurface CreateSurface(Bitmap source, int width, int height)
    {
        using var normalised = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = System.Drawing.Graphics.FromImage(normalised))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        CompositionDrawingSurface surface = GraphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(width, height),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        BitmapData data = normalised.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);

        try
        {
            IntPtr contextPtr = CompositorInterop.BeginDraw(surface, out POINT offset);
            var context = new ID2D1DeviceContext(contextPtr);

            try
            {
                context.Clear(new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));

                var properties = new BitmapProperties(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));

                using ID2D1Bitmap bitmap = context.CreateBitmap(
                    new Vortice.Mathematics.SizeI(width, height), data.Scan0, (uint)data.Stride, properties);

                // Surfaces are atlased, so honour the offset BeginDraw handed back.
                // SourceCopy rather than SourceOver: the pixels are already premultiplied and
                // the target was just cleared, so blending would only cost precision.
                context.DrawImage(
                    bitmap,
                    new System.Numerics.Vector2(offset.x, offset.y),
                    Vortice.Direct2D1.InterpolationMode.Linear,
                    Vortice.Direct2D1.CompositeMode.SourceCopy);
            }
            finally
            {
                context.Dispose();
                CompositorInterop.EndDraw(surface);
            }
        }
        finally
        {
            normalised.UnlockBits(data);
        }

        return surface;
    }

    public void Dispose()
    {
        _d2dDevice.Dispose();
        _d2dFactory.Dispose();
        _d3dDevice.Dispose();
    }
}
