using System.Runtime.InteropServices;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Tests;

/// <summary>The drawn pointer: path, modes, fades, and real exports with captured Windows cursors.</summary>
public class PointerTests
{
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr inst, int id);
    private const int Hand = 32649, IBeam = 32513, Wait = 32514;

    private static CursorEvent Move(double t, int x, int y) => new(t, x, y, CursorKind.Move);
    private static readonly Dictionary<string, CursorShape> OneShape = new() { ["a"] = new CursorShape("a", 32, 32, 4, 6, [0]) };

    [Fact]
    public void OriginalFollowsTheRawPathAndAppliesTheHotspot()
    {
        var tr = new CursorTrack([Move(0, 100, 100), Move(1, 300, 200)], [(0, "a")], OneShape, new PointerStyle(Mode: PointerMode.Original), 2);
        var s = tr.At(1.5, 30);
        Assert.Equal(("a", 300 - 4.0, 200 - 6.0), (s.ShapeId, s.X, s.Y));
    }

    [Fact]
    public void CinematicSmoothsJitterButLandsExactlyOnClicks()
    {
        var ev = new List<CursorEvent>();
        var rnd = new Random(3);
        for (var t = 0.0; t < 4; t += 1 / 120.0) ev.Add(Move(t, 500 + rnd.Next(-12, 13), 300 + rnd.Next(-12, 13))); // shaky hand
        ev.Add(new(2.0, 640, 360, CursorKind.Left));
        var raw = new CursorTrack(ev, [(0, "a")], OneShape, new PointerStyle(Mode: PointerMode.Original), 4);
        var smooth = new CursorTrack(ev, [(0, "a")], OneShape, new PointerStyle(Mode: PointerMode.Cinematic, Smoothing: 1), 4);
        double Jitter(CursorTrack tr) => Enumerable.Range(1, 100).Sum(i => Math.Abs(tr.Position(0.5 + i / 120.0).X - tr.Position(0.5 + (i - 1) / 120.0).X));
        Assert.True(Jitter(smooth) < Jitter(raw) / 4, $"not smoothed: {Jitter(smooth)} vs {Jitter(raw)}");
        Assert.Equal((640.0, 360.0), smooth.Position(2.0)); // the click is where the pointer is
    }

    [Fact]
    public void HiddenOrUncapturableNeverDrawsAStandIn()
    {
        var log = new List<(double, string)> { (0, "a"), (1, CursorShapeLog.Hidden), (2, CursorShapeLog.Unknown), (3, "a"), (4, "gone") };
        var tr = new CursorTrack([Move(0, 10, 10)], log, OneShape, new PointerStyle(), 5);
        Assert.Equal("a", tr.At(0.5, 30).ShapeId);
        Assert.Null(tr.At(1.5, 30).ShapeId);    // hidden (typing, or an app drawing its own pointer: it's in the video)
        Assert.Null(tr.At(2.5, 30).ShapeId);    // unknown right after hidden: nothing real to keep
        Assert.Equal("a", tr.At(4.5, 30).ShapeId); // a missing shape file keeps the last real shape
    }

    [Fact]
    public void HideIdleFadesOutAndBackIn()
    {
        var tr = new CursorTrack([Move(0, 10, 10), Move(5, 20, 20)], [(0, "a")], OneShape, new PointerStyle(HideIdle: true), 6);
        Assert.Equal(1, tr.At(1, 30).Alpha);
        Assert.Equal(0, tr.At(CursorTrack.IdleAfter + CursorTrack.IdleFade + 0.1, 30).Alpha);
        Assert.Equal(1, tr.At(5.1, 30).Alpha);
    }

    [Fact]
    public void CustomReplacesOnlyTheArrow()
    {
        var shapes = new Dictionary<string, CursorShape> { ["arrow"] = new("arrow", 32, 32, 0, 0, [0], Arrow: true), ["hand"] = new("hand", 32, 32, 10, 0, [0]) };
        var tr = new CursorTrack([Move(0, 50, 50)], [(0, "arrow"), (1, "hand")], shapes, new PointerStyle(Mode: PointerMode.Custom, CustomImage: "c.png", CustomHotX: 0), 2, customSize: (40, 40));
        Assert.Equal(CursorTrack.CustomShapeId, tr.At(0.5, 30).ShapeId);
        Assert.Equal("hand", tr.At(1.5, 30).ShapeId);
    }

    [Fact]
    public void ScaleIncludesDpi()
    {
        var tr = new CursorTrack([Move(0, 100, 100)], [(0, "a")], OneShape, new PointerStyle(Mode: PointerMode.Cinematic, Scale: 2), 1, dpiScale: 1.5);
        var s = tr.At(0.5, 30);
        Assert.Equal(3, s.Scale, 6);
        Assert.Equal(100 - 4 * 3, s.X, 6); // hotspot scales with the cursor
    }

    // ---------- real export ----------

    private static async Task<(string Dir, SessionManifest M)> Session(double seconds)
    {
        var dir = TempDir.Create("ass-pointer").FullName;
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", $"color=c=gray:s=640x360:r=30:d={seconds}", "-c:v", "libx264", "-qp", "0", "-pix_fmt", "yuv420p", Path.Combine(dir, "screen.mp4")])).Code);
        return (dir, new SessionManifest { Width = 640, Height = 360, Fps = 30, Complete = true, CursorInVideo = false });
    }

    private static async Task<byte[]> Frame(string dir, string file, double t)
    {
        var raw = Path.Combine(dir, $"f{t:F3}.rgb");
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-ss", FormattableString.Invariant($"{t:F3}"), "-i", file, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", raw])).Code);
        return File.ReadAllBytes(raw);
    }

    /// <summary>Pixels clearly not the gray background inside a box.</summary>
    private static int Ink(byte[] rgb, int x0, int y0, int w, int h, int width = 640) =>
        Enumerable.Range(y0, h).SelectMany(y => Enumerable.Range(x0, w).Select(x => (x, y)))
            .Count(p => p.x >= 0 && p.y >= 0 && p.x < width && p.y < 360 && Math.Abs(rgb[(p.y * width + p.x) * 3] - 128) > 40);

    [Fact]
    public async Task ExportDrawsTheRealShapesWhereThePreviewDoes()
    {
        var (dir, m) = await Session(3);
        var store = new CursorShapeStore(dir);
        var hand = store.Resolve(LoadCursor(IntPtr.Zero, Hand))!;
        var beam = store.Resolve(LoadCursor(IntPtr.Zero, IBeam))!;
        var log = new List<(double, string)> { (0, hand), (1, CursorShapeLog.Hidden), (2, beam) };
        var ev = new List<CursorEvent> { Move(0, 200, 150), Move(2, 420, 220) };
        var p = new StudioProject { Segments = [new(0, 3)], Pointer = new PointerStyle(Mode: PointerMode.Original) };
        var track = new CursorTrack(ev, log, store.Shapes, p.Pointer, 3);
        var outPath = Path.Combine(dir, "out.mp4");
        await Exporter.RunAsync(Exporter.Build(p, m, dir, ev, EncoderProbe.Cpu, outPath, Path.Combine(dir, "g.txt"), pointer: track), Path.Combine(dir, "g.txt"), _ => { }, CancellationToken.None);

        var a = track.At(0.5, 30);
        var f = await Frame(dir, outPath, 0.5);
        Assert.True(Ink(f, (int)a.X, (int)a.Y, 32, 32) > 30, "hand not drawn at the track's position");
        Assert.True(Ink(f, 0, 0, 160, 120) == 0, "cursor drawn somewhere else");
        Assert.Equal(0, Ink(await Frame(dir, outPath, 1.5), 0, 0, 640, 360)); // hidden: nothing at all
        var b = track.At(2.5, 30);
        var g = await Frame(dir, outPath, 2.5);
        Assert.True(Ink(g, (int)b.X, (int)b.Y, 32, 32) > 10, "text cursor not drawn");
        Assert.Equal(0, Ink(g, (int)a.X, (int)a.Y, 32, 32)); // the hand is gone: one cursor at a time
    }

    [Fact]
    public async Task ExportAnimatesAndZoomsTheCursor()
    {
        var (dir, m) = await Session(2);
        var store = new CursorShapeStore(dir);
        var wait = store.Resolve(LoadCursor(IntPtr.Zero, Wait))!;
        var shape = store.Shapes[wait];
        var ev = new List<CursorEvent> { Move(0, 320, 180) };
        // 2× zoom centered on the cursor for the whole clip.
        var p = new StudioProject { Segments = [new(0, 2)], Zooms = [new(0, 2, 2, 0.5, 0.5, 0.01)], Pointer = new PointerStyle(Mode: PointerMode.Original) };
        var track = new CursorTrack(ev, [(0, wait)], store.Shapes, p.Pointer, 2);
        var outPath = Path.Combine(dir, "out.mp4");
        await Exporter.RunAsync(Exporter.Build(p, m, dir, ev, EncoderProbe.Cpu, outPath, Path.Combine(dir, "g.txt"), pointer: track), Path.Combine(dir, "g.txt"), _ => { }, CancellationToken.None);

        // Under 2× the 32 px cursor covers ~64 px around the center.
        var f1 = await Frame(dir, outPath, 1.0);
        var px = File.ReadAllBytes(CursorShapeStore.PixelsPath(dir, wait));
        var opaque = Enumerable.Range(0, shape.W * shape.H).Count(i => px[i * 4 + 3] > 128);
        var ink = Ink(f1, 320 - 48, 180 - 48, 96, 96);
        Assert.True(ink > opaque * 2.5, $"cursor not magnified by the zoom: {ink} px vs {opaque} at 1x"); // ~4x the area at 2x
        if (shape.Frames > 1)
        {
            // Half a cycle apart the spinner's highlight is on the opposite side; its colors differ mostly in green.
            var f2 = await Frame(dir, outPath, 1.0 + shape.CycleMs / 2000.0);
            var diff = Enumerable.Range(0, f1.Length / 3).Count(i => Math.Abs(f1[i * 3 + 1] - f2[i * 3 + 1]) > 25);
            Assert.True(diff > 30, $"animated cursor didn't animate ({diff} px changed)");
            Assert.NotEqual(shape.FrameAt(1.0, 30), shape.FrameAt(1.0 + shape.CycleMs / 2000.0, 30));
        }
    }
}
