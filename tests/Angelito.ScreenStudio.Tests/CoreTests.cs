using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Tests;

public class ZoomTests
{
    private static readonly ZoomSegment Seg = new(2, 6, 2, 0.75, 0.25, 0.5);

    [Fact]
    public void Evaluate_IsIdentityOutsideAndFullInside()
    {
        Assert.Equal((1d, 0.5, 0.5), ZoomMath.Evaluate([Seg], 1));
        Assert.Equal((1d, 0.5, 0.5), ZoomMath.Evaluate([Seg], 7));
        var (z, cx, cy) = ZoomMath.Evaluate([Seg], 4);
        Assert.Equal(2, z, 6);
        Assert.Equal(0.75, cx, 6);
        Assert.Equal(0.25, cy, 6);
    }

    [Fact]
    public void Evaluate_IsContinuousAcrossRamps()
    {
        double prev = 1;
        for (var t = 0.0; t < 8; t += 1 / 60.0)
        {
            var z = ZoomMath.Evaluate([Seg], t).Zoom;
            Assert.True(Math.Abs(z - prev) < 0.05, $"jump at {t}: {prev} -> {z}");
            prev = z;
        }
    }

    [Fact]
    public void ViewRect_StaysInsideFrame()
    {
        var corner = new ZoomSegment(0, 4, 3, 1, 1);
        var (x, y, w, h) = ZoomMath.ViewRect([corner], 2, 1920, 1080);
        Assert.Equal(640, w, 3);
        Assert.Equal(1920 - 640, x, 3);
        Assert.Equal(1080 - 360, y, 3);
        Assert.True(h > 0);
    }

    [Fact]
    public void AutoZoom_GroupsNearbyClicksAndSeparatesDistantOnes()
    {
        var ev = new List<CursorEvent>
        {
            new(1.0, 100, 100, CursorKind.Move),
            new(2.0, 400, 300, CursorKind.Left),
            new(3.0, 420, 310, CursorKind.Left),
            new(15.0, 1500, 900, CursorKind.Right),
        };
        var z = AutoZoom.Propose(ev, 1920, 1080, 20);
        Assert.Equal(2, z.Count);
        Assert.InRange(z[0].X, 0.2, 0.23);
        Assert.True(z[0].End < z[1].Start, "segments overlap");
        Assert.All(z, s => Assert.True(s.End - s.Start >= AutoZoom.MinLength - 1e-9));
    }

    [Fact]
    public void AutoZoom_NoClicks_NoZooms() => Assert.Empty(AutoZoom.Propose([new(1, 5, 5, CursorKind.Move)], 100, 100, 10));
}

public class ProjectTests
{
    [Fact]
    public void SplitDeleteUndoRedo()
    {
        var p = new StudioProject { Segments = [new(0, 10)] };
        var h = new History(p);
        h.Push(h.Current.SplitAt(4));
        Assert.Equal([new Segment(0, 4), new Segment(4, 10)], h.Current.Segments);
        h.Push(h.Current.DeleteAt(5));
        Assert.Equal(4, h.Current.OutputDuration, 6);
        h.Undo();
        Assert.Equal(10, h.Current.OutputDuration, 6);
        h.Redo();
        Assert.Single(h.Current.Segments);
        Assert.Same(h.Current, h.Current.DeleteAt(1)); // keeps at least one segment
    }

    [Fact]
    public void SaveLoadRoundTrip_AndBackupRecovery()
    {
        var dir = Directory.CreateTempSubdirectory("ass").FullName;
        var path = Path.Combine(dir, "p" + StudioProject.Extension);
        var p = new StudioProject { Segments = [new(1, 2)], Zooms = [new(0, 1, 2, 0.1, 0.2)], Webcam = new(Shape: WebcamShape.Rounded) };
        p.Save(path);
        p.Save(path); // second save creates .bak
        var back = StudioProject.Load(path);
        Assert.Equal(p.ToJson(), back.ToJson());

        File.WriteAllText(path, "{ corrupt");
        Assert.Equal(p.ToJson(), StudioProject.Load(path).ToJson());
    }

