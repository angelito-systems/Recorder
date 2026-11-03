namespace Angelito.ScreenStudio.Core;

public enum DragMode { Move, Start, End }

/// <summary>Range dragging on the timeline: magnetic snapping, then clamping to the neighbors' bounds.</summary>
public static class TimelineEdit
{
    /// <summary>
    /// New (start, end) after dragging [s, e] by <paramref name="delta"/> seconds. The result stays inside [lo, hi] and
    /// at least <paramref name="minLen"/> long; a moved edge sticks to the nearest snap point within <paramref name="tol"/>.
    /// </summary>
    public static (double Start, double End) Drag(double s, double e, DragMode mode, double delta, double lo, double hi,
        double minLen, IReadOnlyList<double> snaps, double tol)
    {
        var len = e - s;
        switch (mode)
        {
            case DragMode.Move:
            {
                var ns = s + delta;
                // Snap whichever edge lands closer to a snap point.
                double ss = Nearest(ns, snaps, tol), se = Nearest(ns + len, snaps, tol);
                bool snapS = ss != ns, snapE = se != ns + len;
                if (snapS && (!snapE || Math.Abs(ss - ns) <= Math.Abs(se - ns - len))) ns = ss;
                else if (snapE) ns = se - len;
                ns = Math.Clamp(ns, lo, Math.Max(lo, hi - len));
                return (ns, ns + Math.Min(len, hi - lo));
            }
            case DragMode.Start:
                return (Math.Clamp(Nearest(s + delta, snaps, tol), lo, e - minLen), e);
            default:
                return (s, Math.Clamp(Nearest(e + delta, snaps, tol), s + minLen, hi));
        }
    }

    private static double Nearest(double t, IReadOnlyList<double> snaps, double tol)
    {
        var best = t;
        var bestD = tol;
        foreach (var p in snaps)
        {
            var d = Math.Abs(p - t);
            if (d <= bestD) { bestD = d; best = p; }
        }
        return best;
    }
}
