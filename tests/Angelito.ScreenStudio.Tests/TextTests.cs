using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Tests;

public class SrtTests
{
    [Fact]
    public void ParsesRealWorldVariants()
    {
        const string srt = "﻿1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>Hola</i> mundo\r\nsegunda línea\r\n\r\n" +
                           "00:00:03.2 --> 00:00:04.75\nSin índice\n\n3\n00:00:05,000 --> 00:00:04,000\nInvertido\n\n";
        var cues = Srt.Parse(srt);
        Assert.Equal(2, cues.Count);
        Assert.Equal(new Cue(1, 2.5, "Hola mundo\nsegunda línea"), cues[0]);
        Assert.Equal(3.2, cues[1].Start, 6);
        Assert.Equal(4.75, cues[1].End, 6);
    }

    [Fact]
    public void FormatRoundTrips()
    {
        List<Cue> cues = [new(0.5, 1.25, "a\nb"), new(3661.001, 3662, "c")];
        var text = Srt.Format(cues);
        Assert.Contains("01:01:01,001 --> 01:01:02,000", text);
        Assert.Equal(cues, Srt.Parse(text));
    }
}

public class TimelineEditTests
{
    private static readonly double[] None = [];

    [Fact]
    public void MoveKeepsLengthAndStaysInBounds()
    {
        Assert.Equal((3, 5), TimelineEdit.Drag(2, 4, DragMode.Move, 1, 0, 10, 0.2, None, 0));
        Assert.Equal((8, 10), TimelineEdit.Drag(2, 4, DragMode.Move, 50, 0, 10, 0.2, None, 0));
        Assert.Equal((1, 3), TimelineEdit.Drag(2, 4, DragMode.Move, -5, 1, 10, 0.2, None, 0));
    }

    [Fact]
    public void ResizeRespectsMinLengthAndNeighbors()
    {
        Assert.Equal((3.8, 4), TimelineEdit.Drag(2, 4, DragMode.Start, 5, 0, 10, 0.2, None, 0));
        Assert.Equal((2, 6), TimelineEdit.Drag(2, 4, DragMode.End, 9, 0, 6, 0.2, None, 0));
    }

    [Fact]
    public void SnapsEdgesWithinTolerance()
    {
        double[] snaps = [5.0];
        Assert.Equal((5, 7), TimelineEdit.Drag(2, 4, DragMode.Move, 2.95, 0, 10, 0.2, snaps, 0.1)); // start snaps
        Assert.Equal((3, 5), TimelineEdit.Drag(2, 4, DragMode.Move, 0.93, 0, 10, 0.2, snaps, 0.1)); // end snaps
        Assert.Equal((2, 5), TimelineEdit.Drag(2, 4, DragMode.End, 0.95, 0, 10, 0.2, snaps, 0.1));
        Assert.Equal((2, 4.8), TimelineEdit.Drag(2, 4, DragMode.End, 0.8, 0, 10, 0.2, snaps, 0.1)); // out of reach
    }
}

public class MaskTests
{
    /// <summary>Privacy check: the masked area must actually change, the rest must not.</summary>
    [Theory]
    [InlineData(AnnotationKind.Blur)]
    [InlineData(AnnotationKind.Pixelate)]
    public async Task MaskChangesOnlyItsRegion(AnnotationKind kind)
    {
        var dir = Directory.CreateTempSubdirectory("ass-mask").FullName;
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=1", "-c:v", "libx264", "-qp", "0", "-pix_fmt", "yuv420p", Path.Combine(dir, "screen.mp4")])).Code);
        var m = new SessionManifest { Width = 640, Height = 360, Fps = 30, Complete = true };
        // Mask the left half (odd pixel bounds on purpose: the exporter must grow, not shrink, the box).
        var p = new StudioProject { Segments = [new(0, 1)], Annotations = [new(0, 1, kind, 0.001, 0.001, 0.5, 0.999)] };
        var outPath = Path.Combine(dir, "out.mp4");
        var plan = Exporter.Build(p, m, dir, [], EncoderProbe.Cpu, outPath, Path.Combine(dir, "g.txt"));
        await Exporter.RunAsync(plan, Path.Combine(dir, "g.txt"), _ => { }, CancellationToken.None);

        Assert.True(await Psnr(dir, "crop=300:340:10:10") < 30, "masked area is still close to the original");
        Assert.True(await Psnr(dir, "crop=300:340:330:10") > 35, "unmasked area changed");
    }

