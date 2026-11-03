using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Tests;

/// <summary>Clips play and export in list order, which can differ from source order.</summary>
public class ReorderTests
{
    // Source 0–6 s split into A(0–2) B(2–4) C(4–6), then C moved to the front: output order C, A, B.
    private static StudioProject Reordered() =>
        StudioProject.FromSession(6, [], 640, 360).SplitAt(2).SplitAt(4).MoveSegment(5, -1).MoveSegment(5, -1);

    [Fact]
    public void MoveSwapsWithNeighbourAndStopsAtEdges()
    {
        var p = Reordered();
        Assert.Equal([4.0, 0, 2], p.Segments.Select(s => s.Start));
        Assert.Same(p, p.MoveSegment(5, -1));       // already first
        Assert.Same(p, p.MoveSegment(3, +1));       // already last
        Assert.Equal([0.0, 4, 2], p.MoveSegment(5, +1).Segments.Select(s => s.Start));
    }

    [Fact]
    public void TimeMappingFollowsListOrder()
    {
        var p = Reordered();
        Assert.Equal(0.5, p.ToOutput(4.5));
        Assert.Equal(2.5, p.ToOutput(0.5));
        Assert.Equal(4.5, p.ToSource(0.5));
        Assert.Equal(0.5, p.ToSource(2.5));
        Assert.Equal((2.0, 6.0), p.OutputRange(0, 4));    // A and B stay adjacent in the output
        Assert.Equal((0.0, 2.0), p.OutputRange(3.5, 6));  // B's tail and C split apart: the earlier run (C) wins
    }

    [Fact]
    public void PlaybackRunsInListOrder()
    {
        var p = Reordered();
        var cur = p.PlaybackSegment(-1, 4.0);
        Assert.Equal(0, cur);                         // starts in C
        Assert.Equal(1, p.PlaybackSegment(cur, 6.0)); // C ends → A (back in source time)
        Assert.Equal(2, p.PlaybackSegment(1, 2.0));   // A ends → B
        Assert.Equal(-1, p.PlaybackSegment(2, 4.0));  // B ends → end, not on into C
    }

    [Fact]
    public async Task ExportUsesListOrder()
    {
        var dir = TempDir.Create("ass-reorder").FullName;
        // Red first second, blue second second.
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "color=c=red:s=64x64:r=30:d=1", "-f", "lavfi", "-i", "color=c=blue:s=64x64:r=30:d=1",
            "-filter_complex", "[0][1]concat=n=2,format=yuv420p", "-c:v", "libx264", "-qp", "0", Path.Combine(dir, "screen.mp4")])).Code);
        var m = new SessionManifest { Width = 64, Height = 64, Fps = 30, Complete = true };
        var p = StudioProject.FromSession(2, [], 64, 64).SplitAt(1).MoveSegment(1.5, -1) with { Zooms = [] };
        var outPath = Path.Combine(dir, "out.mp4");
        await Exporter.RunAsync(Exporter.Build(p, m, dir, [], EncoderProbe.Cpu, outPath, Path.Combine(dir, "g.txt")), Path.Combine(dir, "g.txt"), _ => { }, CancellationToken.None);

        Assert.True(await Blue(outPath, 0.5), "output should start with the blue clip");
        Assert.False(await Blue(outPath, 1.5), "output should end with the red clip");
    }

    private static async Task<bool> Blue(string file, double t)
    {
        var px = Path.Combine(Path.GetDirectoryName(file)!, $"px{t}.rgb");
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-ss", FormattableString.Invariant($"{t}"), "-i", file, "-frames:v", "1", "-vf", "scale=1:1", "-f", "rawvideo", "-pix_fmt", "rgb24", px])).Code);
        var rgb = File.ReadAllBytes(px);
        return rgb[2] > 150 && rgb[0] < 100;
    }
}
