using System.Diagnostics;
using System.IO;
using Angelito.ScreenStudio.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Angelito.ScreenStudio.Desktop.Capture;

/// <summary>Capture rectangle in pixels relative to the monitor's top-left; null = whole monitor.</summary>
public sealed record Region(int X, int Y, int Width, int Height);

public sealed record RecordOptions(MonitorInfo Monitor, Region? Region, int Fps, string? Webcam, bool Mic, bool SystemAudio,
    EncoderChoice ScreenEncoder, EncoderChoice CpuEncoder, string Dir, KeyFilter? Keys = null, WindowInfo? Window = null);

/// <summary>
/// One recording = one folder of independent streams (see docs/FORMATS.md). Every stream stamps its start against
/// the same session clock. Pause stops the video processes (nothing is captured while paused); each resume starts a new
/// part. Crash safety: the journal in session.json is updated as streams start/stop, audio headers and event logs are
/// flushed every second, and the folder is locked while live — so <see cref="SessionFinalizer"/> can finish the session
/// later from disk alone if this process dies.
/// </summary>
public sealed class Recorder
{
    private readonly RecordOptions _o;
    private readonly SessionManifest _m = new();
    private readonly RecordingJournal _j = new();
    private readonly Stopwatch _clock = new();
    private FfmpegProcess? _screen, _cam;
    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _sys;
    private WasapiOut? _silence;
    private WaveFileWriter? _micW, _sysW;
    private MouseHook? _hook;
    private StreamWriter? _cursor, _keys;
    private KeyHook? _keyHook;
    private FileStream? _lock;
    private WindowCapture? _win;
    private double _lastMove = -1, _lastCursorFlush, _pausedTotal, _pausedAt;
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
        if (o.Window != null)
        {
            using var probe = new WindowCapture(o.Window.Handle);
            (_ox, _oy, _w, _h) = (0, 0, probe.Width, probe.Height);
            return;
        }
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

        _lock = SessionFinalizer.AcquireLock(_o.Dir);
        _m.Width = _w; _m.Height = _h; _m.Fps = _o.Fps;
        _j.Mic = _o.Mic; _j.SystemAudio = _o.SystemAudio; _j.Keys = _o.Keys != null;
        _m.Recording = _j;
        Journal(); // written first: an incomplete session is recognizable and recoverable after a crash
        _clock.Start();

        _cursor = new StreamWriter(Path.Combine(_o.Dir, "cursor.raw.txt"));
        _hook = new MouseHook();
        _hook.Event += OnMouse;
        if (_o.Keys != null)
        {
            _keys = new StreamWriter(Path.Combine(_o.Dir, "keys.raw.txt")) { AutoFlush = true }; // rare events: flush each
            _keyHook = new KeyHook(_o.Keys);
            _keyHook.Shortcut += combo => { if (!_paused) _keys.WriteLine(KeyLog.Format(new KeyEvent(Elapsed, combo))); };
        }

        StartVideo();

