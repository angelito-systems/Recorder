using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Angelito.ScreenStudio.Core;

/// <summary>
/// One captured cursor appearance: straight (non-premultiplied) BGRA frames of W×H stacked in the session's
/// cursors/{Id}.bgra, the hotspot (the pixel that clicks) and each animation frame's duration in ms.
/// Arrow marks the system arrow, which the "Custom" pointer mode replaces.
/// </summary>
public sealed record CursorShape(string Id, int W, int H, int HotX, int HotY, int[] FrameMs, bool Arrow = false)
{
    public int Frames => FrameMs.Length;
    public int CycleMs => Math.Max(1, FrameMs.Sum());

    /// <summary>Animation frame shown at session time t; frames advance on the output frame grid so preview and export agree.</summary>
    public int FrameAt(double t, int fps)
    {
        if (Frames <= 1) return 0;
        var ms = (long)Math.Floor(Math.Max(0, t) * fps + 1e-6) * 1000 / Math.Max(1, fps) % CycleMs;
        for (var i = 0; i < Frames; i++)
        {
            if (ms < FrameMs[i]) return i;
            ms -= FrameMs[i];
        }
        return Frames - 1;
    }
}

/// <summary>
/// Captures what Windows shows as the cursor, exactly: CopyIcon + GetIconInfo for size and hotspot, DrawIconEx for every
/// animation step (GetCursorFrameInfo gives the step count and rates). Each frame is drawn over black and over white:
/// the difference gives true per-pixel alpha, and pixels that come out lighter on black than on white are the
/// "invert screen" parts of monochrome cursors (the classic I-beam), kept as opaque black.
/// </summary>
public static partial class CursorCapture
{
    /// <summary>Captures a cursor handle, or null if Windows won't give its image (then the caller keeps the previous shape).</summary>
    public static (CursorShape Shape, byte[] Pixels)? Capture(IntPtr cursor, bool isArrow = false)
    {
        if (cursor == IntPtr.Zero || !OperatingSystem.IsWindows()) return null;
        var copy = CopyIcon(cursor); // a private copy can't be destroyed under us by the app that owns the cursor
        if (copy == IntPtr.Zero) return null;
        try
        {
            if (!GetIconInfo(copy, out var ii)) return null;
            int w, h;
            try
            {
                var bmp = ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask;
                if (GetObject(bmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
                (w, h) = (bm.bmWidth, ii.hbmColor != IntPtr.Zero ? bm.bmHeight : bm.bmHeight / 2); // mono: AND mask over XOR mask
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
            if (w <= 0 || h <= 0 || w > 1024 || h > 1024) return null;

            var rates = FrameRates(cursor);
            var pixels = new byte[w * h * 4 * rates.Length];
            for (var step = 0; step < rates.Length; step++)
                if (!Render(rates.Length > 1 ? cursor : copy, w, h, step, pixels.AsSpan(step * w * h * 4, w * h * 4))) return null;
            var id = Convert.ToHexString(SHA1.HashData([.. pixels, .. BitConverter.GetBytes(ii.xHotspot), .. BitConverter.GetBytes(ii.yHotspot), .. rates.SelectMany(BitConverter.GetBytes)]))[..16].ToLowerInvariant();
            return (new CursorShape(id, w, h, ii.xHotspot, ii.yHotspot, rates, isArrow), pixels);
        }
        finally { DestroyIcon(copy); }
    }

    /// <summary>Per-step durations in ms; one entry for a static cursor. Rates are in jiffies (1/60 s).</summary>
    private static int[] FrameRates(IntPtr cursor)
    {
        if (FrameInfo is not { } info) return [0];
        try
        {
            if (info(cursor, 0, 0, out var rate, out var steps) == IntPtr.Zero || steps <= 1) return [0];
            var r = new int[Math.Min((int)steps, 256)];
            for (var i = 0; i < r.Length; i++)
            {
                if (i > 0 && info(cursor, 0, (uint)i, out rate, out _) == IntPtr.Zero) rate = 0;
                r[i] = Math.Max(16, (int)Math.Round(rate * 1000 / 60.0));
            }
            return r;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or SEHException) { return [0]; }
    }

    private static bool Render(IntPtr cursor, int w, int h, int step, Span<byte> outBgra)
    {
        var black = DrawOver(cursor, w, h, step, 0x00);
        var white = DrawOver(cursor, w, h, step, 0xFF);
        if (black == null || white == null) return false;
        for (var i = 0; i < w * h * 4; i += 4)
        {
            int db = white[i] - black[i], dg = white[i + 1] - black[i + 1], dr = white[i + 2] - black[i + 2];
            if (db < 0 && dg < 0 && dr < 0) // brighter on black than on white: XOR/invert pixel
            {
                outBgra[i] = outBgra[i + 1] = outBgra[i + 2] = 0;
                outBgra[i + 3] = 255;
                continue;
            }
            var a = Math.Clamp(255 - (Math.Max(0, db) + Math.Max(0, dg) + Math.Max(0, dr)) / 3, 0, 255);
            outBgra[i + 3] = (byte)a;
            for (var c = 0; c < 3; c++) outBgra[i + c] = a == 0 ? (byte)0 : (byte)Math.Clamp(black[i + c] * 255 / a, 0, 255);
        }
        return true;
    }

    private static byte[]? DrawOver(IntPtr cursor, int w, int h, int step, byte bg)
    {
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(dc, ref bi, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { DeleteDC(dc); return null; }
        var old = SelectObject(dc, dib);
        try
        {
            var buf = new byte[w * h * 4];
            Array.Fill(buf, bg);
            Marshal.Copy(buf, 0, bits, buf.Length);
            if (!DrawIconEx(dc, 0, 0, cursor, w, h, (uint)step, IntPtr.Zero, 0x0003 /* DI_NORMAL */)) return null;
            GdiFlush();
            Marshal.Copy(bits, buf, 0, buf.Length);
            return buf;
        }
        finally { SelectObject(dc, old); DeleteObject(dib); DeleteDC(dc); }
    }

    /// <summary>The cursor Windows is showing now: handle and whether it is visible (hidden while typing, by games, or suppressed for pen/touch).</summary>
    public static (IntPtr Handle, bool Visible, int X, int Y) Current()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!OperatingSystem.IsWindows() || !GetCursorInfo(ref ci)) return (IntPtr.Zero, false, 0, 0);
        return (ci.hCursor, (ci.flags & 1) != 0 && (ci.flags & 2) == 0, ci.pt.X, ci.pt.Y); // CURSOR_SHOWING, not CURSOR_SUPPRESSED
    }

    /// <summary>The shared system arrow (for the current scheme and size).</summary>
    public static IntPtr SystemArrow => OperatingSystem.IsWindows() ? LoadCursor(IntPtr.Zero, 32512) : IntPtr.Zero;

    // GetCursorFrameInfo is exported by user32 but not declared in the SDK; resolve it at run time and degrade to one frame.
    private delegate IntPtr GetCursorFrameInfoFn(IntPtr hcur, uint reserved, uint step, out uint rateJiffies, out uint steps);
    private static readonly GetCursorFrameInfoFn? FrameInfo = ResolveFrameInfo();

    private static GetCursorFrameInfoFn? ResolveFrameInfo()
    {
        if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("user32.dll", out var lib)) return null;
        return NativeLibrary.TryGetExport(lib, "GetCursorFrameInfo", out var p) ? Marshal.GetDelegateForFunctionPointer<GetCursorFrameInfoFn>(p) : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr h, int w, int hgt, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr inst, int id);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}

/// <summary>
/// The session's cursor library (cursors/shapes.json + one .bgra per shape). Captures a handle once (cache by handle),
/// and stores each distinct appearance once (cache by content hash: a recreated handle with the same image reuses it).
/// </summary>
public sealed class CursorShapeStore
{
    public const string Folder = "cursors", Index = "shapes.json";
    private readonly string _dir;
    private readonly Dictionary<IntPtr, string?> _byHandle = [];
    private readonly Dictionary<string, CursorShape> _shapes = [];
    private readonly IntPtr _arrow = CursorCapture.SystemArrow;

