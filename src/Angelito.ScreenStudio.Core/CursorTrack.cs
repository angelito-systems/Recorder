namespace Angelito.ScreenStudio.Core;

/// <summary>
/// Original: the real cursor where it really was. Cinematic: the real cursor on a smoothed path that still lands exactly
/// on every click. Custom: the user's image instead of the arrow (other shapes — text, hand, resize… — stay real).
/// </summary>
public enum PointerMode { Original, Cinematic, Custom }

/// <summary>
/// The drawn pointer (recordings with an editable cursor). Scale multiplies the cursor's real size; Smoothing 0..1;
/// HideIdle fades the pointer out after it rests. CustomImage is a PNG in the session folder, hotspot at CustomHotX/Y px.
/// </summary>
public sealed record PointerStyle(bool Visible = true, PointerMode Mode = PointerMode.Cinematic, double Scale = 1.0, double Smoothing = 0.5,
    double Opacity = 1.0, bool HideIdle = false, string? CustomImage = null, int CustomHotX = 0, int CustomHotY = 0);

/// <summary>What to draw at one moment: the shape (null = nothing), its frame, top-left in source pixels, size factor, alpha.</summary>
public readonly record struct PointerState(string? ShapeId, int Frame, double X, double Y, double Scale, double Alpha);

/// <summary>
/// The pointer over time, computed once per project and shared by the preview and the exporter, so both draw the same
/// cursor, path, animation frame and fade. Positions are sampled on a fixed grid (no state accumulates over long
/// projects); smoothing is a centered Gaussian whose error is pulled back to zero around each click.
/// </summary>
public sealed class CursorTrack
{
    public const int Rate = 120;           // path samples per second
    public const double IdleAfter = 1.5, IdleFade = 0.3;
    public const string CustomShapeId = "custom";

    private readonly float[] _x, _y;
    private readonly double[] _shapeT;
    private readonly string?[] _shapeId;
    private readonly double[] _moveT;      // last real movement at or before each sample (for HideIdle)
    private readonly IReadOnlyDictionary<string, CursorShape> _shapes;
    private readonly PointerStyle _style;
    private readonly double _baseScale;
    private readonly CursorShape? _custom;

