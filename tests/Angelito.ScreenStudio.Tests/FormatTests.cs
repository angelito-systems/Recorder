using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Tests;

public class FormatTests
{
    private static async Task<(string Dir, SessionManifest M, StudioProject P)> Session()
    {
        var dir = TempDir.Create("ass-fmt").FullName;
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", Path.Combine(dir, "screen.mp4")])).Code);
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "sine=f=440:d=3", Path.Combine(dir, "mic.wav")])).Code);
        var m = new SessionManifest { Width = 640, Height = 360, Fps = 30, Mic = "mic.wav", Complete = true };
        var p = new StudioProject { Segments = [new(0, 1), new(2, 3)], Zooms = [new(0.2, 2.8, 1.5, 0.3, 0.3)] };
        return (dir, m, p);
    }

    private static async Task<string> Export(string dir, SessionManifest m, StudioProject p, EncoderChoice enc)
    {
        var outPath = Path.Combine(dir, "out" + p.Export.Extension);
        var graph = Path.Combine(dir, "g.txt");
        var plan = Exporter.Build(p, m, dir, [], enc, outPath, graph);
        await Exporter.RunAsync(plan, graph, _ => { }, CancellationToken.None);
        Assert.True(File.Exists(outPath));
        return outPath;
    }

    /// <summary>ffmpeg's own description of a file's streams.</summary>
    private static async Task<string> Info(string file)
    {
        var lines = new List<string>();
        using var f = new FfmpegProcess(["-i", file]);
        f.StdErr += l => { lock (lines) lines.Add(l); };
        f.Start();
        await f.WaitAsync();
        lock (lines) return string.Join("\n", lines);
    }

    [Fact]
    public async Task MkvH264()
    {
        var (dir, m, p) = await Session();
        var info = await Info(await Export(dir, m, p with { Export = new ExportSettings(Format: ExportFormat.MkvH264) }, EncoderProbe.Cpu));
        Assert.Contains("matroska", info);
        Assert.Contains("Video: h264", info);
        Assert.Contains("Audio: aac", info);
    }

    [Fact]
    public async Task Mp4Hevc()
    {
        var (dir, m, p) = await Session();
        var info = await Info(await Export(dir, m, p with { Export = new ExportSettings(Format: ExportFormat.Mp4Hevc, Quality: ExportQuality.Fast) }, EncoderProbe.CpuHevc));
        Assert.Contains("Video: hevc", info);
        Assert.Contains("hvc1", info);
    }

    [Fact]
    public async Task Mp4Av1DecodesBack()
    {
        var (dir, m, p) = await Session();
        var enc = await EncoderProbe.ForExportAsync(ExportFormat.Mp4Av1); // GPU if this machine has one, else libaom
        var out_ = await Export(dir, m, p with { Export = new ExportSettings(Format: ExportFormat.Mp4Av1, Quality: ExportQuality.Fast) }, enc);
        Assert.Contains("Video: av1", await Info(out_));
        // Playback check: the file must decode end to end without errors.
        var (code, log) = await FfmpegProcess.RunAsync(["-v", "error", "-i", out_, "-f", "null", "-"]);
        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(log), log);
    }

    [Fact]
    public async Task WavIsAudioOnlyWithCutsApplied()
    {
        var (dir, m, p) = await Session();
        var outPath = await Export(dir, m, p with { Export = new ExportSettings(Format: ExportFormat.Wav) }, EncoderProbe.Cpu);
        var info = await Info(outPath);
        Assert.DoesNotContain("Video:", info);
        Assert.Contains("pcm_s16le", info);
        Assert.Equal(2.0, await FfmpegProcess.ProbeDurationAsync(outPath), 1);
    }

    [Fact]
    public async Task PngStillHasOutputSize()
    {
        var (dir, m, p) = await Session();
        var still = p with { Canvas = new ProjectCanvas(CanvasAspect.Square), Export = new ExportSettings(720) };
        var info = await Info(await Export(dir, m, still.StillAt(1.5, 30), EncoderProbe.Cpu));
        Assert.Contains("Video: png", info);
        Assert.Contains("720x720", info);
    }

    [Fact]
    public async Task FpsOverride()
    {
        var (dir, m, p) = await Session();
        var info = await Info(await Export(dir, m, p with { Export = new ExportSettings(Fps: 15) }, EncoderProbe.Cpu));
        Assert.Contains("15 fps", info);
    }

    [Fact]
    public void EstimateScalesWithQualityAndFormat()
    {
        var m = new SessionManifest { Width = 1920, Height = 1080, Fps = 60 };
        var p = new StudioProject { Segments = [new(0, 60)] };
        var balanced = Exporter.EstimateBytes(p, m);
        Assert.InRange(balanced, 30_000_000, 60_000_000); // ~6 Mbit/s for a minute of 1080p60
        Assert.True(Exporter.EstimateBytes(p with { Export = new ExportSettings(Quality: ExportQuality.Max) }, m) > balanced);
        Assert.True(Exporter.EstimateBytes(p with { Export = new ExportSettings(Format: ExportFormat.Mp4Hevc) }, m) < balanced);
        Assert.Equal(60L * 48000 * 4, Exporter.EstimateBytes(p with { Export = new ExportSettings(Format: ExportFormat.Wav) }, m));
    }
}
