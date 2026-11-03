using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace Angelito.ScreenStudio.Desktop.Capture;

public sealed record WindowInfo(IntPtr Handle, string Title, string Process)
{
    public override string ToString() => $"{Title}  ({Process})";
}

/// <summary>
/// Captures one window with Windows.Graphics.Capture — only that window's pixels, even when other windows cover it —
/// and streams raw BGRA frames to an ffmpeg process. ffmpeg stamps frames with the wall clock and emits constant fps,
/// so the video stays in sync with audio even though WGC only delivers frames when the window changes.
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly byte[] _latest, _sending;
    private readonly AutoResetEvent _newFrame = new(false);
    private readonly Thread _writer;
    private Stream? _out;
    private SizeInt32 _poolSize;
    private volatile bool _running = true;
    private bool _haveFrame;
    public int Width { get; }
    public int Height { get; }
    /// <summary>Raised once, with the time the first frame was handed to ffmpeg (session clock).</summary>
    public event Action? FirstFrame;
    public event Action<string>? Lost;

    /// <param name="size">Output size; null = the window's current size. Later parts of a recording reuse the first part's size.</param>
    public WindowCapture(IntPtr hwnd, (int W, int H)? size = null)
    {
        _device = CreateDevice();
        _item = CreateItem(hwnd);
        // Fixed output size (even, for the encoders); if the window is resized while recording it is cropped/padded, not scaled.
        Width = size?.W ?? Math.Max(64, _item.Size.Width & ~1);
        Height = size?.H ?? Math.Max(64, _item.Size.Height & ~1);
        _latest = new byte[Width * Height * 4];
        _sending = new byte[_latest.Length];
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

    private void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame == null || !_running) return;
        if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
        {
            _poolSize = frame.ContentSize; // window resized: keep capturing at the new size
            pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        }
        try
        {
            using var bmp = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
            int bw = bmp.PixelWidth, bh = bmp.PixelHeight;
            var tmp = new byte[bw * bh * 4];
            bmp.CopyToBuffer(tmp.AsBuffer());
            lock (_latest)
            {
                Array.Clear(_latest);
                int rows = Math.Min(bh, Height), bytes = Math.Min(bw, Width) * 4;
                for (var y = 0; y < rows; y++) Buffer.BlockCopy(tmp, y * bw * 4, _latest, y * Width * 4, bytes);
                _haveFrame = true;
            }
            _newFrame.Set();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { } // transient (e.g. window minimizing)
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
            lock (_latest)
            {
                if (!_haveFrame) continue;
                Buffer.BlockCopy(_latest, 0, _sending, 0, _latest.Length);
            }
            try { _out!.Write(_sending); _out.Flush(); }
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

    public void Dispose() { if (_running) Stop(); _newFrame.Dispose(); }

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

    private static IDirect3DDevice CreateDevice()
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
                try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(dev); if (ctx != IntPtr.Zero) Marshal.Release(ctx); }
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