    public CursorTrack(IReadOnlyList<CursorEvent> events, IReadOnlyList<(double T, string Id)> shapeLog, IReadOnlyDictionary<string, CursorShape> shapes,
        PointerStyle style, double duration, double dpiScale = 1, (int W, int H)? customSize = null)
    {
        _shapes = shapes;
        _style = style;
        _baseScale = dpiScale;
        if (style.Mode == PointerMode.Custom && customSize is { } cs)
            _custom = new CursorShape(CustomShapeId, cs.W, cs.H, style.CustomHotX, style.CustomHotY, [0]);

        var n = (int)Math.Ceiling(Math.Max(0, duration) * Rate) + 2;
        _x = new float[n]; _y = new float[n]; _moveT = new double[n];
        var ev = events.Where(e => e.T >= 0).OrderBy(e => e.T).ToList();
        int j = 0; double px = ev.Count > 0 ? ev[0].X : 0, py = ev.Count > 0 ? ev[0].Y : 0, lastMove = ev.Count > 0 ? ev[0].T : double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)Rate;
            while (j < ev.Count && ev[j].T <= t)
            {
                if (ev[j].X != px || ev[j].Y != py) lastMove = ev[j].T;
                (px, py) = (ev[j].X, ev[j].Y);
                j++;
            }
            (_x[i], _y[i], _moveT[i]) = ((float)px, (float)py, lastMove);
        }
        if (style.Mode == PointerMode.Cinematic && style.Smoothing > 0)
        {
            Smooth(_x, style.Smoothing); Smooth(_y, style.Smoothing);
            PinClicks(ev.Where(e => e.K != CursorKind.Move).ToList(), events);
        }

        var log = shapeLog.OrderBy(s => s.T).ToList();
        _shapeT = log.Select(s => s.T).ToArray();
        // Hidden → draw nothing (an app drawing its own pointer has it in the video already: no duplicate).
        // Not capturable ("?" or missing file) → keep the last real shape rather than inventing a stand-in arrow.
        _shapeId = new string?[log.Count];
        string? prev = null;
        for (var i = 0; i < log.Count; i++)
            _shapeId[i] = prev = log[i].Id == CursorShapeLog.Hidden ? null : shapes.ContainsKey(log[i].Id) ? log[i].Id : prev;
    }

    /// <summary>
    /// The track for a session, or null when the cursor is part of the screen video (older recordings, or recorded with
    /// the editable cursor off): that cursor is kept exactly as recorded and nothing is drawn over it.
    /// </summary>
    public static CursorTrack? ForSession(StudioProject p, SessionManifest m, string sessionDir, IReadOnlyList<CursorEvent> cursor, double duration)
    {
        if (m.CursorInVideo) return null;
        (int, int)? custom = null;
        if (p.Pointer.Mode == PointerMode.Custom && p.Pointer.CustomImage is { } img && PngSize(Path.Combine(sessionDir, img)) is { } sz) custom = sz;
        return new CursorTrack(cursor, m.LoadCursorShapes(sessionDir), CursorShapeStore.Load(sessionDir), p.Pointer, duration, m.CursorScale, custom);
    }

    /// <summary>Width and height from a PNG's header, or null if it isn't a readable PNG.</summary>
    public static (int W, int H)? PngSize(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var b = new byte[24];
            if (f.Read(b, 0, 24) < 24 || b[1] != 'P' || b[2] != 'N' || b[3] != 'G') return null;
            static int Be(byte[] a, int o) => a[o] << 24 | a[o + 1] << 16 | a[o + 2] << 8 | a[o + 3];
            return (Be(b, 16), Be(b, 20));
        }
        catch (IOException) { return null; }
    }

    /// <summary>Gaussian blur of a path; sigma up to 0.12 s. Edges are clamped, so the path never drifts at the ends.</summary>
    private static void Smooth(float[] v, double amount)
    {
        var sigma = Math.Clamp(amount, 0, 1) * 0.12 * Rate;
        if (sigma < 0.5) return;
        var r = (int)Math.Ceiling(sigma * 3);
        var k = Enumerable.Range(-r, 2 * r + 1).Select(d => Math.Exp(-d * d / (2 * sigma * sigma))).ToArray();
        var src = (float[])v.Clone();
        for (var i = 0; i < v.Length; i++)
        {
            double s = 0, w = 0;
            for (var d = -r; d <= r; d++) { var idx = Math.Clamp(i + d, 0, v.Length - 1); s += src[idx] * k[d + r]; w += k[d + r]; }
            v[i] = (float)(s / w);
        }
    }

    /// <summary>At a click the smoothed pointer must be exactly where the click happened: blend the error out over ±0.15 s.</summary>
    private void PinClicks(List<CursorEvent> clicks, IReadOnlyList<CursorEvent> _)
    {
        const double Reach = 0.15;
        foreach (var c in clicks)
        {
            var ci = (int)Math.Round(c.T * Rate);
            if (ci < 0 || ci >= _x.Length) continue;
            double ex = c.X - _x[ci], ey = c.Y - _y[ci];
            var r = (int)(Reach * Rate);
            for (var i = Math.Max(0, ci - r); i <= Math.Min(_x.Length - 1, ci + r); i++)
            {
                var u = Math.Abs(i - ci) / (double)r;
                var w = 1 - u * u * (3 - 2 * u); // 1 at the click, 0 at ±Reach
                _x[i] += (float)(ex * w);
                _y[i] += (float)(ey * w);
            }
        }
    }

    /// <summary>Hotspot position (source pixels) of the drawn pointer at time t.</summary>
    public (double X, double Y) Position(double t)
    {
        var f = Math.Clamp(t * Rate, 0, _x.Length - 1);
        var i = (int)f; var u = f - i; var k = Math.Min(i + 1, _x.Length - 1);
        return (_x[i] + (_x[k] - _x[i]) * u, _y[i] + (_y[k] - _y[i]) * u);
    }

    /// <summary>The shape Windows showed at t (null: hidden, or a cursor that couldn't be captured).</summary>
    public string? ShapeAt(double t)
    {
        var i = Array.BinarySearch(_shapeT, t);
        if (i < 0) i = ~i - 1;
        return i < 0 ? null : _shapeId[i];
    }

    public CursorShape? Shape(string? id) => id == null ? null : id == CustomShapeId ? _custom : _shapes.GetValueOrDefault(id);

    /// <summary>Everything needed to draw the pointer at t on an fps grid (fps picks the animation frame).</summary>
    public PointerState At(double t, int fps)
    {
        if (!_style.Visible) return default;
        var id = ShapeAt(t);
        var shape = Shape(id);
        if (shape == null) return default;
        if (_custom != null && shape.Arrow) (id, shape) = (CustomShapeId, _custom);
        var alpha = Math.Clamp(_style.Opacity, 0, 1);
        if (_style.HideIdle)
        {
            var since = t - _moveT[(int)Math.Clamp(t * Rate, 0, _moveT.Length - 1)];
            alpha *= since <= IdleAfter ? 1 : Math.Clamp(1 - (since - IdleAfter) / IdleFade, 0, 1);
        }
        if (alpha <= 0) return default;
        var s = _baseScale * (_style.Mode == PointerMode.Original ? 1 : Math.Clamp(_style.Scale, 0.25, 4));
        var (x, y) = Position(t);
        return new PointerState(id, shape.FrameAt(t, fps), x - shape.HotX * s, y - shape.HotY * s, s, alpha);
    }
}
