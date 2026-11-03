using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Tests;

public class CanvasTests
{
    [Fact]
    public void VerticalWindowAndOutputSizes()
    {
        var m = new SessionManifest { Width = 1920, Height = 1080, Fps = 30 };
        var v = new ProjectCanvas(CanvasAspect.Vertical, 0.2);
        Assert.Equal((607.5, 1080), v.Window(1920, 1080));
        Assert.Equal((1080, 1920), Layout.OutputSize(m, new ExportSettings(1080), v));
        Assert.Equal((608, 1080), Layout.OutputSize(m, new ExportSettings(0), v));
        Assert.Equal((1920, 1080), Layout.OutputSize(m, new ExportSettings(2160), new ProjectCanvas())); // source never upscales
        Assert.Equal((1080, 1080), Layout.OutputSize(m, new ExportSettings(1080), new ProjectCanvas(CanvasAspect.Square)));
        var (x, _, w, _) = ZoomMath.ViewRect([], 0, 1920, 1080, v);
        Assert.Equal(1920 * 0.2 - 607.5 / 2, x, 3); // framing center is honored
        Assert.Equal(0, ZoomMath.ViewRect([], 0, 1920, 1080, v with { X = 0 }).X); // and clamped to the frame
        Assert.Equal(607.5, w, 3);
    }

    [Fact]
    public void ZoomOnCanvasKeepsAspect()
    {
        var (_, _, w, h) = ZoomMath.ViewRect([new(0, 4, 2, 0.8, 0.5)], 2, 1920, 1080, new ProjectCanvas(CanvasAspect.Vertical));
        Assert.Equal(9 / 16.0, w / h, 6);
        Assert.Equal(540, h, 3);
    }

    /// <summary>Preview/export parity: the rendered frame mid-zoom equals the rectangle the preview shows.</summary>
    [Theory]
    [InlineData(CanvasAspect.Source, false, false)]
    [InlineData(CanvasAspect.Vertical, false, false)]
    [InlineData(CanvasAspect.Source, true, false)]
    [InlineData(CanvasAspect.Vertical, true, false)]
    [InlineData(CanvasAspect.Source, false, true)]
    [InlineData(CanvasAspect.Source, true, true)]
    public async Task ExportMatchesViewRect(CanvasAspect aspect, bool gpu, bool autoPan = false)
    {
        var vk = gpu ? await EncoderProbe.CameraGpuAsync() : -1;
        if (gpu && vk < 0) return; // no libplacebo/Vulkan GPU faster than the CPU here: the CPU path is what ships
        var dir = TempDir.Create("ass-canvas").FullName;
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=3", "-c:v", "libx264", "-qp", "0", "-pix_fmt", "yuv420p", Path.Combine(dir, "screen.mp4")])).Code);
        var m = new SessionManifest { Width = 1280, Height = 720, Fps = 30, Complete = true };
        var p = new StudioProject
        {
            Segments = [new(0, 3)],
            // autoPan: auto-zoom on two far clicks, so frame 36 (1.2 s) is mid-pan between overlapping, averaged segments.
            Zooms = autoPan
                ? AutoZoom.Propose([new(0.5, 200, 150, CursorKind.Left), new(1.4, 1100, 600, CursorKind.Left)], 1280, 720, 3)
                : [new(0.5, 2.5, 2, 0.7, 0.35, 0.5)],
            Canvas = new ProjectCanvas(aspect, 0.4), Export = new ExportSettings(0, ExportQuality.High),
        };
        var outPath = Path.Combine(dir, "out.mp4");
        var plan = Exporter.Build(p, m, dir, [], EncoderProbe.Cpu, outPath, Path.Combine(dir, "g.txt"), vulkanDevice: vk);
        await Exporter.RunAsync(plan, Path.Combine(dir, "g.txt"), _ => { }, CancellationToken.None);

        var (ow, oh) = Layout.OutputSize(m, p.Export, p.Canvas);
        async Task<double> Compare(int frame, int viewFrame)
        {
            var (x, y, w, h) = ZoomMath.ViewRect(p.Zooms, viewFrame / 30.0, 1280, 720, p.Canvas);
            var crop = FormattableString.Invariant($"crop={w:F2}:{h:F2}:{x:F2}:{y:F2}:exact=1,scale={ow}:{oh}:flags=bicubic");
            return await Psnr(outPath, Path.Combine(dir, "screen.mp4"), $"select=eq(n\\,{frame})", $"select=eq(n\\,{frame}),{crop}");
        }
        if (autoPan) Assert.Equal(ZoomState.Tracking, AutoZoom.StateAt(p.Zooms, 36 / 30.0));
        var parity = await Compare(36, 36);   // ramping in (or panning): position, scale and aspect all moving
        var wrong = await Compare(36, 24);    // the view of a different moment
        Assert.True(parity > 30, $"parity PSNR {parity:F1}");
        Assert.True(parity - wrong > 6, $"test can't tell frames apart ({parity:F1} vs {wrong:F1})");
    }

    private static async Task<double> Psnr(string a, string b, string fa, string fb)
    {
        double result = double.NaN;
        using var f = new FfmpegProcess(["-i", a, "-i", b, "-lavfi", $"[0:v]{fa}[x];[1:v]{fb}[y];[x][y]psnr", "-f", "null", "-"]);
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
