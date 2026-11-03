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
    /// <summary>Smoothstep weight 0..1 of one segment at time t.</summary>
    public static double Weight(ZoomSegment z, double t)
    {
        var r = Math.Max(0.001, Math.Min(z.Ramp, (z.End - z.Start) / 2));
        var a = Math.Min(Math.Clamp((t - z.Start) / r, 0, 1), Math.Clamp((z.End - t) / r, 0, 1));
        return a * a * (3 - 2 * a);
    }

    /// <summary>Zoom factor (≥1) and normalized center at time t; the center rests at (bx, by) outside zooms.</summary>
    public static (double Zoom, double Cx, double Cy) Evaluate(IReadOnlyList<ZoomSegment> zooms, double t, double bx = 0.5, double by = 0.5)
    {
        double z = 1, cx = bx, cy = by;
        foreach (var s in zooms)
        {
            var w = Weight(s, t);
            if (w <= 0) continue;
            z += (s.Scale - 1) * w;
            cx += (s.X - bx) * w;
            cy += (s.Y - by) * w;
        }
        return (Math.Max(1, z), cx, cy);
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
        var z = new StringBuilder("1");
        var cx = new StringBuilder(F($"{canvas.X}"));
        var cy = new StringBuilder(F($"{canvas.Y}"));
        foreach (var s in zooms)
        {
            var r = Math.Max(0.001, Math.Min(s.Ramp, (s.End - s.Start) / 2));
            var a = F($"min(clip(({t}-{s.Start})/{r},0,1),clip(({s.End}-{t})/{r},0,1))");
            var w = $"({a})*({a})*(3-2*({a}))";
            z.Append(F($"+{s.Scale - 1}*{w}"));
            cx.Append(F($"+{s.X - canvas.X}*{w}"));
            cy.Append(F($"+{s.Y - canvas.Y}*{w}"));
        }
        var vw = F($"({cw}/max(1,{z}))");
        var vh = F($"({ch}/max(1,{z}))");
        return (F($"clip(({cx})*{width}-{vw}/2,0,{width}-{vw})"), F($"clip(({cy})*{height}-{vh}/2,0,{height}-{vh})"), vw, vh);
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
/// <summary>
/// Proposes zoom segments from clicks. Proposals are ordinary editable segments, never baked into video.
/// Clicks are grouped so the camera does not chase every small mouse movement.
/// </summary>
public static class AutoZoom
{
    public const double ClusterGap = 3.0, LeadIn = 0.8, Hold = 1.6, MinLength = 2.0, MinPause = 1.0, DefaultScale = 1.8;

    public static List<ZoomSegment> Propose(IReadOnlyList<CursorEvent> events, int width, int height, double duration)
    {
        var clicks = events.Where(e => e.K != CursorKind.Move && e.T >= 0 && e.T <= duration).OrderBy(e => e.T).ToList();
        var clusters = new List<List<CursorEvent>>();
        var reach = 0.3 * Math.Max(width, height);
        foreach (var c in clicks)
        {
            var last = clusters.LastOrDefault();
            if (last != null && c.T - last[^1].T <= ClusterGap
                && Math.Abs(c.X - last.Average(e => e.X)) <= reach && Math.Abs(c.Y - last.Average(e => e.Y)) <= reach)
                last.Add(c);
            else clusters.Add([c]);
        }

        var result = new List<ZoomSegment>();
        foreach (var cl in clusters)
        {
            var s = Math.Max(0, cl[0].T - LeadIn);
            var e = Math.Min(duration, Math.Max(cl[^1].T + Hold, s + MinLength));
            var seg = new ZoomSegment(s, e, DefaultScale, Math.Clamp(cl.Average(p => p.X) / width, 0, 1),
                Math.Clamp(cl.Average(p => p.Y) / height, 0, 1));
            if (result.Count > 0 && seg.Start - result[^1].End < MinPause)
            {
                var p = result[^1];
                double wp = p.End - p.Start, ws = seg.End - seg.Start;
                result[^1] = p with
                {
                    End = Math.Max(p.End, seg.End),
                    X = (p.X * wp + seg.X * ws) / (wp + ws),
                    Y = (p.Y * wp + seg.Y * ws) / (wp + ws),
                };
            }
            else if (seg.End - seg.Start > 0.2) result.Add(seg);
        }
        return result;
    }
}
