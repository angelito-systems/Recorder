using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop;

public enum JobState { Queued, Running, Done, Failed, Canceled }

/// <summary>
/// One export. It holds an immutable snapshot of the project taken when it was queued, so editing continues freely.
/// Annotation PNGs are rendered at queue time (they need the UI thread) into the job's own temp folder.
/// </summary>
public sealed class RenderJob : INotifyPropertyChanged
{
    public required StudioProject Project { get; init; }
    public required SessionManifest Manifest { get; init; }
    public required string SessionDir { get; init; }
    public required IReadOnlyList<CursorEvent> Cursor { get; init; }
    public required string OutputPath { get; init; }
    public required string TempDir { get; init; }
    public required Dictionary<int, (string Png, int X, int Y)> AnnotationImages { get; init; }
    public IReadOnlyList<KeyEvent> Keys { get; init; } = [];
    public Dictionary<string, (string Png, int W, int H)> KeyImages { get; init; } = [];
    public CancellationTokenSource Cts { get; } = new();

    public string Name => Path.GetFileName(OutputPath);
    public string Details { get; init; } = "";

    private JobState _state;
    public JobState State { get => _state; set { _state = value; Changed(); Changed(nameof(StatusText)); Changed(nameof(IsActive)); Changed(nameof(IsDone)); } }
    private double _progress;
    public double Progress { get => _progress; set { _progress = value; Changed(); Changed(nameof(StatusText)); } }
    private string? _error;
    public string? Error { get => _error; set { _error = value; Changed(); Changed(nameof(StatusText)); } }
    public string Encoder { get; set; } = "";

    public bool IsActive => State is JobState.Queued or JobState.Running;
    public bool IsDone => State == JobState.Done;
    public string StatusText => State switch
    {
        JobState.Queued => "En cola",
        JobState.Running => $"Exportando {Progress:P0}{(Encoder != "" ? " · " + Encoder : "")}",
        JobState.Done => $"Listo · {Encoder}",
        JobState.Failed => "Error: " + Error,
        _ => "Cancelado",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>App-wide queue: jobs run one at a time (encoders and disk don't benefit from running several at once).</summary>
public static class RenderQueue
{
    public static ObservableCollection<RenderJob> Jobs { get; } = [];
    private static readonly Dictionary<ExportFormat, Task<EncoderChoice>> s_encoders = [];
    private static Task<int>? s_cameraGpu;
    private static bool s_running;

    public static bool Busy => Jobs.Any(j => j.IsActive);

    public static void Enqueue(RenderJob job)
    {
        Jobs.Add(job);
        if (!s_running) _ = RunAsync();
    }

    public static void Cancel(RenderJob job)
    {
        job.Cts.Cancel();
        if (job.State == JobState.Queued) { job.State = JobState.Canceled; Cleanup(job); }
    }

    private static async Task RunAsync()
    {
        s_running = true;
        try
        {
            while (Jobs.FirstOrDefault(j => j.State == JobState.Queued) is { } job) await RunJob(job);
        }
        finally { s_running = false; }
    }

    private static async Task RunJob(RenderJob job)
    {
        job.State = JobState.Running;
        // ffmpeg picks the muxer from the extension, so the partial file keeps it; it's renamed only on success.
        var part = Path.Combine(Path.GetDirectoryName(job.OutputPath)!, Path.GetFileNameWithoutExtension(job.OutputPath) + ".part" + Path.GetExtension(job.OutputPath));
        try
        {
            var fmt = job.Project.Export.Format;
            if (!s_encoders.TryGetValue(fmt, out var probe)) s_encoders[fmt] = probe = EncoderProbe.ForExportAsync(fmt);
            var enc = await probe;
            job.Encoder = job.Project.Export.Format switch { ExportFormat.Wav => "PCM", ExportFormat.Png => "PNG", _ => enc.Name };
            var graph = Path.Combine(job.TempDir, "export.graph");
            var gpu = job.Project.Zooms.Count > 0 && fmt is not (ExportFormat.Wav or ExportFormat.Png) ? await (s_cameraGpu ??= EncoderProbe.CameraGpuAsync()) : -1;
            ExportPlan Plan(int vk) => Exporter.Build(job.Project, job.Manifest, job.SessionDir, job.Cursor, enc, part, graph,
                i => job.AnnotationImages.TryGetValue(i, out var v) ? v : null,
                job.Keys, c => job.KeyImages.TryGetValue(c, out var k) ? k : null, vk);
            var ui = Application.Current.Dispatcher;
            try { await Exporter.RunAsync(Plan(gpu), graph, v => ui.BeginInvoke(() => job.Progress = v), job.Cts.Token); }
            catch (Exception ex) when (gpu >= 0 && !job.Cts.IsCancellationRequested)
            {
                // A GPU path that passed the probe can still fail on a driver quirk; the CPU path always works.
                App.Log($"GPU camera failed, retrying on CPU: {ex.Message}");
                s_cameraGpu = Task.FromResult(-1);
                await Exporter.RunAsync(Plan(-1), graph, v => ui.BeginInvoke(() => job.Progress = v), job.Cts.Token);
            }
            File.Move(part, job.OutputPath, overwrite: true);
            job.Progress = 1;
            job.State = JobState.Done;
        }
        catch (OperationCanceledException) { TryDelete(part); job.State = JobState.Canceled; }
        catch (Exception ex)
        {
            TryDelete(part);
            App.Log($"export {job.Name}: {ex}");
            job.Error = ex.Message.Split('\n')[0];
            job.State = JobState.Failed;
        }
        finally { Cleanup(job); }
    }

    private static void Cleanup(RenderJob job)
    {
        try { if (Directory.Exists(job.TempDir)) Directory.Delete(job.TempDir, true); } catch (IOException) { }
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch (IOException) { } }
}
