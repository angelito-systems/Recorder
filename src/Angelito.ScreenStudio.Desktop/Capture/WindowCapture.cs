using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Angelito.ScreenStudio.Desktop.Capture;

public sealed record WindowInfo(IntPtr Handle, string Title, string Process)
{
    public override string ToString() => $"{Title}  ({Process})";
}

/// <summary>
/// Captures one window with Windows.Graphics.Capture — only that window's pixels, even when other windows cover it —
/// and streams raw BGRA frames to an ffmpeg process. ffmpeg stamps frames with the wall clock and emits constant fps,
/// so the video stays in sync with audio even though WGC only delivers frames when the window changes.
/// Per frame there is one GPU copy into a staging texture and one CPU copy into a rotating buffer — no allocations.
/// </summary>
internal sealed unsafe class WindowCapture : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _device;
    private readonly IntPtr _d3d, _ctx; // ID3D11Device / immediate ID3D11DeviceContext (used only on the frame thread)
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private byte[] _back, _ready, _send; // triple buffer: frame thread fills back, writer sends send, ready is the newest
    private readonly object _swap = new();
    private bool _fresh, _any;
    private readonly AutoResetEvent _newFrame = new(false);
    private readonly Thread _writer;
    private Stream? _out;
    private SizeInt32 _poolSize;
    private IntPtr _staging;
    private int _stagingW, _stagingH;
    private volatile bool _running = true;
    public int Width { get; }
    public int Height { get; }
    /// <summary>Raised once, when the first frame was handed to ffmpeg.</summary>
    public event Action? FirstFrame;
    public event Action<string>? Lost;

    /// <param name="size">Output size; null = the window's current size. Later parts of a recording reuse the first part's size.</param>
    public WindowCapture(IntPtr hwnd, (int W, int H)? size = null)
    {
        (_device, _d3d, _ctx) = CreateDevice();
        _item = CreateItem(hwnd);
        // Fixed output size (even, for the encoders); if the window is resized while recording it is cropped/padded, not scaled.
        Width = size?.W ?? Math.Max(64, _item.Size.Width & ~1);
        Height = size?.H ?? Math.Max(64, _item.Size.Height & ~1);
        _back = new byte[Width * Height * 4];
        _ready = new byte[_back.Length];
        _send = new byte[_back.Length];
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrame;
        _item.Closed += (_, _) => Lost?.Invoke("La ventana que se estaba grabando se cerró.");
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        // Windows 11 can hide the yellow capture border; Windows 10 always shows it.
        if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            try { _session.IsBorderRequired = false; } catch (UnauthorizedAccessException) { }
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "window-capture-writer" };
    }

    /// <summary>Starts feeding frames to <paramref name="ffmpegInput"/>.</summary>
    public void Start(Stream ffmpegInput)
    {
        _out = ffmpegInput;
        _session.StartCapture();
        _writer.Start();
    }

    private bool _reported;

    /// <summary>WinRT drops exceptions thrown from FrameArrived silently; report the first one so a broken capture is visible.</summary>
    private void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        try { CopyFrame(pool); }
        catch (Exception e) when (!_reported) { _reported = true; Lost?.Invoke("Error al copiar la ventana: " + e.Message); App.Log("window capture: " + e); }
    }

    private void CopyFrame(Direct3D11CaptureFramePool pool)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame == null || !_running) return;
        var content = frame.ContentSize;
        if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
        {
            _poolSize = content; // window resized: keep capturing at the new size
            pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            return; // this frame still has the old size
        }
        var tex = WinRT.CastExtensions.As<IDirect3DDxgiInterfaceAccess>(frame.Surface).GetInterface(IID_ID3D11Texture2D);
        try
        {
            if (!EnsureStaging(content.Width, content.Height)) return;
            CopyResource(_ctx, _staging, tex);
            D3D11_MAPPED_SUBRESOURCE map;
            if (Map(_ctx, _staging, &map) < 0) return;
            try
            {
                int rows = Math.Min(content.Height, Height), bytes = Math.Min(content.Width, Width) * 4;
                if (rows < Height || bytes < Width * 4) Array.Clear(_back); // smaller window: pad with black
                fixed (byte* dst = _back)
                    for (var y = 0; y < rows; y++)
                        Buffer.MemoryCopy((byte*)map.pData + (long)y * map.RowPitch, dst + (long)y * Width * 4, bytes, bytes);
            }
            finally { Unmap(_ctx, _staging); }
            lock (_swap) { (_back, _ready) = (_ready, _back); _fresh = true; }
            _newFrame.Set();
        }
        catch (COMException) { } // transient, e.g. device lost while the window minimizes
        finally { Marshal.Release(tex); }
    }

    /// <summary>
    /// Sends the newest frame whenever one arrives, and repeats it at least every 100 ms so ffmpeg's wall-clock timing
    /// sees a live stream while the window is static.
    /// </summary>
    private void WriteLoop()
    {
        var first = true;
        while (_running)
        {
            _newFrame.WaitOne(100);
            if (!_running) break;
            lock (_swap)
            {
                if (_fresh) { (_ready, _send) = (_send, _ready); _fresh = false; _any = true; }
                if (!_any) continue;
            }
            try { _out!.Write(_send); _out.Flush(); }
            catch (IOException) { Lost?.Invoke("El codificador dejó de recibir la ventana."); break; }
            if (first) { first = false; FirstFrame?.Invoke(); }
        }
    }

    public void Stop()
    {
        _running = false;
        _newFrame.Set();
        if (_writer.IsAlive) _writer.Join(2000);
        _session.Dispose();
        _pool.Dispose();
    }

    public void Dispose()
    {
        if (_running) Stop();
        _newFrame.Dispose();
        if (_staging != IntPtr.Zero) Marshal.Release(_staging);
        Marshal.Release(_ctx);
        Marshal.Release(_d3d);
    }

    // ---------- D3D11 through vtables (only the four calls needed; no extra dependency) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE { public void* pData; public uint RowPitch, DepthPitch; }

    private bool EnsureStaging(int w, int h)
    {
        if (_staging != IntPtr.Zero && _stagingW == w && _stagingH == h) return true;
        if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = 87 /*B8G8R8A8_UNORM*/, SampleCount = 1,
            Usage = 3 /*STAGING*/, CPUAccessFlags = 0x20000 /*READ*/,
        };
        IntPtr tex;
        var create = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)(*(void***)_d3d)[5]; // CreateTexture2D
        if (create(_d3d, &desc, IntPtr.Zero, &tex) < 0) return false;
        (_staging, _stagingW, _stagingH) = (tex, w, h);
        return true;
    }

    private static void CopyResource(IntPtr ctx, IntPtr dst, IntPtr src) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)(*(void***)ctx)[47])(ctx, dst, src);

    private static int Map(IntPtr ctx, IntPtr res, D3D11_MAPPED_SUBRESOURCE* map) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)(*(void***)ctx)[14])(ctx, res, 0, 1 /*READ*/, 0, map);

    private static void Unmap(IntPtr ctx, IntPtr res) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)(*(void***)ctx)[15])(ctx, res, 0);

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface(in Guid iid); }

    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    // ---------- interop ----------

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, in Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
    }

    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var ptr = interop.CreateForWindow(hwnd, IID_IGraphicsCaptureItem);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels,
        uint sdkVersion, out IntPtr device, out int level, out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static (IDirect3DDevice Device, IntPtr D3D, IntPtr Context) CreateDevice()
    {
        // Hardware device with BGRA support; WARP as a fallback for machines without a usable GPU driver.
        var hr = D3D11CreateDevice(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out var dev, out _, out var ctx);
        if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, 5 /*WARP*/, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out dev, out _, out ctx);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var iid = IID_IDXGIDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(dev, in iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try { return (WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable), dev, ctx); } // dev/ctx are kept for frame copies
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        catch { Marshal.Release(dev); if (ctx != IntPtr.Zero) Marshal.Release(ctx); throw; }
    }

    // ---------- window list ----------

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr data);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Native.RECT value, int size);

    /// <summary>Top-level windows a user would pick: visible, titled, not tool windows, not cloaked (other virtual desktops/UWP shells).</summary>
    public static List<WindowInfo> List()
    {
        var own = Environment.ProcessId;
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
            if ((GetWindowLong(h, -20 /*GWL_EXSTYLE*/) & 0x80 /*WS_EX_TOOLWINDOW*/) != 0) return true;
            if (DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var sb = new System.Text.StringBuilder(256);
            if (GetWindowText(h, sb, sb.Capacity) == 0) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == own) return true;
            string proc;
            try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch (ArgumentException) { return true; }
            list.Add(new WindowInfo(h, sb.ToString(), proc));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The window's visible bounds on the desktop (what WGC captures), in physical pixels.</summary>
    public static (int X, int Y) Origin(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) == 0 ? (r.Left, r.Top) : (0, 0);
}
