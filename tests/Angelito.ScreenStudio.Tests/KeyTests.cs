using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Tests;

/// <summary>The keystroke filter is a privacy boundary: these tests pin what may and may not be stored.</summary>
public class KeyFilterTests
{
    private const int VK_S = 0x53, VK_A = 0x41, VK_2 = 0x32, VK_TAB = 0x09, VK_F5 = 0x74, VK_F12 = 0x7B, VK_Q = 0x51, VK_LCTRL = 0xA2;

    [Fact]
    public void AllowedShortcutsPass()
    {
        var f = new KeyFilter();
        Assert.Equal("Ctrl+S", f.Accept(VK_S, Mods.Ctrl, "code"));
        Assert.Equal("Alt+Tab", f.Accept(VK_TAB, Mods.Alt, "explorer"));
        Assert.Equal("F5", f.Accept(VK_F5, Mods.None, "chrome"));
        Assert.Equal("Ctrl+Shift+P", f.Accept(0x50, Mods.Ctrl | Mods.Shift, "code"));
    }

    [Fact]
    public void TypingIsNeverStored()
    {
        var f = new KeyFilter();
        Assert.Null(f.Accept(VK_A, Mods.None, "notepad"));           // letters
        Assert.Null(f.Accept(VK_A, Mods.Shift, "notepad"));          // capitals
        Assert.Null(f.Accept(VK_2, Mods.None, "notepad"));           // digits
        Assert.Null(f.Accept(0x20, Mods.None, "notepad"));           // space
        Assert.Null(f.Accept(VK_LCTRL, Mods.Ctrl, "notepad"));       // modifiers alone
        Assert.Null(f.Accept(VK_Q, Mods.Ctrl | Mods.Alt | Mods.AltGr, "notepad")); // AltGr+Q = "@" on Spanish layouts
        Assert.Null(f.Accept(VK_2, Mods.Ctrl | Mods.Alt, "notepad")); // Ctrl+Alt+2 = AltGr+2 = "@"
        Assert.Null(f.Accept(0x4D, Mods.Alt, "notepad"));             // Alt+M: not on the list
    }

    [Fact]
    public void ExcludedAppsAndSuspend()
    {
        var f = new KeyFilter(extraExcludedApps: ["MiBanco"]);
        Assert.Null(f.Accept(VK_S, Mods.Ctrl, "KeePassXC"));
        Assert.Null(f.Accept(VK_S, Mods.Ctrl, "mibanco"));
        Assert.Null(f.Accept(VK_F12, Mods.Ctrl | Mods.Shift, "code")); // the toggle itself is never logged
        Assert.True(f.Suspended);
        Assert.Null(f.Accept(VK_S, Mods.Ctrl, "code"));
        f.Accept(VK_F12, Mods.Ctrl | Mods.Shift, "code");
        Assert.Equal("Ctrl+S", f.Accept(VK_S, Mods.Ctrl, "code"));
    }

    [Fact]
    public void UserCombosAreNormalizedAndCannotEnableTyping()
    {
        var f = new KeyFilter(extraAllowed: ["shift+ctrl+pageup", "AltGr+Q", "nonsense+key"]);
        Assert.Equal("Ctrl+Shift+PageUp", f.Accept(0x21, Mods.Ctrl | Mods.Shift, "code"));
        Assert.Null(f.Accept(VK_Q, Mods.Ctrl | Mods.Alt | Mods.AltGr, "code"));
        Assert.Null(KeyFilter.Normalize("Ctrl+Nonsense"));
    }

    [Fact]
    public void WindowsReplaceAndExpire()
    {
        var w = KeyLog.Windows([new(1, "Ctrl+C"), new(1.5, "Ctrl+V"), new(5, "Ctrl+S")], 1.2);
        Assert.Equal([(1, 1.5, "Ctrl+C"), (1.5, 2.7, "Ctrl+V"), (5, 6.2, "Ctrl+S")], w.Select(x => (x.Start, Math.Round(x.End, 6), x.Combo)));
        Assert.True(KeyLog.TryParse(KeyLog.Format(new KeyEvent(2.5, "Ctrl+Shift+S")), out var back));
        Assert.Equal(new KeyEvent(2.5, "Ctrl+Shift+S"), back);
    }

    [Fact]
    public async Task ExportOverlaysKeystrokes()
    {
        var dir = Directory.CreateTempSubdirectory("ass-keys").FullName;
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", Path.Combine(dir, "screen.mp4")])).Code);
        var png = Path.Combine(dir, "k.png");
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "color=c=white:s=120x40,format=rgba", "-frames:v", "1", png])).Code);
        var m = new SessionManifest { Width = 640, Height = 360, Fps = 30, Complete = true };
        var p = new StudioProject { Segments = [new(0, 3)] };
        var outPath = Path.Combine(dir, "out.mp4");
        var plan = Exporter.Build(p, m, dir, [], EncoderProbe.Cpu, outPath, Path.Combine(dir, "g.txt"),
            keys: [new(0.5, "Ctrl+C"), new(1.0, "Ctrl+V"), new(2.0, "Ctrl+C")], keyImage: _ => (png, 120, 40));
        Assert.Equal(3, plan.FilterGraph.Split('\n').Count(l => l.Contains("[kq") && l.Contains("overlay="))); // one overlay per showing
        Assert.Equal(2, plan.Args.Count(a => a == png));           // one image input per distinct shortcut
        await Exporter.RunAsync(plan, Path.Combine(dir, "g.txt"), _ => { }, CancellationToken.None);

        // Box centered at (0.5, 0.8) of 640×360 → x 260..380, y 268..308. White while shown (0.5–1.0 s), absent before.
        Assert.True(await WhiteAt(outPath, 0.7) > 30, "keystroke not drawn");
        Assert.True(await WhiteAt(outPath, 0.2) < 15, "keystroke drawn outside its window");
    }

    private static async Task<double> WhiteAt(string file, double t)
    {
        double result = double.NaN;
        var sel = FormattableString.Invariant($"select=eq(n\\,{(int)Math.Round(t * 30)})");
        using var f = new FfmpegProcess(["-i", file, "-f", "lavfi", "-i", "color=c=white:s=100x30",
            "-lavfi", $"[0:v]{sel},crop=100:30:270:273,format=yuv420p[a];[1:v]format=yuv420p[b];[a][b]psnr", "-frames:v", "1", "-f", "null", "-"]);
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
