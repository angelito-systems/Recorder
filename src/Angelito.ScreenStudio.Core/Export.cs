using System.Globalization;
using System.Text;

namespace Angelito.ScreenStudio.Core;

/// <summary>Placement math shared by the editor preview and the exporter.</summary>
public static class Layout
{
    public static (int W, int H) OutputSize(SessionManifest m, ExportSettings e)
    {
        if (e.Height <= 0 || e.Height >= m.Height) return (Even(m.Width), Even(m.Height));
        return (Even(m.Width * (double)e.Height / m.Height), Even(e.Height));
    }

    public static (int X, int Y, int D) WebcamRect(int outW, int outH, WebcamLayout l)
    {
        var d = Math.Clamp(Even(l.Size * outH), 16, Even(Math.Min(outW, outH)));
        return ((int)Math.Clamp(l.X * outW - d / 2.0, 0, outW - d), (int)Math.Clamp(l.Y * outH - d / 2.0, 0, outH - d), d);
    }

    public static double CornerRadius(WebcamShape s, int d) => s == WebcamShape.Circle ? d / 2.0 : Math.Round(d * 0.18);

    public static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
}

/// <summary>Files maps auxiliary file paths (text overlays) to their contents; they are written next to the graph before running.</summary>
public sealed record ExportPlan(List<string> Args, string FilterGraph, double Duration, Dictionary<string, string> Files);

