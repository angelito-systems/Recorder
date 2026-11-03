using System.Diagnostics;
using System.IO;
using Angelito.ScreenStudio.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Angelito.ScreenStudio.Desktop.Capture;

public sealed record RecordOptions(MonitorInfo Monitor, int Fps, string? Webcam, bool Mic, bool SystemAudio,
    EncoderChoice ScreenEncoder, EncoderChoice CpuEncoder, string Dir);

/// <summary>
/// One recording = one folder of independent streams (see docs/FORMATS.md). Every stream stamps its start against
/// the same clock so the editor can align them; a failing stream is reported and the others keep recording.
/// </summary>
public sealed class Recorder
{
    private readonly RecordOptions _o;
    private readonly SessionManifest _m = new();
    private readonly Stopwatch _clock = new();
    private FfmpegProcess? _screen, _cam;
    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _sys;
    private WasapiOut? _silence;
    private WaveFileWriter? _micW, _sysW;
    private MouseHook? _hook;
    private StreamWriter? _cursor;
    private double _lastMove = -1;
    private bool _stopping;
    private readonly List<Task> _audioStopped = [];

    public double Elapsed => _clock.Elapsed.TotalSeconds;
    public float MicPeak { get; private set; }
    public float SystemPeak { get; private set; }
    /// <summary>Raised (any thread) when a stream fails mid-recording, e.g. a device was unplugged.</summary>
    public event Action<string>? Warning;

    public Recorder(RecordOptions o) => _o = o;

