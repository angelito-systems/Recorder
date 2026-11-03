using System.Globalization;
using System.Text;

namespace Angelito.ScreenStudio.Core;

/// <summary>
/// Turns a session folder's raw parts into the final streams, using only what is on disk (the journal in session.json),
/// so the same code finishes a normal stop and recovers a recording interrupted by a crash or power loss.
/// Raw files are deleted only after every output was written.
/// </summary>
public static class SessionFinalizer
{
    /// <summary>Interrupted sessions in <paramref name="root"/> that can be finalized (journal present, nobody recording into them).</summary>
    public static List<string> FindInterrupted(string root)
    {
        var list = new List<string>();
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                if (!File.Exists(Path.Combine(dir, SessionManifest.FileName))) continue;
                var m = SessionManifest.Load(dir);
                if (!m.Complete && m.Recording is { Parts.Count: > 0 } && !IsLocked(dir)) list.Add(dir);
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) { }
        }
        return list;
    }

    public const string LockFile = "recording.lock";

    /// <summary>
    /// Output options for live MKV parts: 1-second clusters flushed per packet. By default the muxer buffers up to ~5 s
    /// in memory, so a crash could leave a 0-byte file; with these, at most about a second is lost.
    /// </summary>
    public static readonly string[] CrashSafeMkvArgs = ["-flush_packets", "1", "-cluster_time_limit", "1000"];

    /// <summary>A live recorder holds this file open exclusively; the OS releases it if the process dies.</summary>
    public static FileStream AcquireLock(string dir) =>
        new(Path.Combine(dir, LockFile), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    private static bool IsLocked(string dir)
    {
        var path = Path.Combine(dir, LockFile);
        if (!File.Exists(path)) return false;
        try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } File.Delete(path); return false; }
        catch (IOException) { return true; }
    }

    public static async Task FinalizeAsync(string dir, SessionManifest m, EncoderChoice cpuEncoder, Action<string>? warn = null)
    {
        var j = m.Recording ?? throw new InvalidDataException("La sesión no tiene registro de grabación.");
        var parts = new List<RecordedPart>();
        var used = new List<(JournalPart Part, string Screen)>();
        foreach (var p in j.Parts)
        {
            if (p.ScreenStart is not { } start || !File.Exists(Path.Combine(dir, p.Screen))) continue;
            var (file, d) = await UsablePart(dir, p.Screen, p.Stop - start);
            if (file == null || d <= 0.05) continue;
            parts.Add(new RecordedPart(start, d));
            used.Add((p, file));
        }
        if (parts.Count == 0) throw new IOException("No hay video de pantalla recuperable en esta sesión.");

        m.Screen = await JoinCopy(dir, used.Select(u => u.Screen).ToList(), "screen.mp4");
        m.ScreenOffset = 0;

        if (j.Parts.Any(p => p.Webcam != null)) await FinalizeWebcam(dir, m, parts, used, cpuEncoder, warn);

        m.Mic = await Retime(dir, "mic", j.Mic, j.MicOffset, parts, warn);
        m.SystemAudio = await Retime(dir, "system", j.SystemAudio, j.SystemOffset, parts, warn);
        m.MicOffset = m.SystemAudioOffset = 0;

        RetimeLines(dir, "cursor.raw.txt", m.Cursor, l => CursorLog.TryParse(l, out var e) && Parts.ToVideoTime(parts, e.T) is { } t ? CursorLog.Format(e with { T = t }) : null);
        if (j.Keys)
        {
            RetimeLines(dir, "keys.raw.txt", "keys.txt", l => KeyLog.TryParse(l, out var k) && Parts.ToVideoTime(parts, k.T) is { } t ? KeyLog.Format(k with { T = t }) : null);
            m.Keys = "keys.txt";
        }

        var raw = j.Parts.SelectMany(p => new[] { p.Screen, p.Webcam, Path.ChangeExtension(p.Screen, ".fixed.mkv"), p.Webcam == null ? null : Path.ChangeExtension(p.Webcam, ".fixed.mkv") }).OfType<string>().ToList();
        m.Recording = null;
        m.Complete = true;
        m.Save(dir);
        foreach (var f in raw) TryDelete(dir, f);
    }

    /// <summary>
    /// A part's real duration. A process killed mid-write leaves an MKV without duration/cues; a stream-copy remux rebuilds
    /// the index from the frames that made it to disk.
    /// </summary>
    private static async Task<(string? File, double Duration)> UsablePart(string dir, string file, double? journalLength)
    {
        try { return (file, await FfmpegProcess.ProbeDurationAsync(Path.Combine(dir, file))); }
        catch (InvalidDataException) { }
        var fixedName = Path.ChangeExtension(file, ".fixed.mkv");
        var (code, _) = await FfmpegProcess.RunAsync(["-y", "-loglevel", "error", "-err_detect", "ignore_err", "-i", file, "-c", "copy", fixedName], workingDir: dir);
        if (code == 0)
            try { return (fixedName, await FfmpegProcess.ProbeDurationAsync(Path.Combine(dir, fixedName))); }
            catch (InvalidDataException) { }
        return journalLength is > 0 ? (file, journalLength.Value) : (null, 0);
    }

    private static async Task<string> JoinCopy(string dir, List<string> files, string output)
    {
        string[] input;
        if (files.Count == 1) input = ["-i", files[0]];
        else
        {
            File.WriteAllLines(Path.Combine(dir, "parts.txt"), files.Select(f => $"file '{f}'"));
            input = ["-f", "concat", "-safe", "0", "-i", "parts.txt"];
        }
        var (code, log) = await FfmpegProcess.RunAsync(["-y", "-loglevel", "error", .. input, "-c", "copy", "-movflags", "+faststart", output], workingDir: dir);
        TryDelete(dir, "parts.txt");
        if (code != 0) throw new IOException($"No se pudo unir {output}; los tramos originales se conservaron.\n{log}");
        return output;
    }

    /// <summary>One part: remux + offset. Several: each camera part is cut to its screen part's window and re-encoded once.</summary>
    private static async Task FinalizeWebcam(string dir, SessionManifest m, List<RecordedPart> parts, List<(JournalPart Part, string Screen)> used,
        EncoderChoice enc, Action<string>? warn)
    {
        var cams = new List<(string File, double Start)>();
        foreach (var (p, _) in used)
        {
            if (p.Webcam == null || p.WebcamStart == null || !File.Exists(Path.Combine(dir, p.Webcam)))
            {
                warn?.Invoke("Falta un tramo de la cámara; la grabación se guardó sin cámara (los archivos de cámara se conservaron).");
                m.Webcam = null;
                return;
            }
            var (file, _) = await UsablePart(dir, p.Webcam, null);
            cams.Add((file ?? p.Webcam, p.WebcamStart.Value));
        }
        if (cams.Count == 1)
        {
            m.Webcam = await JoinCopy(dir, [cams[0].File], "webcam.mp4");
            m.WebcamOffset = cams[0].Start - parts[0].Start;
            return;
        }
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string> { "-y", "-loglevel", "error" };
        var g = new StringBuilder();
        for (var i = 0; i < cams.Count; i++)
        {
            args.AddRange(["-i", cams[i].File]);
            var shift = parts[i].Start - cams[i].Start;
            var align = shift >= 0 ? string.Create(inv, $"trim=start={shift:F4},") : string.Create(inv, $"tpad=start_duration={-shift:F4}:start_mode=clone,");
            g.Append(string.Create(inv, $"[{i}:v]fps={Math.Max(1, m.Fps)},setpts=PTS-STARTPTS,{align}setpts=PTS-STARTPTS,tpad=stop=-1:stop_mode=clone,trim=duration={parts[i].Duration:F4},format=yuv420p[c{i}];"));
        }
        for (var i = 0; i < cams.Count; i++) g.Append($"[c{i}]");
        g.Append($"concat=n={cams.Count}:v=1:a=0[out]");
        args.AddRange(["-filter_complex", g.ToString(), "-map", "[out]", "-pix_fmt", enc.CpuPixFmt, .. enc.QualityArgs(ExportQuality.High), "webcam.mp4"]);
        var (code, log) = await FfmpegProcess.RunAsync(args, workingDir: dir);
        if (code != 0) throw new IOException("No se pudo unir la cámara; los tramos originales se conservaron.\n" + log);
        m.Webcam = "webcam.mp4";
        m.WebcamOffset = 0;
    }

    /// <summary>Cuts a continuous raw WAV down to the recorded parts. Returns the final file name, or null if there is none.</summary>
    private static async Task<string?> Retime(string dir, string name, bool recorded, double? offset, List<RecordedPart> parts, Action<string>? warn)
    {
        if (!recorded || !File.Exists(Path.Combine(dir, name + ".raw.wav"))) return null;
        if (offset == null) { warn?.Invoke($"El {name} no llegó a grabar datos."); return null; }
        var (code, log) = await FfmpegProcess.RunAsync(["-y", "-loglevel", "error", "-i", name + ".raw.wav",
            "-filter_complex", Parts.AudioFilter(parts, offset.Value), "-map", "[out]", "-c:a", "pcm_f32le", name + ".wav"], workingDir: dir);
        if (code != 0) throw new IOException($"No se pudo procesar {name}.raw.wav (se conservó).\n{log}");
        TryDelete(dir, name + ".raw.wav");
        return name + ".wav";
    }

    private static void RetimeLines(string dir, string raw, string output, Func<string, string?> map)
    {
        var path = Path.Combine(dir, raw);
        File.WriteAllLines(Path.Combine(dir, output), File.Exists(path) ? File.ReadLines(path).Select(map).OfType<string>().ToList() : []);
        TryDelete(dir, raw);
    }

    private static void TryDelete(string dir, string file) { try { File.Delete(Path.Combine(dir, file)); } catch (IOException) { } }
}
