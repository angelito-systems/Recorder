using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Angelito.ScreenStudio.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Angelito.ScreenStudio.Desktop.Capture;

/// <summary>Capture rectangle in pixels relative to the monitor's top-left; null = whole monitor.</summary>
public sealed record Region(int X, int Y, int Width, int Height);

public sealed record RecordOptions(MonitorInfo Monitor, Region? Region, int Fps, string? Webcam, bool Mic, bool SystemAudio,
    EncoderChoice ScreenEncoder, EncoderChoice CpuEncoder, string Dir, KeyFilter? Keys = null);

/// <summary>
/// One recording = one folder of independent streams (see docs/FORMATS.md). Every stream stamps its start against
/// the same session clock. Pause stops the video processes (nothing is captured while paused); each resume starts a new
/// part. On stop, parts are joined and audio and cursor are re-timed to the joined video, so the editor sees one timeline.
/// </summary>
public sealed class Recorder
{
    private sealed class Part(string file)
    {
        public string File { get; } = file;
        public double Start = double.NaN; // session seconds when ffmpeg reported running
        public double Stop;
    }

    private readonly RecordOptions _o;
    private readonly SessionManifest _m = new();
    private readonly Stopwatch _clock = new();
    private readonly List<Part> _screenParts = [], _camParts = [];
    private FfmpegProcess? _screen, _cam;
    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _sys;
    private WasapiOut? _silence;
    private WaveFileWriter? _micW, _sysW;
    private double _micOffset, _sysOffset;
    private MouseHook? _hook;
    private StreamWriter? _cursor, _keys;
    private KeyHook? _keyHook;
    private double _lastMove = -1, _pausedTotal, _pausedAt;
    private volatile bool _paused, _videoStopping, _stopping;
    private readonly List<Task> _audioStopped = [];
    private readonly int _ox, _oy, _w, _h;

    public double Elapsed => _clock.Elapsed.TotalSeconds;
    /// <summary>Recorded time, excluding pauses (what the timer shows).</summary>
    public double Recorded => (_paused ? _pausedAt : Elapsed) - _pausedTotal;
    public bool IsPaused => _paused;
    public bool KeysSuspended => _keyHook?.Suspended == true;
    public float MicPeak { get; private set; }
    public float SystemPeak { get; private set; }
    /// <summary>Raised (any thread) when a stream fails mid-recording, e.g. a device was unplugged.</summary>
    public event Action<string>? Warning;

    public Recorder(RecordOptions o)
    {
        _o = o;
        var r = o.Region ?? new Region(0, 0, o.Monitor.Width, o.Monitor.Height);
        // Encoders need even sizes; trim a pixel rather than scale.
        (_ox, _oy, _w, _h) = (r.X, r.Y, r.Width & ~1, r.Height & ~1);
        if (_w < 64 || _h < 64) throw new ArgumentException("La región debe medir al menos 64×64 píxeles.");
    }

