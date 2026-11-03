using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop.Capture;

/// <summary>
/// Small live webcam picture for the recorder: ffmpeg reads the dshow device and pipes raw BGRA frames into a bitmap.
/// Must be stopped before recording, since a dshow camera serves one reader at a time.
/// </summary>
public sealed class CameraPreview : IDisposable
{
    public const int W = 192, H = 108;
    private readonly Process _p;
    private readonly CancellationTokenSource _cts = new();
    public WriteableBitmap Bitmap { get; } = new(W, H, 96, 96, PixelFormats.Bgra32, null);

    public CameraPreview(string device)
    {
        _p = new Process
        {
            StartInfo = new ProcessStartInfo(FfmpegProcess.Locate())
            {
                ArgumentList = { "-loglevel", "error", "-f", "dshow", "-i", $"video={device}",
                    "-vf", $"fps=15,scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H},hflip", "-f", "rawvideo", "-pix_fmt", "bgra", "-" },
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                UseShellExecute = false, CreateNoWindow = true,
            },
        };
        _p.ErrorDataReceived += (_, _) => { }; // drained so ffmpeg never blocks on a full stderr pipe
        _p.Start();
        ChildProcessJob.Attach(_p); // dies with the app, like every other ffmpeg we start
        _p.BeginErrorReadLine();
        _ = Task.Run(Pump);
    }

    private async Task Pump()
    {
        var frame = new byte[W * H * 4];
        var s = _p.StandardOutput.BaseStream;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await s.ReadExactlyAsync(frame, _cts.Token);
                var copy = (byte[])frame.Clone();
                Application.Current?.Dispatcher.BeginInvoke(() => Bitmap.WritePixels(new Int32Rect(0, 0, W, H), copy, W * 4, 0));
            }
        }
        catch (Exception e) when (e is EndOfStreamException or OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    /// <summary>Stops ffmpeg and waits until it has released the camera.</summary>
    public async Task StopAsync()
    {
        _cts.Cancel();
        try { if (!_p.HasExited) { await _p.StandardInput.WriteAsync('q'); await _p.StandardInput.FlushAsync(); } } catch (IOException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _p.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { try { _p.Kill(true); } catch (InvalidOperationException) { } }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (!_p.HasExited) _p.Kill(true); } catch (InvalidOperationException) { }
        _p.Dispose();
    }
}
