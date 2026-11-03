using System.Globalization;
using System.Text;

namespace Angelito.ScreenStudio.Core;

/// <summary>
/// The single definition of the camera over time: the canvas window (aspect + framing center) moved by zoom segments.
/// The editor preview calls <see cref="ViewRect"/>; the exporter emits <see cref="FfmpegViewExpr"/>, a literal
/// translation of the same formula, so preview and render agree (checked by a pixel test).
/// </summary>
public static class ZoomMath
{
    /// <summary>Ease-in and ease-out times, shrunk together when the segment is too short to fit both.</summary>
    public static (double In, double Out) Ramps(ZoomSegment z)
    {
        double i = Math.Max(0.001, z.Ramp), o = Math.Max(0.001, z.RampOut < 0 ? z.Ramp : z.RampOut);
        var k = Math.Min(1, Math.Max(0, z.End - z.Start) / (i + o));
        return (Math.Max(0.001, i * k), Math.Max(0.001, o * k));
    }

    /// <summary>Smoothstep weight 0..1 of one segment at time t.</summary>
    public static double Weight(ZoomSegment z, double t)
    {
        var (ri, ro) = Ramps(z);
        var a = Math.Min(Math.Clamp((t - z.Start) / ri, 0, 1), Math.Clamp((z.End - t) / ro, 0, 1));
        return a * a * (3 - 2 * a);
    }

    /// <summary>
    /// Zoom factor (≥1) and normalized center at time t; the center rests at (bx, by) outside zooms.
    /// Overlapping segments are averaged by weight, never added, so stacked zooms can't compound the scale. A segment
    /// easing out while the next eases in over the same span has weights summing to exactly 1 (smoothstep is
    /// symmetric): the scale holds and only the center glides — that is how auto-zoom pans between regions.
    /// </summary>
    public static (double Zoom, double Cx, double Cy) Evaluate(IReadOnlyList<ZoomSegment> zooms, double t, double bx = 0.5, double by = 0.5)
    {
        double sw = 0, z = 0, cx = 0, cy = 0;
        foreach (var s in zooms)
        {
            var w = Weight(s, t);
            if (w <= 0) continue;
            sw += w;
            z += (s.Scale - 1) * w;
            cx += (s.X - bx) * w;
            cy += (s.Y - by) * w;
        }
        var n = Math.Max(1, sw);
        return (Math.Max(1, 1 + z / n), bx + cx / n, by + cy / n);
    }

    /// <summary>Visible source rectangle in pixels (canvas aspect), clamped so the view never leaves the frame.</summary>
    public static (double X, double Y, double W, double H) ViewRect(IReadOnlyList<ZoomSegment> zooms, double t, double width, double height, ProjectCanvas? canvas = null)
    {
        canvas ??= new ProjectCanvas();
        var (cw, ch) = canvas.Window(width, height);
        var (z, cx, cy) = Evaluate(zooms, t, canvas.X, canvas.Y);
        double w = cw / z, h = ch / z;
        return (Math.Clamp(cx * width - w / 2, 0, width - w), Math.Clamp(cy * height - h / 2, 0, height - h), w, h);
    }

