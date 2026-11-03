using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Tests;

/// <summary>Auto-zoom behaviour: zoom on click, hold, follow, and always return to 100%.</summary>
public class ZoomEngineTests
{
    private const int W = 1920, H = 1080;
    private static readonly AutoZoomSettings S = new(); // 1.5×, in 0.45, hold 1.2, idle 2.0, out 0.6

    private static CursorEvent Click(double t, int x, int y) => new(t, x, y, CursorKind.Left);
    private static double Scale(List<ZoomSegment> z, double t) => ZoomMath.Evaluate(z, t).Zoom;

    /// <summary>Max frame-to-frame change of zoom and center at 60 fps: catches jumps.</summary>
    private static (double Zoom, double Center) MaxStep(List<ZoomSegment> z, double from, double to)
    {
        double dz = 0, dc = 0;
        var prev = ZoomMath.Evaluate(z, from);
        for (var t = from + 1 / 60.0; t <= to; t += 1 / 60.0)
        {
            var e = ZoomMath.Evaluate(z, t);
            dz = Math.Max(dz, Math.Abs(e.Zoom - prev.Zoom));
            dc = Math.Max(dc, Math.Max(Math.Abs(e.Cx - prev.Cx), Math.Abs(e.Cy - prev.Cy)));
            prev = e;
        }
        return (dz, dc);
    }

    [Fact] // 1. one click, then 2 s idle → back to 100%
    public void OneClickReturnsToFullView()
    {
        var z = AutoZoom.Propose([Click(2, 600, 400)], W, H, 20, S);
        Assert.Single(z);
        Assert.Equal(1, Scale(z, 1.5), 6);                       // 100% before
        Assert.Equal(1.5, Scale(z, 2.0), 6);                     // arrived as the click happens
        Assert.Equal(1.5, Scale(z, 2 + S.IdleReturn - 0.01), 6); // held during the idle wait
        Assert.Equal(1, Scale(z, 2 + S.IdleReturn + S.ZoomOut + 0.01), 6); // back to 100%
        Assert.Equal(ZoomState.ZoomingOut, AutoZoom.StateAt(z, 2 + S.IdleReturn + S.ZoomOut / 2));
        var e = ZoomMath.Evaluate(z, 10);
        Assert.Equal((1.0, 0.5, 0.5), (e.Zoom, e.Cx, e.Cy));     // center and framing restored too
    }

    [Fact] // 2. clicks in the same region keep the focus
    public void SameRegionClicksHoldFocus()
    {
        var z = AutoZoom.Propose([Click(2, 600, 400), Click(3, 650, 420), Click(4.5, 620, 380), Click(6, 640, 410)], W, H, 30, S);
        Assert.Single(z);
        for (var t = 2.0; t <= 6 + S.IdleReturn; t += 0.1) Assert.Equal(1.5, Scale(z, t), 6);
        Assert.Equal(1, Scale(z, 6 + S.IdleReturn + S.ZoomOut + 0.01), 6);
    }

    [Fact] // 3. a click elsewhere pans there at constant scale, smoothly
    public void OtherRegionPansWithoutZoomingOut()
    {
        var z = AutoZoom.Propose([Click(2, 300, 300), Click(3.5, 1600, 800)], W, H, 30, S);
        Assert.Equal(2, z.Count);
        for (var t = 2.0; t <= 3.5 + S.IdleReturn; t += 0.02) Assert.Equal(1.5, Scale(z, t), 6); // never dips
        Assert.Equal(ZoomState.Tracking, AutoZoom.StateAt(z, 3.5 - S.ZoomIn / 2));
        var at = ZoomMath.Evaluate(z, 3.5);
        Assert.Equal(1600.0 / W, at.Cx, 3);
        Assert.Equal(800.0 / H, at.Cy, 3);
        Assert.True(MaxStep(z, 0, 10).Center < 0.06, "pan jumps");
    }

    [Fact] // 4. mouse movement alone never zooms
    public void MovesAloneDoNotZoom()
    {
        var moves = Enumerable.Range(0, 600).Select(i => new CursorEvent(i * 0.05, i * 3 % W, i * 2 % H, CursorKind.Move)).ToList();
        Assert.Empty(AutoZoom.Propose(moves, W, H, 30, S));
    }

    [Fact] // 5. a click during the zoom-out cancels it smoothly instead of popping back in
    public void ClickWhileZoomingOutReAdjusts()
    {
        var outMid = 2 + S.IdleReturn + S.ZoomOut / 2;
        var z = AutoZoom.Propose([Click(2, 600, 400), Click(outMid, 610, 410)], W, H, 30, S);
        Assert.Single(z);                                        // no second, stacked zoom
        Assert.Equal(1.5, Scale(z, outMid), 6);                  // the zoom-out never happened
        var (dz, dc) = MaxStep(z, 0, 15);
        Assert.True(dz < 0.03 && dc < 0.03, $"jump: zoom {dz}, center {dc}");
    }

    [Fact] // 6. pause/resume: session time is continuous, so clicks around a pause behave like any other
    public void AcrossPauseStaysCoherent()
    {
        // A pause removes no time from the session clock; clicks before/after are just apart in time.
        var z = AutoZoom.Propose([Click(2, 600, 400), Click(12, 600, 400)], W, H, 30, S);
        Assert.Equal(2, z.Count);
        Assert.Equal(1, Scale(z, 8), 6);                         // fully out between them
        Assert.True(z[0].End <= z[1].Start);
    }

    [Fact] // 8. long project: no accumulation, bounded scale, returns to 100% in every quiet gap
    public void LongProjectNeverAccumulates()
    {
        var rnd = new Random(7);
        var clicks = new List<CursorEvent>();
        for (double t = 1; t < 3600; t += 0.5 + rnd.NextDouble() * 9)
            clicks.Add(Click(t, rnd.Next(W), rnd.Next(H)));
        var z = AutoZoom.Propose(clicks, W, H, 3600, S);
        for (double t = 0; t < 3600; t += 0.37)
        {
            var e = ZoomMath.Evaluate(z, t);
            Assert.InRange(e.Zoom, 1, S.Scale + 1e-9);
        }
        Assert.All(z.Zip(z.Skip(1)), p => Assert.True(p.Second.Start >= p.First.Start));
        // Every gap of idle + out without clicks returns to exactly 100%.
        var times = clicks.Select(c => c.T).ToList();
        foreach (var (a, b) in times.Zip(times.Skip(1)).Where(p => p.Second - p.First > S.IdleReturn + S.ZoomOut + S.ZoomIn + 0.1))
            Assert.Equal(1, Scale(z, a + S.IdleReturn + S.ZoomOut + 0.05), 6);
        var (dz, _) = MaxStep(z, 0, 600);
        Assert.True(dz < 0.03, $"zoom jump {dz}");
    }

    [Fact]
    public void DisabledProposesNothing() =>
        Assert.Empty(AutoZoom.Propose([Click(2, 600, 400)], W, H, 20, S with { Enabled = false }));

    [Fact]
    public void StackedZoomsAverageInsteadOfCompounding()
    {
        var a = new ZoomSegment(0, 10, 2, 0.3, 0.3, 0.1);
        var b = new ZoomSegment(0, 10, 3, 0.7, 0.7, 0.1);
        var e = ZoomMath.Evaluate([a, b], 5);
        Assert.Equal(2.5, e.Zoom, 6); // not 1 + 1 + 2 = 4
        Assert.Equal(0.5, e.Cx, 6);
    }
}