    [Fact]
    public async Task SmallBlurStillDestroysDetail()
    {
        var dir = Directory.CreateTempSubdirectory("ass-mask").FullName;
        // The real case: small black text (a "password") on white, under a 64×24 mask.
        var font = Exporter.Font(false).Replace('\\', '/').Replace(":", "\\:");
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", $"color=white:s=640x360:r=30:d=1,drawtext=fontfile='{font}':text='clave 1234':fontsize=15:fontcolor=black:x=194:y=112",
            "-c:v", "libx264", "-qp", "0", "-pix_fmt", "yuv420p", Path.Combine(dir, "screen.mp4")])).Code);
        var m = new SessionManifest { Width = 640, Height = 360, Fps = 30, Complete = true };
        var p = new StudioProject { Segments = [new(0, 1)], Annotations = [new(0, 1, AnnotationKind.Blur, 0.3, 0.3, 0.4, 0.3667)] };
        var plan = Exporter.Build(p, m, dir, [], EncoderProbe.Cpu, Path.Combine(dir, "out.mp4"), Path.Combine(dir, "g.txt"));
        await Exporter.RunAsync(plan, Path.Combine(dir, "g.txt"), _ => { }, CancellationToken.None);
        Assert.True(await Psnr(dir, "crop=56:16:196:112") < 20);
    }

    private static async Task<double> Psnr(string dir, string crop)
    {
        double result = double.NaN;
        using var f = new FfmpegProcess(["-i", Path.Combine(dir, "out.mp4"), "-i", Path.Combine(dir, "screen.mp4"),
            "-lavfi", $"[0:v]{crop}[a];[1:v]{crop}[b];[a][b]psnr", "-f", "null", "-"]);
        f.StdErr += l =>
        {
            var i = l.IndexOf("average:", StringComparison.Ordinal);
            if (i >= 0) result = double.Parse(l[(i + 8)..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        };
        f.Start();
        await f.WaitAsync();
        return result;
    }
}

public class MediaCacheTests
{
    [Fact]
    public async Task ThumbnailsAreGeneratedOnceAndCoverTheVideo()
    {
        var dir = Directory.CreateTempSubdirectory("ass-cache").FullName;
        var video = Path.Combine(dir, "screen.mp4");
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=7", "-g", "60", "-c:v", "libx264", "-pix_fmt", "yuv420p", video])).Code);
        var thumbs = await MediaCache.ThumbnailsAsync(video, dir, 7, CancellationToken.None);
        Assert.True(File.Exists(MediaCache.ThumbPath(thumbs, 0)));
        Assert.True(File.Exists(MediaCache.ThumbPath(thumbs, 2))); // t = 4 s (the fps filter may drop the very last slot)
        var stamp = File.GetLastWriteTimeUtc(MediaCache.ThumbPath(thumbs, 0));
        await MediaCache.ThumbnailsAsync(video, dir, 7, CancellationToken.None);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(MediaCache.ThumbPath(thumbs, 0))); // reused, not regenerated
    }

    [Fact]
    public async Task PeaksFollowLoudness()
    {
        var dir = Directory.CreateTempSubdirectory("ass-cache").FullName;
        var wav = Path.Combine(dir, "mic.wav");
        // 1 s of silence, then 1 s of a loud tone
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=1", "-f", "lavfi", "-i", "sine=f=440:d=1:r=48000",
            "-filter_complex", "[1]volume=7,aformat=channel_layouts=stereo[s];[0][s]concat=n=2:v=0:a=1", wav])).Code);
        var peaks = await MediaCache.PeaksAsync(wav, dir, CancellationToken.None);
        Assert.InRange(peaks.Length, 195, 205);
        Assert.True(peaks.Take(90).All(p => p < 3));
        Assert.True(peaks.Skip(110).Take(80).All(p => p > 150));
        Assert.Equal(peaks, await MediaCache.PeaksAsync(wav, dir, CancellationToken.None));
    }
}

public class TimelineViewTests
{
    [Fact]
    public void ZoomKeepsAnchorUnderCursor()
    {
        var v = TimelineView.Fit(100).ZoomAt(25, 4, 100);
        Assert.Equal(25, v.Length, 6);
        Assert.Equal(0.25, (25 - v.Start) / v.Length, 6); // anchor stays at 25% of the width
        Assert.Equal(25, v.ToTime(v.ToX(25, 800), 800), 6);
    }

    [Fact]
    public void StaysInsideDurationAndLimits()
    {
        var v = TimelineView.Fit(100);
        Assert.Equal(TimelineView.Fit(100), v.ZoomAt(50, 0.1, 100)); // can't zoom out past the whole clip
        Assert.Equal(TimelineView.MinLength, v.ZoomAt(99.9, 1e6, 100).Length, 6);
        var deep = v.ZoomAt(99.9, 1e6, 100);
        Assert.True(deep.End <= 100 && deep.Start <= 99.9 && deep.End >= 99.9); // anchor visible, never past the end
        Assert.Equal(100, new TimelineView(95, 10).Clamped(100).End, 6);
        Assert.Equal(90, new TimelineView(0, 10).ScrollBy(500, 100).Start, 6);
        Assert.Equal(0, new TimelineView(5, 10).ScrollBy(-50, 100).Start, 6);
    }

    [Fact]
    public void FollowPagesOnlyWhenOutside()
    {
        var v = new TimelineView(10, 10);
        Assert.Equal(v, v.Follow(15, 100));
        Assert.Equal(24, v.Follow(25, 100).Start, 6);
        Assert.Equal(0, v.Follow(0.5, 100).Start, 6);
    }
}

public class TimeMappingTests
{
    // Source 0-10 with 4-6 removed: output is 8 s long.
    private static readonly StudioProject P = new() { Segments = [new(0, 4), new(6, 10)] };

    [Fact]
    public void MapsBothWays()
    {
        Assert.Equal(3, P.ToOutput(3));
        Assert.Null(P.ToOutput(5));
        Assert.Equal(5, P.ToOutput(7));
        Assert.Equal(7, P.ToSource(5));
        Assert.Equal(10, P.ToSource(99));
    }

    [Fact]
    public void SrtExportUsesOutputTimeAndDropsRemovedCues()
    {
        var p = P with { Subtitles = [new(1, 2, "kept"), new(4.5, 5.5, "removed"), new(3, 7, "spans cut")] };
        var cues = Srt.Parse(p.ExportSrt());
        Assert.Equal(["kept", "spans cut"], cues.Select(c => c.Text));
        Assert.Equal(3, cues[1].Start, 3);
        Assert.Equal(5, cues[1].End, 3);
    }

    [Fact]
    public void SrtImportMapsBackToSource()
    {
        var p = P.ImportSrt("1\n00:00:05,000 --> 00:00:06,000\nx\n");
        Assert.Equal(new Cue(7, 8, "x"), Assert.Single(p.Subtitles));
    }
}