    /// <summary>
    /// ffmpeg expressions for the view rectangle (x, y, w, h) per frame, for the perspective filter, whose only clock
    /// is "in" — the 1-based input frame number — so time is (in-1)/fps on the constant-rate stream it receives.
    /// </summary>
    /// <param name="frameVar">The filter's frame-number variable: perspective counts from 1 ("in-1"), libplacebo from 0 ("n").</param>
    public static (string X, string Y, string W, string H) FfmpegViewExpr(IReadOnlyList<ZoomSegment> zooms, ProjectCanvas canvas, int width, int height, int fps, string frameVar = "in-1")
    {
        var (cw, ch) = canvas.Window(width, height);
        var t = F($"(({frameVar})/{fps})");
        var ws = new List<string>();
        var z = new StringBuilder("0");
        var cx = new StringBuilder("0");
        var cy = new StringBuilder("0");
        foreach (var s in zooms)
        {
            var (ri, ro) = Ramps(s);
            var a = F($"min(clip(({t}-{s.Start})/{ri},0,1),clip(({s.End}-{t})/{ro},0,1))");
            var w = $"({a})*({a})*(3-2*({a}))";
            ws.Add(w);
            z.Append(F($"+{s.Scale - 1}*{w}"));
            cx.Append(F($"+{s.X - canvas.X}*{w}"));
            cy.Append(F($"+{s.Y - canvas.Y}*{w}"));
        }
        // Same weighted average as Evaluate.
        var n = ws.Count == 0 ? "1" : $"max(1,{string.Join("+", ws)})";
        z.Insert(0, "1+(").Append($")/{n}");
        cx.Insert(0, F($"{canvas.X}+(")).Append($")/{n}");
        cy.Insert(0, F($"{canvas.Y}+(")).Append($")/{n}");
        var vw = F($"({cw}/max(1,{z}))");
        var vh = F($"({ch}/max(1,{z}))");
        return (F($"clip(({cx})*{width}-{vw}/2,0,{width}-{vw})"), F($"clip(({cy})*{height}-{vh}/2,0,{height}-{vh})"), vw, vh);
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
/// <summary>Where the auto-zoom camera is between two clicks; the generator walks clicks through these states.</summary>
public enum ZoomState { Idle, ZoomingIn, Focused, Tracking, ZoomingOut }

/// <summary>
/// Cinematic auto-zoom from clicks (mouse moves alone never zoom). A click zooms in so the camera arrives as it happens;
/// it stays while clicks keep landing inside the visible area, pans to a click outside it (Follow), and zooms back to
/// 100% after <see cref="AutoZoomSettings.IdleReturn"/> without clicks — never sooner than MinHold at full zoom.
/// Because the whole click log is known after recording, a click during a zoom-out simply cancels that zoom-out.
/// Output is ordinary editable segments (Auto = true), never baked into the video.
/// </summary>
public static class AutoZoom
{
    /// <summary>A click inside this fraction of the zoomed view keeps the current focus.</summary>
    public const double KeepInside = 0.8;

    public static List<ZoomSegment> Propose(IReadOnlyList<CursorEvent> events, int width, int height, double duration, AutoZoomSettings? settings = null)
    {
        var s = settings ?? new AutoZoomSettings();
        var result = new List<ZoomSegment>();
        if (!s.Enabled || width <= 0 || height <= 0) return result;
        var clicks = events.Where(e => e.K != CursorKind.Move && e.T >= 0 && e.T <= duration).OrderBy(e => e.T);

        ZoomSegment? cur = null;   // the focus being held (state Focused, or ZoomingIn/Tracking at its start)
        double last = 0;           // last click inside cur
        double OutStart(ZoomSegment c) => Math.Max(last + s.IdleReturn, c.Start + c.Ramp + s.MinHold);
        void Finish(ZoomSegment c, double end, double rampOut)
        {
            end = Math.Min(duration, end);
            if (end - c.Start > 0.2) result.Add(c with { End = end, RampOut = rampOut });
        }

        foreach (var c in clicks)
        {
            double x = Math.Clamp((double)c.X / width, 0, 1), y = Math.Clamp((double)c.Y / height, 0, 1);
            if (cur != null)
            {
                var outStart = OutStart(cur);
                if (c.T - s.ZoomIn < outStart + s.ZoomOut) // still zoomed (or zooming out): don't stack another zoom
                {
                    var half = 0.5 / Math.Max(1, cur.Scale) * KeepInside;
                    if (!s.Follow || (Math.Abs(x - cur.X) <= half && Math.Abs(y - cur.Y) <= half))
                    {
                        last = c.T; // same region: keep focus, push the return back
                        continue;
                    }
                    // Tracking: cur eases out exactly while the next eases in, so the scale holds and the center glides.
                    var d = s.ZoomIn;
                    var ts = Math.Max(c.T - d, cur.Start + cur.Ramp);
                    Finish(cur, ts + d, d);
                    cur = new ZoomSegment(ts, ts + d, s.Scale, x, y, d, Auto: true);
                    last = c.T;
                    continue;
                }
                Finish(cur, outStart + s.ZoomOut, s.ZoomOut); // idle long enough: back to 100%
            }
            var start = Math.Max(0, c.T - s.ZoomIn);
            if (result.Count > 0) start = Math.Max(start, result[^1].End); // never overlap a finished zoom-out
            cur = new ZoomSegment(start, start, s.Scale, x, y, s.ZoomIn, Auto: true);
            last = c.T;
        }
        if (cur != null) Finish(cur, OutStart(cur) + s.ZoomOut, s.ZoomOut);
        return result;
    }

    /// <summary>The camera state at time t for these zooms (for UI and tests).</summary>
    public static ZoomState StateAt(IReadOnlyList<ZoomSegment> zooms, double t)
    {
        var active = zooms.Where(z => t > z.Start && t < z.End).ToList();
        if (active.Count == 0) return ZoomState.Idle;
        if (active.Count > 1) return ZoomState.Tracking;
        var z = active[0];
        var (ri, ro) = ZoomMath.Ramps(z);
        return t < z.Start + ri ? ZoomState.ZoomingIn : t > z.End - ro ? ZoomState.ZoomingOut : ZoomState.Focused;
    }
}
