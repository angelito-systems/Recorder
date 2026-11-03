using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Tests;

/// <summary>The recording as a rounded card on a background: geometry and a real export.</summary>
public class BackgroundTests
{
    [Fact]
    public void ScreenRectIsCenteredEvenAndOffWhenDisabled()
    {
        Assert.Equal((0, 0, 1920, 1080, 0), Layout.ScreenRect(1920, 1080, new BackgroundStyle()));
        var (x, y, w, h, r) = Layout.ScreenRect(1920, 1080, new BackgroundStyle(Enabled: true, Padding: 0.1, Radius: 0.02));
        Assert.Equal((1536, 864), (w, h));                    // 80% each way: aspect kept
        Assert.Equal((192, 108), (x, y));                     // centered
        Assert.Equal(22, r);                                  // 2% of the short side
        Assert.True(w % 2 == 0 && h % 2 == 0);
    }

    [Fact]
    public async Task ExportDrawsCardOnBackground()
    {
        var dir = TempDir.Create("ass-bg").FullName;
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "color=c=red:s=640x360:r=30:d=2", "-c:v", "libx264", "-qp", "0", "-pix_fmt", "yuv420p", Path.Combine(dir, "screen.mp4")])).Code);
        var m = new SessionManifest { Width = 640, Height = 360, Fps = 30, Complete = true };
        var p = new StudioProject
        {
            Segments = [new(0, 2)],
            Background = new BackgroundStyle(Enabled: true, Color1: "#0000FF", Color2: "#0000FF", Padding: 0.1, Radius: 0.1),
        };
        var outPath = Path.Combine(dir, "out.mp4");
        await Exporter.RunAsync(Exporter.Build(p, m, dir, [], EncoderProbe.Cpu, outPath, Path.Combine(dir, "g.txt")), Path.Combine(dir, "g.txt"), _ => { }, CancellationToken.None);

        Assert.Equal(2.0, await FfmpegProcess.ProbeDurationAsync(outPath), 1); // the repeated mask/gradient end with the video
        var (x, y, _, _, _) = Layout.ScreenRect(640, 360, p.Background);
        Assert.Equal("blue", await ColorAt(dir, outPath, 6, 6));          // margin
        Assert.Equal("red", await ColorAt(dir, outPath, 320, 180));       // the recording
        Assert.Equal("blue", await ColorAt(dir, outPath, x + 2, y + 2));  // rounded corner shows the background
        Assert.Equal("red", await ColorAt(dir, outPath, 320, y + 4));     // but the top edge is the card
    }

    private static async Task<string> ColorAt(string dir, string file, int px, int py)
    {
        var raw = Path.Combine(dir, $"px{px}_{py}.rgb");
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-ss", "1", "-i", file, "-frames:v", "1", "-vf", $"format=rgb24,crop=1:1:{px}:{py}", "-f", "rawvideo", "-pix_fmt", "rgb24", raw])).Code);
        var c = File.ReadAllBytes(raw);
        return c[0] > 150 && c[2] < 100 ? "red" : c[2] > 150 && c[0] < 100 ? "blue" : $"other({c[0]},{c[1]},{c[2]})";
    }
}
