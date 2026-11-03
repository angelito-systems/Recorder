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