    public CursorShapeStore(string sessionDir) => _dir = Path.Combine(sessionDir, Folder);

    public IReadOnlyDictionary<string, CursorShape> Shapes => _shapes;

    /// <summary>Shape id for a handle, capturing it the first time it is seen; null if it can't be captured.</summary>
    public string? Resolve(IntPtr handle)
    {
        if (_byHandle.TryGetValue(handle, out var known)) return known;
        string? id = null;
        if (CursorCapture.Capture(handle, handle == _arrow) is var (shape, px))
        {
            id = shape.Id;
            if (!_shapes.ContainsKey(id))
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllBytes(Path.Combine(_dir, id + ".bgra"), px);
                _shapes[id] = shape;
                AtomicFile.WriteAllText(Path.Combine(_dir, Index), JsonSerializer.Serialize(_shapes.Values, SessionManifest.Json));
            }
        }
        _byHandle[handle] = id;
        return id;
    }

    public static Dictionary<string, CursorShape> Load(string sessionDir)
    {
        var path = Path.Combine(sessionDir, Folder, Index);
        try
        {
            if (!File.Exists(path)) return [];
            return (JsonSerializer.Deserialize<List<CursorShape>>(File.ReadAllText(path), SessionManifest.Json) ?? []).ToDictionary(s => s.Id);
        }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }

    public static string PixelsPath(string sessionDir, string id) => Path.Combine(sessionDir, Folder, id + ".bgra");
}

/// <summary>"t id" lines: from t on, Windows shows shape id ("-" = hidden cursor, e.g. while typing or app-drawn).</summary>
public static class CursorShapeLog
{
    public const string Hidden = "-", Unknown = "?";
    public static string Format(double t, string id) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{t:F4} {id}");

    public static bool TryParse(string line, out double t, out string id)
    {
        (t, id) = (0, "");
        var i = line.IndexOf(' ');
        if (i <= 0 || !double.TryParse(line.AsSpan(0, i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t)) return false;
        id = line[(i + 1)..].Trim();
        return id.Length > 0;
    }
}
