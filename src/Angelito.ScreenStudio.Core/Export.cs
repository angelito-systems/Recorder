using System.Globalization;
using System.Text;

namespace Angelito.ScreenStudio.Core;

/// <summary>Placement math shared by the editor preview and the exporter.</summary>
public static class Layout
{
    /// <summary>Where the screen card sits on an ow×oh output with this background: even size, centered, corner radius.</summary>
    public static (int X, int Y, int W, int H, int R) ScreenRect(int ow, int oh, BackgroundStyle bg)
    {
        if (!bg.Enabled) return (0, 0, ow, oh, 0);
        var k = 1 - 2 * Math.Clamp(bg.Padding, 0, 0.3);
        int w = Even(ow * k), h = Even(oh * k);
        var r = (int)Math.Round(Math.Clamp(bg.Radius, 0, 0.2) * Math.Min(ow, oh));
        return ((ow - w) / 2, (oh - h) / 2, w, h, Math.Min(r, Math.Min(w, h) / 2));
    }

    /// <summary>Fade-in/out length for texts, subtitles and shortcuts; at most half of a short item.</summary>
    public static double FadeLength(double start, double end) => Math.Min(0.2, (end - start) / 2);

    /// <summary>Opacity at time t of an item shown from start to end (0 outside). Same curve as the export's alpha.</summary>
    public static double FadeAlpha(double t, double start, double end) =>
        t < start || t > end ? 0 : Math.Clamp(Math.Min(t - start, end - t) / Math.Max(1e-3, FadeLength(start, end)), 0, 1);

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
        EncoderChoice enc, string outPath, string graphPath, Func<int, (string Png, int X, int Y)?>? annotationImage = null,
        IReadOnlyList<KeyEvent>? keys = null, Func<string, (string Png, int W, int H)?>? keyImage = null, int vulkanDevice = -1,
        CursorTrack? pointer = null)
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

            // The real cursor, in source pixels (so it zooms with the content), above the click rings.
            if (pointer != null) last = PointerStage(g, args, files, ref input, last, pointer, p, sessionDir, graphPath, fps, srcEnd);

            // Camera: canvas window moved by zooms → output size. perspective samples sub-pixel (no zoompan shimmer);
            // without zooms the window is static and a plain crop is much cheaper.
            if (p.Zooms.Count == 0)
            {
                var (sx, sy, sw, sh) = ZoomMath.ViewRect(p.Zooms, 0, m.Width, m.Height, p.Canvas);
                g.Append(F($"[{last}]crop={Math.Round(sw)}:{Math.Round(sh)}:{Math.Round(sx)}:{Math.Round(sy)}:exact=1,scale={ow}:{oh}:flags=bicubic,setsar=1[vz];\n"));
            }
            else if (vulkanDevice >= 0)
            {
                // Same window on the GPU (libplacebo over Vulkan): equal speed on a discrete GPU, ~6× less CPU left to the encoder.
                args.InsertRange(0, ["-init_hw_device", F($"vulkan=vk:{vulkanDevice}"), "-filter_hw_device", "vk"]);
                var (vx, vy, vw, vh) = ZoomMath.FfmpegViewExpr(p.Zooms, p.Canvas, m.Width, m.Height, fps, "n");
                g.Append($"[{last}]format=yuv420p,hwupload,libplacebo=w={ow}:h={oh}:crop_x='{vx}':crop_y='{vy}':crop_w='{vw}':crop_h='{vh}':format=yuv420p,hwdownload,format=yuv420p,setsar=1[vz];\n");
            }
            else
            {
                var (vx, vy, vw, vh) = ZoomMath.FfmpegViewExpr(p.Zooms, p.Canvas, m.Width, m.Height, fps);
                g.Append($"[{last}]perspective=x0='{vx}':y0='{vy}':x1='{vx}+{vw}':y1='{vy}':x2='{vx}':y2='{vy}+{vh}':x3='{vx}+{vw}':y3='{vy}+{vh}':eval=frame:sense=source,");
                g.Append(F($"scale={ow}:{oh}:flags=bicubic,setsar=1[vz];\n"));
            }
            last = "vz";

            // Background: the screen as a rounded card on a gradient. Mask and gradient are computed once and repeated
            // for exactly the stream's frame count (an endless loop would never let ffmpeg finish).
            if (p.Background.Enabled)
            {
                var (bx, by, bw, bh, br) = Layout.ScreenRect(ow, oh, p.Background);
                var frames = (int)Math.Ceiling(srcEnd * fps) + 1;
                var (r1, g1, b1) = ParseColor(p.Background.Color1);
                var (r2, g2, b2) = ParseColor(p.Background.Color2);
                var mask = br > 0 ? F($"if(lte(hypot(max(abs(X-W/2)-(W/2-{br}),0),max(abs(Y-H/2)-(H/2-{br}),0)),{br}),255,0)") : "255";
                g.Append(F($"[vz]scale={bw}:{bh}:flags=bicubic,format=yuva420p[bgs];"));
                g.Append(F($"color=black:s={bw}x{bh}:r={fps}:d={1.0 / fps:F4},format=gray,geq=lum='{mask}',loop=loop={frames}:size=1[bgm];[bgs][bgm]alphamerge[bgf];"));
                g.Append(F($"gradients=s={ow}x{oh}:c0=0x{r1:X2}{g1:X2}{b1:X2}:c1=0x{r2:X2}{g2:X2}{b2:X2}:x0=0:y0=0:x1={ow}:y1={oh}:nb_colors=2:speed=0:r={fps},trim=end_frame=1,loop=loop={frames}:size=1[bgg];"));
                g.Append(F($"[bgg][bgf]overlay={bx}:{by}:shortest=1,format=yuv420p,setsar=1[vbg];\n"));
                last = "vbg";
            }

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
                g.Append(F($":alpha='clip(min(t-{start:F3},{end:F3}-t)/{Math.Max(1e-3, Layout.FadeLength(start, end)):F3},0,1)'"));
                g.Append(F($":x=({cx}*w-text_w/2):y=({cy}*h-text_h/2):enable='between(t,{start:F3},{end:F3})'[x{n}];\n"));
                last = $"x{n++}";
            }
            foreach (var t in p.Texts) Text(t.Start, t.End, t.Text, t.X, t.Y, t.Size, t.Color, t.Background, t.Bold);
            if (p.SubtitleStyle.Burn)
                foreach (var c in p.Subtitles) Text(c.Start, c.End, c.Text, 0.5, p.SubtitleStyle.Y, p.SubtitleStyle.Size, p.SubtitleStyle.Color, p.SubtitleStyle.Background, false);

            // Keystrokes on top: one pre-rendered image per distinct shortcut (the preview's own drawing), split per showing.
            if (p.Keys.Visible && keys is { Count: > 0 } && keyImage != null)
            {
                var kn = 0;
                foreach (var group in KeyLog.Windows(keys, p.Keys.Duration).Where(w => w.End > 0 && w.Start < srcEnd).GroupBy(w => w.Combo))
                {
                    if (keyImage(group.Key) is not { } img) continue;
                    var shows = group.ToList();
                    // Looped so each showing can fade; -t bounds the loop to the source length.
                    args.AddRange(["-loop", "1", "-framerate", F($"{fps}"), "-t", F($"{srcEnd:F3}"), "-i", img.Png]);
                    g.Append(F($"[{input}:v]format=rgba,split={shows.Count}"));
                    for (var i = 0; i < shows.Count; i++) g.Append($"[kq{kn + i}]");
                    g.Append(";\n");
                    int kx = (int)Math.Round(p.Keys.X * ow - img.W / 2.0), ky = (int)Math.Round(p.Keys.Y * oh - img.H / 2.0);
                    foreach (var (s, e, _) in shows)
                    {
                        var fd = Math.Max(1e-3, Layout.FadeLength(s, e));
                        g.Append(F($"[kq{kn}]trim=start={s:F3}:end={e:F3},fade=t=in:st={s:F3}:d={fd:F3}:alpha=1,fade=t=out:st={e - fd:F3}:d={fd:F3}:alpha=1[kf{kn}];\n"));
                        g.Append(F($"[{last}][kf{kn}]overlay={kx}:{ky}:eof_action=pass[ko{kn}];\n"));
                        last = $"ko{kn++}";
                    }
                    input++;
                }
            }
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

        // Soft subtitle track (players can toggle it), in output time; added alongside any burned-in copy.
        var srt = fmt is ExportFormat.Png or ExportFormat.Wav ? "" : p.ExportSrt();
        if (srt.Length > 0)
        {
            var srtPath = Path.ChangeExtension(graphPath, ".subs.srt");
            files[srtPath] = srt;
            args.AddRange(["-i", srtPath]);
        }
        args.AddRange(["-/filter_complex", graphPath]);
        if (video) args.AddRange(["-map", "[vout]"]);
        if (audioOut) args.AddRange(["-map", "[aout]"]);
        if (srt.Length > 0) args.AddRange(["-map", $"{input}:s", "-c:s", fmt == ExportFormat.MkvH264 ? "srt" : "mov_text"]);
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
        if (p.Export.Format == ExportFormat.Mp4Av1) bpp *= 0.5;
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
    /// <summary>
    /// One overlay per cursor shape used. A shape's frames are stacked in one PNG; a per-frame command file (sendcmd)
    /// sets each overlay's position (off-frame when that shape isn't showing), the crop picking the animation frame, and
    /// the opacity — all read from <see cref="CursorTrack.At"/>, the function the preview draws with.
    /// </summary>
    private static string PointerStage(StringBuilder g, List<string> args, Dictionary<string, string> files, ref int input, string last,
        CursorTrack pointer, StudioProject p, string sessionDir, string graphPath, int fps, double srcEnd)
    {
        var frames = (int)Math.Ceiling(srcEnd * fps);
        var states = Enumerable.Range(0, frames + 1).Select(k => pointer.At(k / (double)fps, fps)).ToArray();
        var used = states.Where(st => st.ShapeId != null).Select(st => st.ShapeId!).Distinct().ToList();
        if (used.Count == 0) return last;

        var dir = Path.GetDirectoryName(Path.GetFullPath(graphPath))!;
        var stem = Path.GetFileNameWithoutExtension(graphPath);
        var scale = states.First(st => st.ShapeId != null).Scale; // constant for a project
        var slot = new Dictionary<string, (int Index, CursorShape Shape)>();
        foreach (var id in used)
        {
            var shape = pointer.Shape(id)!;
            string png;
            if (id == CursorTrack.CustomShapeId) png = Path.Combine(sessionDir, p.Pointer.CustomImage!);
            else
            {
                png = Path.Combine(dir, $"{stem}.cursor-{id}.png");
                var px = File.ReadAllBytes(CursorShapeStore.PixelsPath(sessionDir, id));
                Png.WriteBgra(png, shape.W, shape.H * shape.Frames, px);
            }
            var i = slot.Count;
            slot[id] = (i, shape);
            int sw = Math.Max(1, (int)Math.Round(shape.W * scale)), sh = Math.Max(1, (int)Math.Round(shape.H * scale));
            args.AddRange(["-loop", "1", "-framerate", F($"{fps}"), "-t", F($"{srcEnd:F3}"), "-i", png]);
            g.Append(F($"[{input}:v]format=rgba,crop@pc{i}=w={shape.W}:h={shape.H}:x=0:y=0,scale={sw}:{sh}:flags=lanczos,colorchannelmixer@pa{i}=aa=1[pq{i}];\n"));
            input++;
        }

        // Commands only when something changes; times sit just before each frame so the value applies to it.
        var cmd = new StringBuilder();
        var prev = new (int X, int Y, int Crop, double A)[used.Count];
        Array.Fill(prev, (int.MinValue, int.MinValue, -1, -1));
        for (var k = 0; k <= frames; k++)
        {
            var st = states[k];
            var parts = new List<string>();
            foreach (var (id, (i, shape)) in slot)
            {
                var on = st.ShapeId == id;
                var now = on ? ((int)Math.Round(st.X), (int)Math.Round(st.Y), st.Frame * shape.H, Math.Round(st.Alpha, 3)) : (-100000, -100000, prev[i].Crop < 0 ? 0 : prev[i].Crop, prev[i].A < 0 ? 1 : prev[i].A);
                if (now.Item1 != prev[i].X) parts.Add(F($"overlay@po{i} x {now.Item1}"));
                if (now.Item2 != prev[i].Y) parts.Add(F($"overlay@po{i} y {now.Item2}"));
                if (now.Item3 != prev[i].Crop) parts.Add(F($"crop@pc{i} y {now.Item3}"));
                if (now.Item4 != prev[i].A) parts.Add(F($"colorchannelmixer@pa{i} aa {now.Item4}"));
                prev[i] = now;
            }
            if (parts.Count > 0) cmd.Append(F($"{Math.Max(0, k / (double)fps - 0.0005):F4} ")).Append(string.Join(", ", parts)).Append(";\n");
        }
        var cmdFile = Path.Combine(dir, stem + ".cursor.cmd");
        files[cmdFile] = cmd.ToString();

        g.Append($"[{last}]sendcmd=f='{FilterPath(cmdFile)}'[ps];\n");
        last = "ps";
        foreach (var (_, (i, _)) in slot)
        {
            g.Append(F($"[{last}][pq{i}]overlay@po{i}=x=-100000:y=-100000:eval=frame:eof_action=pass[pv{i}];\n"));
            last = $"pv{i}";
        }
        return last;
    }

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