        if (_o.Mic)
        {
            _mic = new WasapiCapture();
            _micW = new WaveFileWriter(Path.Combine(_o.Dir, "mic.raw.wav"), _mic.WaveFormat);
            Hook(_mic, _micW, t => { _j.MicOffset = t; Journal(); }, p => MicPeak = p, "micrófono");
        }
        if (_o.SystemAudio)
        {
            // Loopback delivers nothing while the device is silent; playing silence keeps the stream continuous and in sync.
            _silence = new WasapiOut();
            _silence.Init(new SilenceProvider(new WaveFormat(48000, 16, 2)));
            _silence.Play();
            _sys = new WasapiLoopbackCapture();
            _sysW = new WaveFileWriter(Path.Combine(_o.Dir, "system.raw.wav"), _sys.WaveFormat);
            Hook(_sys, _sysW, t => { _j.SystemOffset = t; Journal(); }, p => SystemPeak = p, "audio del sistema");
        }
        _mic?.StartRecording();
        _sys?.StartRecording();
    }

    /// <summary>Saves session.json; serialized with part additions because streams report from different threads.</summary>
    private void Journal() { lock (_j) _m.Save(_o.Dir); }

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
        var n = _j.Parts.Count;
        // MKV while recording: readable after a crash. Joined/remuxed to MP4 by the finalizer.
        var part = new JournalPart { Screen = $"screen_{n}.mkv", Webcam = _o.Webcam != null ? $"webcam_{n}.mkv" : null };
        lock (_j) _j.Parts.Add(part);
        Journal();
        if (_o.Window != null) StartWindowPart(part); else StartScreenPart(part);

        if (part.Webcam != null)
        {
            var c = new List<string> { "-f", "dshow", "-rtbufsize", "256M", "-i", $"video={_o.Webcam}", "-pix_fmt", _o.CpuEncoder.CpuPixFmt };
            c.AddRange(_o.CpuEncoder.QualityArgs(ExportQuality.Balanced));
            c.AddRange([.. SessionFinalizer.CrashSafeMkvArgs, "-y", part.Webcam]);
            _cam = StartFfmpeg(c, "cámara", t => { part.WebcamStart = t; Journal(); });
        }
    }

    /// <summary>Raw window frames on stdin; wall-clock stamps + constant output fps keep it in sync with the audio.</summary>
    private void StartWindowPart(JournalPart part)
    {
        _win = new WindowCapture(_o.Window!.Handle, (_w, _h));
        var a = new List<string> { "-f", "rawvideo", "-pix_fmt", "bgra", "-video_size", $"{_w}x{_h}", "-use_wallclock_as_timestamps", "1", "-i", "pipe:0",
            "-fps_mode", "cfr", "-r", _o.Fps.ToString(), "-pix_fmt", _o.CpuEncoder.BgraInputPixFmt };
        a.AddRange(_o.CpuEncoder.QualityArgs(ExportQuality.High));
        a.AddRange(["-g", (_o.Fps * 2).ToString(), .. SessionFinalizer.CrashSafeMkvArgs, "-y", part.Screen]);
        _screen = StartFfmpeg(a, "ventana", _ => { });
        _win.FirstFrame += () => { part.ScreenStart = Elapsed; Journal(); }; // ffmpeg can't report "running" for pipe input
        _win.Lost += msg => Warning?.Invoke(msg);
        _win.Start(_screen.Input);
    }

    private void StartScreenPart(JournalPart part)
    {
        var src = $"ddagrab=output_idx={_o.Monitor.OutputIdx}:framerate={_o.Fps}:draw_mouse=1";
        if (_o.Region != null) src += $":offset_x={_ox}:offset_y={_oy}:video_size={_w}x{_h}";
        var a = new List<string> { "-f", "lavfi", "-i", src };
        if (_o.ScreenEncoder.CaptureFilter != "") a.AddRange(["-vf", _o.ScreenEncoder.CaptureFilter]);
        a.AddRange(_o.ScreenEncoder.QualityArgs(ExportQuality.High));
        a.AddRange(["-g", (_o.Fps * 2).ToString(), .. SessionFinalizer.CrashSafeMkvArgs, "-y", part.Screen]);
        _screen = StartFfmpeg(a, "pantalla", t => { part.ScreenStart = t; Journal(); });
    }

    private async Task StopVideoAsync()
    {
        _videoStopping = true;
        var stop = Elapsed;
        _win?.Stop();
        await Task.WhenAll(
            (_win != null ? _screen?.CloseInputAsync(TimeSpan.FromSeconds(15)) : _screen?.StopAsync(TimeSpan.FromSeconds(15))) ?? Task.CompletedTask,
            _cam?.StopAsync(TimeSpan.FromSeconds(15)) ?? Task.CompletedTask);
        _win?.Dispose();
        _screen?.Dispose(); _cam?.Dispose();
        _screen = _cam = null;
        _win = null;
        _j.Parts[^1].Stop = stop;
        Journal();
    }

    private FfmpegProcess StartFfmpeg(List<string> args, string name, Action<double> started)
    {
        var f = new FfmpegProcess(args, _o.Dir);
        var tail = new Queue<string>();
        var isStarted = false;
        f.StdErr += l =>
        {
            // shortcut: stream start is taken when ffmpeg reports it is running (±1 frame-ish); the editor exposes manual sync for the rest.
            if (!isStarted && l.Contains("Press [q]")) { isStarted = true; started(Elapsed); }
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
        var lastFlush = 0.0;
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
            // Flush also rewrites the WAV header, so after a crash the file is valid up to the last second.
            if (Elapsed - lastFlush >= 1) { w.Flush(); lastFlush = Elapsed; }
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
        // A captured window can move: measure against where it is now.
        var (ox, oy) = _o.Window != null ? WindowCapture.Origin(_o.Window.Handle) : (_o.Monitor.X + _ox, _o.Monitor.Y + _oy);
        _cursor?.WriteLine(CursorLog.Format(new CursorEvent(t, x - ox, y - oy, (CursorKind)kind)));
        if (t - _lastCursorFlush >= 1) { _cursor?.Flush(); _lastCursorFlush = t; }
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
        _lock?.Dispose();

        await SessionFinalizer.FinalizeAsync(_o.Dir, _m, _o.CpuEncoder, w => Warning?.Invoke(w));
        return _o.Dir;
    }
}