    public void Start()
    {
        Directory.CreateDirectory(_o.Dir);
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_o.Dir))!).AvailableFreeSpace < 2L << 30)
            throw new IOException("Hay menos de 2 GB libres en el disco de destino.");

        _m.Width = _w; _m.Height = _h; _m.Fps = _o.Fps;
        _m.Save(_o.Dir); // written first: an incomplete session is still recognizable after a crash
        _clock.Start();

        _cursor = new StreamWriter(Path.Combine(_o.Dir, "cursor.raw.txt"));
        _hook = new MouseHook();
        _hook.Event += OnMouse;
        if (_o.Keys != null)
        {
            _keys = new StreamWriter(Path.Combine(_o.Dir, "keys.raw.txt"));
            _keyHook = new KeyHook(_o.Keys);
            _keyHook.Shortcut += combo => { if (!_paused) _keys.WriteLine(KeyLog.Format(new KeyEvent(Elapsed, combo))); };
        }

        StartVideo();

        if (_o.Mic)
        {
            _mic = new WasapiCapture();
            _micW = new WaveFileWriter(Path.Combine(_o.Dir, "mic.raw.wav"), _mic.WaveFormat);
            Hook(_mic, _micW, t => _micOffset = t, p => MicPeak = p, "micrófono");
        }
        if (_o.SystemAudio)
        {
            // Loopback delivers nothing while the device is silent; playing silence keeps the stream continuous and in sync.
            _silence = new WasapiOut();
            _silence.Init(new SilenceProvider(new WaveFormat(48000, 16, 2)));
            _silence.Play();
            _sys = new WasapiLoopbackCapture();
            _sysW = new WaveFileWriter(Path.Combine(_o.Dir, "system.raw.wav"), _sys.WaveFormat);
            Hook(_sys, _sysW, t => _sysOffset = t, p => SystemPeak = p, "audio del sistema");
        }
        _mic?.StartRecording();
        _sys?.StartRecording();
    }

    public async Task PauseAsync()
    {
        if (_paused) return;
        _pausedAt = Elapsed;
        _paused = true; // audio now writes silence and the cursor is ignored
        await StopVideoAsync();
    }

    public void Resume()
    {
        if (!_paused) return;
        _pausedTotal += Elapsed - _pausedAt;
        StartVideo();
        _paused = false;
    }

    private void StartVideo()
    {
        _videoStopping = false;
        var n = _screenParts.Count;
        // MKV while recording: survives a crash. Joined/remuxed to MP4 on stop.
        var sp = new Part($"screen_{n}.mkv");
        _screenParts.Add(sp);
        var src = $"ddagrab=output_idx={_o.Monitor.OutputIdx}:framerate={_o.Fps}:draw_mouse=1";
        if (_o.Region != null) src += $":offset_x={_ox}:offset_y={_oy}:video_size={_w}x{_h}";
        var a = new List<string> { "-f", "lavfi", "-i", src };
        if (_o.ScreenEncoder.CaptureFilter != "") a.AddRange(["-vf", _o.ScreenEncoder.CaptureFilter]);
        a.AddRange(_o.ScreenEncoder.QualityArgs(ExportQuality.High));
        a.AddRange(["-g", (_o.Fps * 2).ToString(), "-y", sp.File]);
        _screen = StartFfmpeg(a, "pantalla", sp);

        if (_o.Webcam != null)
        {
            var cp = new Part($"webcam_{n}.mkv");
            _camParts.Add(cp);
            var c = new List<string> { "-f", "dshow", "-rtbufsize", "256M", "-i", $"video={_o.Webcam}", "-pix_fmt", _o.CpuEncoder.CpuPixFmt };
            c.AddRange(_o.CpuEncoder.QualityArgs(ExportQuality.Balanced));
            c.AddRange(["-y", cp.File]);
            _cam = StartFfmpeg(c, "cámara", cp);
        }
    }

    private async Task StopVideoAsync()
    {
        _videoStopping = true;
        var stop = Elapsed;
        await Task.WhenAll(
            _screen?.StopAsync(TimeSpan.FromSeconds(15)) ?? Task.CompletedTask,
            _cam?.StopAsync(TimeSpan.FromSeconds(15)) ?? Task.CompletedTask);
        _screen?.Dispose(); _cam?.Dispose();
        _screen = _cam = null;
        _screenParts[^1].Stop = stop;
        if (_camParts.Count > 0) _camParts[^1].Stop = stop;
    }

    private FfmpegProcess StartFfmpeg(List<string> args, string name, Part part)
    {
        var f = new FfmpegProcess(args, _o.Dir);
        var tail = new Queue<string>();
        f.StdErr += l =>
        {
            // shortcut: stream start is taken when ffmpeg reports it is running (±1 frame-ish); the editor exposes manual sync for the rest.
            if (double.IsNaN(part.Start) && l.Contains("Press [q]")) part.Start = Elapsed;
            lock (tail) { tail.Enqueue(l); if (tail.Count > 8) tail.Dequeue(); }
        };
        f.Start();
        _ = f.WaitAsync().ContinueWith(_ =>
        {
            if (!_videoStopping) lock (tail) Warning?.Invoke($"La grabación de {name} se detuvo inesperadamente:\n{string.Join("\n", tail)}");
        });
        return f;
    }

    private void Hook(IWaveIn src, WaveFileWriter w, Action<double> setOffset, Action<float> peak, string name)
    {
        var first = true;
        src.DataAvailable += (_, e) =>
        {
            if (first) { first = false; setOffset(Math.Max(0, Elapsed - (double)e.BytesRecorded / src.WaveFormat.AverageBytesPerSecond)); }
            if (_paused)
            {
                // Keep the file continuous (sync) without storing anything said while paused.
                Array.Clear(e.Buffer, 0, e.BytesRecorded);
                peak(0);
            }
            else peak(Peak(e.Buffer, e.BytesRecorded, src.WaveFormat));
            w.Write(e.Buffer, 0, e.BytesRecorded);
        };
        var stopped = new TaskCompletionSource();
        _audioStopped.Add(stopped.Task);
        src.RecordingStopped += (_, e) =>
        {
            stopped.TrySetResult();
            if (e.Exception != null && !_stopping) Warning?.Invoke($"Se perdió el {name}: {e.Exception.Message}. Lo grabado hasta ahora se conserva.");
        };
    }

    private static float Peak(byte[] b, int n, WaveFormat f)
    {
        float max = 0;
        if (f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32))
            for (var i = 0; i + 4 <= n; i += 4) max = Math.Max(max, Math.Abs(BitConverter.ToSingle(b, i)));
        else if (f.BitsPerSample == 16)
            for (var i = 0; i + 2 <= n; i += 2) max = Math.Max(max, Math.Abs(BitConverter.ToInt16(b, i) / 32768f));
        return max;
    }

    private void OnMouse(int x, int y, int kind)
    {
        if (_paused) return;
        var t = Elapsed;
        if (kind == 0 && t - _lastMove < 1 / 120.0) return; // moves throttled to 120 Hz; clicks always kept
        if (kind == 0) _lastMove = t;
        _cursor?.WriteLine(CursorLog.Format(new CursorEvent(t, x - _o.Monitor.X - _ox, y - _o.Monitor.Y - _oy, (CursorKind)kind)));
    }

    /// <summary>Stops every stream, finalizes files and returns the session folder.</summary>
    public async Task<string> StopAsync()
    {
        _hook?.Dispose();
        _keyHook?.Dispose();
        if (!_paused) await StopVideoAsync();
        _stopping = true;
        _mic?.StopRecording();
        _sys?.StopRecording();
        _silence?.Stop();
        await Task.WhenAny(Task.WhenAll(_audioStopped), Task.Delay(3000)); // writers must outlive the last DataAvailable
        _micW?.Dispose(); _sysW?.Dispose();
        _mic?.Dispose(); _sys?.Dispose(); _silence?.Dispose();
        _cursor?.Dispose();
        _keys?.Dispose();

        await FinalizeAsync();
        return _o.Dir;
    }

    /// <summary>Joins parts and re-times audio/cursor onto the joined video. Raw files are deleted only after success.</summary>
    private async Task FinalizeAsync()
    {
        var parts = new List<RecordedPart>();
        var used = new List<Part>();
        foreach (var p in _screenParts)
        {
            var path = Path.Combine(_o.Dir, p.File);
            if (double.IsNaN(p.Start) || !File.Exists(path)) continue;
            double d;
            try { d = await FfmpegProcess.ProbeDurationAsync(path); }
            catch (InvalidDataException) { d = p.Stop - p.Start; } // killed ffmpeg: container has no duration
            if (d <= 0) continue;
            parts.Add(new RecordedPart(p.Start, d));
            used.Add(p);
        }
        if (parts.Count == 0) throw new IOException("No se grabó video de pantalla; revise el registro de incidentes.");

        _m.Screen = await JoinCopy(used.Select(p => p.File).ToList(), "screen.mp4");
        _m.ScreenOffset = 0;

        if (_o.Webcam != null) await FinalizeWebcam(parts, used);

        if (_micW != null) { await Retime("mic", _micOffset, parts); _m.Mic = "mic.wav"; }
        if (_sysW != null) { await Retime("system", _sysOffset, parts); _m.SystemAudio = "system.wav"; }
        _m.MicOffset = _m.SystemAudioOffset = 0;

        var raw = Path.Combine(_o.Dir, "cursor.raw.txt");
        var lines = File.ReadLines(raw).Select(l => CursorLog.TryParse(l, out var e) ? e : (CursorEvent?)null)
            .Select(e => e is { } ev && Parts.ToVideoTime(parts, ev.T) is { } vt ? CursorLog.Format(ev with { T = vt }) : null)
            .Where(l => l != null).ToList();
        File.WriteAllLines(Path.Combine(_o.Dir, _m.Cursor), lines!);
        File.Delete(raw);

        if (_keys != null)
        {
            var rawKeys = Path.Combine(_o.Dir, "keys.raw.txt");
            File.WriteAllLines(Path.Combine(_o.Dir, "keys.txt"), File.ReadLines(rawKeys)
                .Select(l => KeyLog.TryParse(l, out var k) && Parts.ToVideoTime(parts, k.T) is { } vt ? KeyLog.Format(k with { T = vt }) : null)
                .OfType<string>());
            File.Delete(rawKeys);
            _m.Keys = "keys.txt";
        }

        _m.Complete = true;
        _m.Save(_o.Dir);
        foreach (var p in _screenParts.Concat(_camParts)) TryDelete(p.File);
    }

    private async Task<string> JoinCopy(List<string> files, string output)
    {
        string[] input;
        if (files.Count == 1) input = ["-i", files[0]];
        else
        {
            File.WriteAllLines(Path.Combine(_o.Dir, "parts.txt"), files.Select(f => $"file '{f}'"));
            input = ["-f", "concat", "-safe", "0", "-i", "parts.txt"];
        }
        var (code, log) = await FfmpegProcess.RunAsync(["-y", "-loglevel", "error", .. input, "-c", "copy", "-movflags", "+faststart", output], workingDir: _o.Dir);
        TryDelete("parts.txt");
        if (code != 0) throw new IOException($"No se pudo unir {output}; los tramos originales se conservaron.\n{log}");
        return output;
    }

    /// <summary>One part: plain remux + offset. Several: each camera part is cut to its screen part's time window and re-encoded once.</summary>
    private async Task FinalizeWebcam(List<RecordedPart> parts, List<Part> used)
    {
        var cams = used.Select(s => _camParts[_screenParts.IndexOf(s)]).ToList();
        if (cams.Any(c => double.IsNaN(c.Start) || !File.Exists(Path.Combine(_o.Dir, c.File))))
        {
            Warning?.Invoke("Falta un tramo de la cámara; la grabación se guardó sin cámara (los archivos parciales se conservaron).");
            _m.Webcam = null;
            return;
        }
        if (cams.Count == 1)
        {
            _m.Webcam = await JoinCopy([cams[0].File], "webcam.mp4");
            _m.WebcamOffset = cams[0].Start - parts[0].Start;
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
            g.Append(string.Create(inv, $"[{i}:v]fps={_o.Fps},setpts=PTS-STARTPTS,{align}setpts=PTS-STARTPTS,tpad=stop=-1:stop_mode=clone,trim=duration={parts[i].Duration:F4},format=yuv420p[c{i}];"));
        }
        for (var i = 0; i < cams.Count; i++) g.Append($"[c{i}]");
        g.Append($"concat=n={cams.Count}:v=1:a=0[out]");
        args.AddRange(["-filter_complex", g.ToString(), "-map", "[out]", "-pix_fmt", _o.CpuEncoder.CpuPixFmt, .. _o.CpuEncoder.QualityArgs(ExportQuality.High), "webcam.mp4"]);
        var (code, log) = await FfmpegProcess.RunAsync(args, workingDir: _o.Dir);
        if (code != 0) throw new IOException("No se pudo unir la cámara; los tramos originales se conservaron.\n" + log);
        _m.Webcam = "webcam.mp4";
        _m.WebcamOffset = 0;
    }

    private async Task Retime(string name, double offset, List<RecordedPart> parts)
    {
        var (code, log) = await FfmpegProcess.RunAsync(["-y", "-loglevel", "error", "-i", name + ".raw.wav",
            "-filter_complex", Parts.AudioFilter(parts, offset), "-map", "[out]", "-c:a", "pcm_f32le", name + ".wav"], workingDir: _o.Dir);
        if (code != 0) throw new IOException($"No se pudo procesar {name}.raw.wav (se conservó).\n{log}");
        TryDelete(name + ".raw.wav");
    }

    private void TryDelete(string file) { try { File.Delete(Path.Combine(_o.Dir, file)); } catch (IOException) { } }
}