    [Fact]
    public void FutureFormatIsRejected() =>
        Assert.Throws<InvalidDataException>(() => StudioProject.FromJson("{\"formatVersion\": 99}"));

    [Fact]
    public void CursorLog_RoundTripsAndSkipsGarbage()
    {
        var e = new CursorEvent(1.25, -3, 40, CursorKind.Middle);
        Assert.True(CursorLog.TryParse(CursorLog.Format(e), out var back));
        Assert.Equal(e, back);
        Assert.False(CursorLog.TryParse("1.0 2", out _));
        Assert.False(CursorLog.TryParse("1.0 2 3 9", out _));
    }

    [Fact]
    public void WebcamRect_ClampedInsideOutput()
    {
        var (x, y, d) = Layout.WebcamRect(1920, 1080, new WebcamLayout(X: 1, Y: 1, Size: 0.3));
        Assert.Equal(324, d);
        Assert.Equal(1920 - 324, x);
        Assert.Equal(1080 - 324, y);
    }
}

public class PartsTests
{
    private static readonly List<RecordedPart> P = [new(0.2, 3), new(5, 2)];

    [Fact]
    public void MapsSessionTimeAcrossPauses()
    {
        Assert.Null(Parts.ToVideoTime(P, 0.1));
        Assert.Equal(0.8, Parts.ToVideoTime(P, 1.0)!.Value, 6);
        Assert.Null(Parts.ToVideoTime(P, 4)); // paused
        Assert.Equal(3.5, Parts.ToVideoTime(P, 5.5)!.Value, 6);
        Assert.Null(Parts.ToVideoTime(P, 7.1));
    }

    [Fact]
    public async Task AudioFilterKeepsOnlyRecordedParts()
    {
        var dir = Directory.CreateTempSubdirectory("ass-parts").FullName;
        var src = Path.Combine(dir, "a.wav");
        var dst = Path.Combine(dir, "b.wav");
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "sine=d=8", src])).Code);
        // audio started 0.5 s into the session → its first 0.3 s of part 1 are padded silence
        var (code, log) = await FfmpegProcess.RunAsync(["-y", "-i", src, "-filter_complex", Parts.AudioFilter(P, 0.5), "-map", "[out]", dst]);
        Assert.True(code == 0, log);
        Assert.Equal(5.0, await FfmpegProcess.ProbeDurationAsync(dst), 1);
    }
}

/// <summary>Runs the real exporter against a synthetic session made with ffmpeg test sources.</summary>
public class ExportIntegrationTests
{
    [Fact]
    public async Task ExportsCutsZoomClicksWebcamAndAudio()
    {
        var dir = Directory.CreateTempSubdirectory("ass-exp").FullName;
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", Path.Combine(dir, "screen.mp4")])).Code);
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "smptebars=s=320x240:r=30:d=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", Path.Combine(dir, "webcam.mp4")])).Code);
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "sine=f=440:d=3", Path.Combine(dir, "mic.wav")])).Code);
        var m = new SessionManifest { Width = 640, Height = 360, Fps = 30, Webcam = "webcam.mp4", WebcamOffset = 0.2, Mic = "mic.wav", MicOffset = 0.5, Complete = true };
        var cursor = new List<CursorEvent> { new(1, 320, 180, CursorKind.Left), new(1.3, 330, 190, CursorKind.Right) };
        var p = StudioProject.FromSession(4, cursor, 640, 360).SplitAt(2).DeleteAt(2.5);
        Assert.NotEmpty(p.Zooms);

        var outPath = Path.Combine(dir, "out.mp4");
        var plan = Exporter.Build(p, m, dir, cursor, EncoderProbe.Cpu, outPath, Path.Combine(dir, "graph.txt"));
        var last = 0.0;
        await Exporter.RunAsync(plan, Path.Combine(dir, "graph.txt"), v => last = v, CancellationToken.None);

        Assert.True(File.Exists(outPath));
        Assert.Equal(2.0, await FfmpegProcess.ProbeDurationAsync(outPath), 1);
        Assert.True(last > 0.5);
    }
}