/// <summary>Turns a project into one ffmpeg invocation (single process, whole graph), mirroring the preview's layer order:
/// screen → click rings → zoom → webcam → cuts → encode.</summary>
public static class Exporter
{
    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static ExportPlan Build(StudioProject p, SessionManifest m, string sessionDir, IReadOnlyList<CursorEvent> cursor,
        EncoderChoice enc, string outPath, string graphPath)
    {
        if (p.Segments.Count == 0) throw new InvalidOperationException("No hay segmentos para exportar.");
        var (ow, oh) = Layout.OutputSize(m, p.Export);
        int fps = Math.Clamp(m.Fps, 1, 240);
        var g = new StringBuilder();
        var args = new List<string> { "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        var input = 0;
        args.AddRange(["-i", Path.Combine(sessionDir, m.Screen)]);
        input++;

        var srcEnd = p.Segments.Max(s => s.End) + 1;
        g.Append(F($"[0:v]fps={fps},setpts=PTS-STARTPTS,format=yuv420p[v0];\n"));
        var last = "v0";

        // Click rings, drawn in source pixels so they zoom with the content (same as preview).
        var clicks = p.Cursor.ShowClicks ? cursor.Where(c => c.K != CursorKind.Move && c.T >= 0 && c.T < srcEnd).ToList() : [];
        if (clicks.Count > 0)
        {
            var sz = Layout.Even(p.Cursor.Size);
            var (r, gg, b) = ParseColor(p.Cursor.Color);
            g.Append(F($"color=c=black@0:s={sz}x{sz}:r={fps}:d={srcEnd},format=rgba,geq=r={r}:g={gg}:b={b}:a='if(between(hypot(X-W/2,Y-H/2),W/2-{Math.Max(3, sz / 10)},W/2-1),230,0)',split={clicks.Count}"));
            for (var i = 0; i < clicks.Count; i++) g.Append($"[r{i}]");
            g.Append(";\n");
            for (var i = 0; i < clicks.Count; i++)
            {
                var c = clicks[i];
                g.Append(F($"[{last}][r{i}]overlay=x={c.X - sz / 2}:y={c.Y - sz / 2}:enable='between(t,{c.T:F3},{c.T + p.Cursor.Duration:F3})'[c{i}];\n"));
                last = $"c{i}";
            }
        }

        // shortcut: zoompan crops on integer pixels, so slow zooms can shimmer slightly; upgrade to a GPU compositor in the optimization phase.
        var (z, x, y) = ZoomMath.FfmpegZoomExpr(p.Zooms);
        g.Append(F($"[{last}]zoompan=z='{z}':x='{x}':y='{y}':d=1:s={ow}x{oh}:fps={fps}[vz];\n"));
        last = "vz";

        if (m.Webcam != null && p.Webcam.Visible && File.Exists(Path.Combine(sessionDir, m.Webcam)))
        {
            args.AddRange(["-i", Path.Combine(sessionDir, m.Webcam)]);
            var (cx, cy, d) = Layout.WebcamRect(ow, oh, p.Webcam);
            var rad = Layout.CornerRadius(p.Webcam.Shape, d);
            var mask = F($"if(lte(hypot(max(abs(X-W/2)-(W/2-{rad}),0),max(abs(Y-H/2)-(H/2-{rad}),0)),{rad}),255,0)");
            g.Append(F($"[{input}:v]fps={fps},setpts=PTS-STARTPTS,{Align(m.WebcamOffset - m.ScreenOffset, false)}scale={d}:{d}:force_original_aspect_ratio=increase,crop={d}:{d},{(p.Webcam.Mirror ? "hflip," : "")}format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='{mask}'[cam];\n"));
            g.Append(F($"[{last}][cam]overlay=x={cx}:y={cy}:eof_action=pass[vc];\n"));
            last = "vc";
            input++;
        }

        // Texts and burned subtitles: screen-space, above the webcam, on source time like the rest.
        var files = new Dictionary<string, string>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(graphPath))!;
        var n = 0;
        void Text(double start, double end, string text, double cx, double cy, double size, string color, bool box, bool bold)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (text.Length == 0 || end <= start) return;
            // textfile + expansion=none: no escaping of user text inside the filtergraph.
            var file = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(graphPath)}.text{n}.txt");
            // shortcut: FFmpeg 8 drawtext draws a missing-glyph box for LF but breaks cleanly on CR; re-check when upgrading FFmpeg.
            files[file] = text.Replace('\n', '\r');
            var px = Math.Max(8, (int)Math.Round(size * oh));
            var (r, gg, b) = ParseColor(color);
            g.Append(F($"[{last}]drawtext=fontfile='{FilterPath(Font(bold))}':textfile='{FilterPath(file)}':expansion=none:text_align=C:fontsize={px}:fontcolor=0x{r:X2}{gg:X2}{b:X2}"));
            if (box) g.Append(F($":box=1:boxcolor=black@0.55:boxborderw={TextPad(px)}"));
            g.Append(F($":x=({cx}*w-text_w/2):y=({cy}*h-text_h/2):enable='between(t,{start:F3},{end:F3})'[x{n}];\n"));
            last = $"x{n++}";
        }
        foreach (var t in p.Texts) Text(t.Start, t.End, t.Text, t.X, t.Y, t.Size, t.Color, t.Background, t.Bold);
        if (p.SubtitleStyle.Burn)
            foreach (var c in p.Subtitles) Text(c.Start, c.End, c.Text, 0.5, p.SubtitleStyle.Y, p.SubtitleStyle.Size, p.SubtitleStyle.Color, p.SubtitleStyle.Background, false);

        // Audio: every source aligned to screen time, mixed, padded so cuts never run short.
        var audio = new List<string>();
        void AddAudio(string? file, double offset, double sync, double gainDb, bool muted, string label)
        {
            if (file == null || muted || !File.Exists(Path.Combine(sessionDir, file))) return;
            args.AddRange(["-i", Path.Combine(sessionDir, file)]);
            g.Append(F($"[{input}:a]asetpts=PTS-STARTPTS,{Align(offset - m.ScreenOffset + sync, true)}volume={gainDb}dB,aresample=48000,aformat=channel_layouts=stereo[{label}];\n"));
            audio.Add(label);
            input++;
        }
        AddAudio(m.Mic, m.MicOffset, p.Audio.MicSync, p.Audio.MicGainDb, p.Audio.MicMuted, "am");
        AddAudio(m.SystemAudio, m.SystemAudioOffset, p.Audio.SystemSync, p.Audio.SystemGainDb, p.Audio.SystemMuted, "as");
        g.Append(audio.Count switch
        {
            0 => "anullsrc=r=48000:cl=stereo[amix];\n",
            1 => $"[{audio[0]}]apad[amix];\n",
            _ => $"[{audio[0]}][{audio[1]}]amix=inputs=2:normalize=0:duration=longest,apad[amix];\n",
        });

        // Cuts: keep the listed source ranges, in order.
        var k = p.Segments.Count;
        g.Append(F($"[{last}]format={enc.CpuPixFmt},split={k}"));
        for (var i = 0; i < k; i++) g.Append($"[s{i}]");
        g.Append(F($";\n[amix]asplit={k}"));
        for (var i = 0; i < k; i++) g.Append($"[t{i}]");
        g.Append(";\n");
        for (var i = 0; i < k; i++)
        {
            var s = p.Segments[i];
            g.Append(F($"[s{i}]trim=start={s.Start:F4}:end={s.End:F4},setpts=PTS-STARTPTS[k{i}];\n"));
            g.Append(F($"[t{i}]atrim=start={s.Start:F4}:end={s.End:F4},asetpts=PTS-STARTPTS[ka{i}];\n"));
        }
        for (var i = 0; i < k; i++) g.Append($"[k{i}][ka{i}]");
        g.Append(F($"concat=n={k}:v=1:a=1[vout][aout]"));

        args.AddRange(["-/filter_complex", graphPath, "-map", "[vout]", "-map", "[aout]"]);
        args.AddRange(enc.QualityArgs(p.Export.Quality));
        args.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outPath]);
        return new ExportPlan(args, g.ToString(), p.OutputDuration, files);
    }

    /// <summary>Shifts a stream that started <paramref name="delay"/> seconds after the screen video.</summary>
    private static string Align(double delay, bool audio)
    {
        if (Math.Abs(delay) < 0.0005) return "";
        if (audio)
            return delay > 0 ? F($"adelay={delay * 1000:F0}:all=1,") : F($"atrim=start={-delay:F4},asetpts=PTS-STARTPTS,");
        return delay > 0 ? F($"tpad=start_duration={delay:F4}:start_mode=clone,") : F($"trim=start={-delay:F4},setpts=PTS-STARTPTS,");
    }

    /// <summary>Box padding around text, shared with the preview.</summary>
    public static int TextPad(int fontPx) => Math.Max(2, (int)Math.Round(fontPx * 0.3));

    public static string Font(bool bold) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), bold ? "segoeuib.ttf" : "segoeui.ttf");

    /// <summary>Path inside a quoted filter option: forward slashes, escaped drive colon and quotes.</summary>
    private static string FilterPath(string path) => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    public static (int R, int G, int B) ParseColor(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return (139, 92, 246);
        return ((v >> 16) & 255, (v >> 8) & 255, v & 255);
    }

    public static async Task RunAsync(ExportPlan plan, string graphPath, Action<double> progress01, CancellationToken ct)
    {
        await File.WriteAllTextAsync(graphPath, plan.FilterGraph, ct);
        foreach (var (path, text) in plan.Files) await File.WriteAllTextAsync(path, text, ct);
        try
        {
            var (code, log) = await FfmpegProcess.RunAsync(plan.Args, s => progress01(Math.Clamp(s / Math.Max(0.01, plan.Duration), 0, 1)), ct);
            if (code != 0) throw new InvalidOperationException("La exportación falló:\n" + log);
        }
        finally
        {
            foreach (var path in plan.Files.Keys) try { File.Delete(path); } catch (IOException) { }
        }
    }
}
