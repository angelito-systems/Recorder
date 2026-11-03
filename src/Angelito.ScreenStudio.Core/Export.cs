using System.Globalization;
using System.Text;

namespace Angelito.ScreenStudio.Core;

/// <summary>Placement math shared by the editor preview and the exporter.</summary>
public static class Layout
{
    public static (int W, int H) OutputSize(SessionManifest m, ExportSettings e, ProjectCanvas? canvas = null)
    {
        canvas ??= new ProjectCanvas();
        var (cw, ch) = canvas.Window(m.Width, m.Height);
        var shortSide = Math.Min(cw, ch);
        // Source shape never upscales (as before); social formats get the exact size asked for (e.g. 1080×1920).
        if (e.Height <= 0 || (canvas.Aspect == CanvasAspect.Source && e.Height >= shortSide)) return (Even(cw), Even(ch));
        var k = e.Height / shortSide;
        return (Even(cw * k), Even(ch * k));
    }

    /// <summary>Size unit for webcam and text: the output's short side, so a vertical frame isn't overfilled.</summary>
    public static int Unit(int outW, int outH) => Math.Min(outW, outH);

    public static (int X, int Y, int D) WebcamRect(int outW, int outH, WebcamLayout l)
    {
        var d = Math.Clamp(Even(l.Size * Unit(outW, outH)), 16, Even(Math.Min(outW, outH)));
        return ((int)Math.Clamp(l.X * outW - d / 2.0, 0, outW - d), (int)Math.Clamp(l.Y * outH - d / 2.0, 0, outH - d), d);
    }

    public static double CornerRadius(WebcamShape s, int d) => s == WebcamShape.Circle ? d / 2.0 : Math.Round(d * 0.18);

    public static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
}

/// <summary>Files maps auxiliary file paths (text overlays) to their contents; they are written next to the graph before running.</summary>
public sealed record ExportPlan(List<string> Args, string FilterGraph, double Duration, Dictionary<string, string> Files);