    public void Start()
    {
        Directory.CreateDirectory(_o.Dir);
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_o.Dir))!).AvailableFreeSpace < 2L << 30)
            throw new IOException("Hay menos de 2 GB libres en el disco de destino.");

        _m.Width = _o.Monitor.Width; _m.Height = _o.Monitor.Height; _m.Fps = _o.Fps;
        _m.Save(_o.Dir); // written first: an incomplete session is still recognizable after a crash
        _clock.Start();

        _cursor = new StreamWriter(Path.Combine(_o.Dir, _m.Cursor));
        _hook = new MouseHook();
        _hook.Event += OnMouse;

        // MKV while recording: survives a crash. Remuxed to MP4 on stop.
        var a = new List<string> { "-f", "lavfi", "-i", $"ddagrab=output_idx={_o.Monitor.OutputIdx}:framerate={_o.Fps}:draw_mouse=1" };
        if (_o.ScreenEncoder.CaptureFilter != "") a.AddRange(["-vf", _o.ScreenEncoder.CaptureFilter]);
        a.AddRange(_o.ScreenEncoder.QualityArgs(ExportQuality.High));
        a.AddRange(["-g", (_o.Fps * 2).ToString(), "-y", "screen.mkv"]);
        _screen = StartFfmpeg(a, "pantalla", t => _m.ScreenOffset = t);

        if (_o.Webcam != null)
        {
            var c = new List<string> { "-f", "dshow", "-rtbufsize", "256M", "-i", $"video={_o.Webcam}", "-pix_fmt", _o.CpuEncoder.CpuPixFmt };
            c.AddRange(_o.CpuEncoder.QualityArgs(ExportQuality.Balanced));
            c.AddRange(["-y", "webcam.mkv"]);
            _cam = StartFfmpeg(c, "cámara", t => _m.WebcamOffset = t);
            _m.Webcam = "webcam.mkv";
        }

        if (_o.Mic)
        {
            _mic = new WasapiCapture();
            _micW = new WaveFileWriter(Path.Combine(_o.Dir, "mic.wav"), _mic.WaveFormat);
            Hook(_mic, _micW, t => _m.MicOffset = t, p => MicPeak = p, "micrófono");
            _m.Mic = "mic.wav";
        }
        if (_o.SystemAudio)
        {
            // Loopback delivers nothing while the device is silent; playing silence keeps the stream continuous and in sync.
            _silence = new WasapiOut();
            _silence.Init(new SilenceProvider(new WaveFormat(48000, 16, 2)));
            _silence.Play();
            _sys = new WasapiLoopbackCapture();
            _sysW = new WaveFileWriter(Path.Combine(_o.Dir, "system.wav"), _sys.WaveFormat);
            Hook(_sys, _sysW, t => _m.SystemAudioOffset = t, p => SystemPeak = p, "audio del sistema");
            _m.SystemAudio = "system.wav";
        }
        _mic?.StartRecording();
        _sys?.StartRecording();
    }

    private FfmpegProcess StartFfmpeg(List<string> args, string name, Action<double> setOffset)
    {
        var f = new FfmpegProcess(args, _o.Dir);
        var started = false;
        var tail = new Queue<string>();
        f.StdErr += l =>
        {
            // shortcut: stream start is taken when ffmpeg reports it is running (±1 frame-ish); the editor exposes manual sync for the rest.
            if (!started && l.Contains("Press [q]")) { started = true; setOffset(Elapsed); }
            lock (tail) { tail.Enqueue(l); if (tail.Count > 8) tail.Dequeue(); }
        };
        f.Start();
        _ = f.WaitAsync().ContinueWith(_ =>
        {
            if (!_stopping) lock (tail) Warning?.Invoke($"La grabación de {name} se detuvo inesperadamente:\n{string.Join("\n", tail)}");
        });
        return f;
    }

    private void Hook(IWaveIn src, WaveFileWriter w, Action<double> setOffset, Action<float> peak, string name)
    {
        var first = true;
        src.DataAvailable += (_, e) =>
        {
            if (first) { first = false; setOffset(Math.Max(0, Elapsed - (double)e.BytesRecorded / src.WaveFormat.AverageBytesPerSecond)); }
            w.Write(e.Buffer, 0, e.BytesRecorded);
            peak(Peak(e.Buffer, e.BytesRecorded, src.WaveFormat));
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
        var t = Elapsed;
        if (kind == 0 && t - _lastMove < 1 / 120.0) return; // moves throttled to 120 Hz; clicks always kept
        if (kind == 0) _lastMove = t;
        _cursor?.WriteLine(CursorLog.Format(new CursorEvent(t, x - _o.Monitor.X, y - _o.Monitor.Y, (CursorKind)kind)));
    }

    /// <summary>Stops every stream, finalizes files and returns the session folder.</summary>
    public async Task<string> StopAsync()
    {
        _stopping = true;
        _hook?.Dispose();
        _mic?.StopRecording();
        _sys?.StopRecording();
        _silence?.Stop();
        await Task.WhenAny(Task.WhenAll(_audioStopped), Task.Delay(3000)); // writers must outlive the last DataAvailable
        await Task.WhenAll(
            _screen?.StopAsync(TimeSpan.FromSeconds(15)) ?? Task.CompletedTask,
            _cam?.StopAsync(TimeSpan.FromSeconds(15)) ?? Task.CompletedTask);
        _micW?.Dispose(); _sysW?.Dispose();
        _mic?.Dispose(); _sys?.Dispose(); _silence?.Dispose();
        _cursor?.Dispose();
        _screen?.Dispose(); _cam?.Dispose();

        _m.Screen = await Remux("screen");
        if (_m.Webcam != null) _m.Webcam = await Remux("webcam");
        _m.Complete = true;
        _m.Save(_o.Dir);
        return _o.Dir;
    }

    /// <summary>MKV → MP4 stream copy (seconds, no re-encode). On failure the MKV is kept and used as-is.</summary>
    private async Task<string> Remux(string name)
    {
        var mkv = Path.Combine(_o.Dir, name + ".mkv");
        if (!File.Exists(mkv)) throw new IOException($"No se generó {name}.mkv; revise el registro de diagnóstico.");
        var (code, _) = await FfmpegProcess.RunAsync(["-y", "-loglevel", "error", "-i", mkv, "-c", "copy", "-movflags", "+faststart", name + ".mp4"], workingDir: _o.Dir);
        if (code != 0) return name + ".mkv";
        File.Delete(mkv);
        return name + ".mp4";
    }
}