/// <summary>Turns a project into one ffmpeg invocation (single process, whole graph), mirroring the preview's layer order:
/// screen → annotations → click rings → camera (canvas + zoom) → webcam → texts → cuts → encode.</summary>
public static class Exporter
{
    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static ExportPlan Build(StudioProject p, SessionManifest m, string sessionDir, IReadOnlyList<CursorEvent> cursor,
        EncoderChoice enc, string outPath, string graphPath, Func<int, (string Png, int X, int Y)?>? annotationImage = null)
    {
        if (p.Segments.Count == 0) throw new InvalidOperationException("No hay segmentos para exportar.");
        var (ow, oh) = Layout.OutputSize(m, p.Export, p.Canvas);
        var fps = Fps(p, m);
        var fmt = p.Export.Format;
        bool video = fmt != ExportFormat.Wav, audioOut = fmt != ExportFormat.Png; // WAV never decodes video
        var g = new StringBuilder();
        var args = new List<string> { "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        var input = 0;
        var srcEnd = p.Segments.Max(s => s.End) + 1;
        var files = new Dictionary<string, string>();
        var last = "v0";
        if (video)
        {
            args.AddRange(["-i", Path.Combine(sessionDir, m.Screen)]);
            input++;

            g.Append(F($"[0:v]fps={fps},setpts=PTS-STARTPTS,format=yuv420p[v0];\n"));

            // Annotations in source pixels, so they stay on the content under zoom (same as preview).
            // Masks are computed here; vector shapes come pre-rendered by the UI (the preview's own drawing) as PNGs.
            for (var i = 0; i < p.Annotations.Count; i++)
            {
                var a = p.Annotations[i];
                var en = F($"enable='between(t,{a.Start:F3},{a.End:F3})'");
                if (a.IsMask)
                {
                    var (bx, by, bw, bh) = a.BoxPx(m.Width, m.Height);
                    // yuv420p crops must be even; grow outward so the mask never uncovers an edge pixel.
                    int x0 = Math.Max(0, bx & ~1), y0 = Math.Max(0, by & ~1);
                    int w = Math.Min(m.Width - x0, Layout.Even(bx + bw - x0 + 1)), h = Math.Min(m.Height - y0, Layout.Even(by + bh - y0 + 1));
                    if (w < 4 || h < 4) continue;
                    var fx = a.Kind == AnnotationKind.Blur
                        ? BlurChain(w, h)
                        : F($"scale={Math.Max(1, w / PixelBlock)}:{Math.Max(1, h / PixelBlock)}:flags=area,scale={w}:{h}:flags=neighbor");
                    g.Append(F($"[{last}]split[ma{i}][mb{i}];[mb{i}]crop={w}:{h}:{x0}:{y0},{fx}[mc{i}];[ma{i}][mc{i}]overlay={x0}:{y0}:{en}[an{i}];\n"));
                    last = $"an{i}";
                }
                else if (annotationImage?.Invoke(i) is { } img)
                {
                    args.AddRange(["-i", img.Png]);
                    g.Append(F($"[{last}][{input}:v]overlay={img.X}:{img.Y}:{en}[an{i}];\n"));
                    last = $"an{i}";
                    input++;
                }
            }

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

            // Camera: canvas window moved by zooms → output size. perspective samples sub-pixel (no zoompan shimmer);
            // without zooms the window is static and a plain crop is much cheaper.
            if (p.Zooms.Count == 0)
            {
                var (sx, sy, sw, sh) = ZoomMath.ViewRect(p.Zooms, 0, m.Width, m.Height, p.Canvas);
                g.Append(F($"[{last}]crop={Math.Round(sw)}:{Math.Round(sh)}:{Math.Round(sx)}:{Math.Round(sy)}:exact=1,scale={ow}:{oh}:flags=bicubic,setsar=1[vz];\n"));
            }
            else
            {
                var (vx, vy, vw, vh) = ZoomMath.FfmpegViewExpr(p.Zooms, p.Canvas, m.Width, m.Height, fps);
                g.Append($"[{last}]perspective=x0='{vx}':y0='{vy}':x1='{vx}+{vw}':y1='{vy}':x2='{vx}':y2='{vy}+{vh}':x3='{vx}+{vw}':y3='{vy}+{vh}':eval=frame:sense=source,");
                g.Append(F($"scale={ow}:{oh}:flags=bicubic,setsar=1[vz];\n"));
            }
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
                var px = Math.Max(8, (int)Math.Round(size * Layout.Unit(ow, oh)));
                var (r, gg, b) = ParseColor(color);
                g.Append(F($"[{last}]drawtext=fontfile='{FilterPath(Font(bold))}':textfile='{FilterPath(file)}':expansion=none:text_align=C:fontsize={px}:fontcolor=0x{r:X2}{gg:X2}{b:X2}"));
                if (box) g.Append(F($":box=1:boxcolor=black@0.55:boxborderw={TextPad(px)}"));
                g.Append(F($":x=({cx}*w-text_w/2):y=({cy}*h-text_h/2):enable='between(t,{start:F3},{end:F3})'[x{n}];\n"));
                last = $"x{n++}";
            }
            foreach (var t in p.Texts) Text(t.Start, t.End, t.Text, t.X, t.Y, t.Size, t.Color, t.Background, t.Bold);
            if (p.SubtitleStyle.Burn)
                foreach (var c in p.Subtitles) Text(c.Start, c.End, c.Text, 0.5, p.SubtitleStyle.Y, p.SubtitleStyle.Size, p.SubtitleStyle.Color, p.SubtitleStyle.Background, false);
        }

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
        if (audioOut)
        {
            AddAudio(m.Mic, m.MicOffset, p.Audio.MicSync, p.Audio.MicGainDb, p.Audio.MicMuted, "am");
            AddAudio(m.SystemAudio, m.SystemAudioOffset, p.Audio.SystemSync, p.Audio.SystemGainDb, p.Audio.SystemMuted, "as");
            g.Append(audio.Count switch
            {
                0 => "anullsrc=r=48000:cl=stereo[amix];\n",
                1 => $"[{audio[0]}]apad[amix];\n",
                _ => $"[{audio[0]}][{audio[1]}]amix=inputs=2:normalize=0:duration=longest,apad[amix];\n",
            });
        }

        // Cuts: keep the listed source ranges, in order.
        var k = p.Segments.Count;
        if (video)
        {
            g.Append(F($"[{last}]format={(fmt == ExportFormat.Png ? "rgb24" : enc.CpuPixFmt)},split={k}"));
            for (var i = 0; i < k; i++) g.Append($"[s{i}]");
            g.Append(";\n");
        }
        if (audioOut)
        {
            g.Append(F($"[amix]asplit={k}"));
            for (var i = 0; i < k; i++) g.Append($"[t{i}]");
            g.Append(";\n");
        }
        for (var i = 0; i < k; i++)
        {
            var s = p.Segments[i];
            if (video) g.Append(F($"[s{i}]trim=start={s.Start:F4}:end={s.End:F4},setpts=PTS-STARTPTS[k{i}];\n"));
            if (audioOut) g.Append(F($"[t{i}]atrim=start={s.Start:F4}:end={s.End:F4},asetpts=PTS-STARTPTS[ka{i}];\n"));
        }
        for (var i = 0; i < k; i++) g.Append((video ? $"[k{i}]" : "") + (audioOut ? $"[ka{i}]" : ""));
        g.Append(F($"concat=n={k}:v={(video ? 1 : 0)}:a={(audioOut ? 1 : 0)}{(video ? "[vout]" : "")}{(audioOut ? "[aout]" : "")}"));

        args.AddRange(["-/filter_complex", graphPath]);
        if (video) args.AddRange(["-map", "[vout]"]);
        if (audioOut) args.AddRange(["-map", "[aout]"]);
        switch (fmt)
        {
            case ExportFormat.Wav: args.AddRange(["-c:a", "pcm_s16le", outPath]); break;
            case ExportFormat.Png: args.AddRange(["-frames:v", "1", "-update", "1", "-c:v", "png", outPath]); break;
            default:
                args.AddRange(enc.QualityArgs(p.Export.Quality));
                if (enc.Hevc) args.AddRange(["-tag:v", "hvc1"]); // plays in Apple/Windows players
                args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
                if (fmt != ExportFormat.MkvH264) args.AddRange(["-movflags", "+faststart"]);
                args.Add(outPath);
                break;
        }        return new ExportPlan(args, g.ToString(), p.OutputDuration, files);
    }

    public static int Fps(StudioProject p, SessionManifest m) => Math.Clamp(p.Export.Fps > 0 ? p.Export.Fps : m.Fps, 1, 240);

    /// <summary>
    /// Rough output size. Encoding is quality-targeted (CQ/CRF), so the real size depends on the content; screen
    /// recordings usually compress better than this. Shown to the user as an approximation.
    /// </summary>
    public static long EstimateBytes(StudioProject p, SessionManifest m)
    {
        var dur = p.OutputDuration;
        if (p.Export.Format == ExportFormat.Wav) return (long)(dur * 48000 * 2 * 2);
        var (w, h) = Layout.OutputSize(m, p.Export, p.Canvas);
        if (p.Export.Format == ExportFormat.Png) return (long)(w * h * 1.5);
        var bpp = p.Export.Quality switch { ExportQuality.Fast => 0.03, ExportQuality.High => 0.08, ExportQuality.Max => 0.12, _ => 0.05 };
        if (p.Export.Format == ExportFormat.Mp4Hevc) bpp *= 0.65;
        return (long)(dur * (bpp * w * h * Fps(p, m) + 192_000) / 8);
    }

    /// <summary>Shifts a stream that started <paramref name="delay"/> seconds after the screen video.</summary>
    private static string Align(double delay, bool audio)
    {
        if (Math.Abs(delay) < 0.0005) return "";
        if (audio)
            return delay > 0 ? F($"adelay={delay * 1000:F0}:all=1,") : F($"atrim=start={-delay:F4},asetpts=PTS-STARTPTS,");
        return delay > 0 ? F($"tpad=start_duration={delay:F4}:start_mode=clone,") : F($"trim=start={-delay:F4},setpts=PTS-STARTPTS,");
    }

    public const int PixelBlock = 16;

    public const int BlurDownscale = 6;

    /// <summary>
    /// Privacy blur whose strength doesn't depend on the region size: boxblur's radius is capped by the plane size
    /// (yuv420p chroma is half size), so small regions are shrunk first, blurred, then scaled back up.
    /// </summary>
    private static string BlurChain(int w, int h)
    {
        int sw = Math.Max(2, Layout.Even(w / (double)BlurDownscale)), sh = Math.Max(2, Layout.Even(h / (double)BlurDownscale));
        var r = Math.Min(6, Math.Min(sw, sh) / 4 - 1);
        var blur = r >= 1 ? F($",boxblur={r}:3") : "";
        return F($"scale={sw}:{sh}:flags=area{blur},scale={w}:{h}:flags=bicubic");
    }

    /// <summary>Preview BlurEffect radius approximating <see cref="BlurChain"/>.</summary>
    public static double PreviewBlurRadius(int w, int h) => BlurDownscale * (Math.Clamp(Math.Min(w, h) / BlurDownscale / 4 - 1, 1, 6) + 2) * 2;

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
