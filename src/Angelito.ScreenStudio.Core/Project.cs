using System.Text.Json;
using System.Text.Json.Serialization;

namespace Angelito.ScreenStudio.Core;

/// <summary>A kept range of the source recording, in screen-video seconds.</summary>
public sealed record Segment(double Start, double End)
{
    [JsonIgnore] public double Duration => End - Start;
}

/// <summary>
/// X/Y are the zoom center normalized to 0..1 of the source frame. Ramp is the ease-in time in seconds; RampOut the
/// ease-out time (negative = same as Ramp, which is how projects saved before it existed read). Auto marks zooms made
/// by <see cref="AutoZoom"/>, so regenerating replaces only those and keeps the user's own.
/// </summary>
public sealed record ZoomSegment(double Start, double End, double Scale, double X, double Y, double Ramp = 0.6, double RampOut = -1, bool Auto = false);

/// <summary>
/// Auto-zoom tuning (Efectos → Zoom). Times in seconds. IdleReturn: inactivity after the last click before zooming
/// back out. Follow: a click elsewhere while zoomed pans there instead of zooming out and in again.
/// </summary>
public sealed record AutoZoomSettings(bool Enabled = true, double Scale = 1.5, double ZoomIn = 0.45, double MinHold = 1.2,
    double IdleReturn = 2.0, double ZoomOut = 0.6, bool Follow = true);

public sealed record CursorStyle(bool ShowClicks = true, string Color = "#8B5CF6", int Size = 56, double Duration = 0.5);

public enum WebcamShape { Circle, Rounded }

/// <summary>X/Y are the webcam center normalized to the output frame; Size is the diameter as a fraction of output height.</summary>
public sealed record WebcamLayout(bool Visible = true, WebcamShape Shape = WebcamShape.Circle, double X = 0.88, double Y = 0.8,
    double Size = 0.28, bool Mirror = true);

public sealed record AudioMix(double MicGainDb = 0, double SystemGainDb = 0, bool MicMuted = false, bool SystemMuted = false,
    double MicSync = 0, double SystemSync = 0);

/// <summary>On-screen text, drawn above everything. Times are source seconds (so text follows the content through cuts);
/// X/Y is the center normalized to the output frame; Size is the font size as a fraction of output height.</summary>
public sealed record TextItem(double Start, double End, string Text, double X = 0.5, double Y = 0.15, double Size = 0.07,
    string Color = "#FFFFFF", bool Background = true, bool Bold = true);

/// <summary>One subtitle, in source seconds like everything else; SRT import/export converts to output time.</summary>
public sealed record Cue(double Start, double End, string Text);

/// <summary>Y is the vertical center normalized to the output frame. Burn draws subtitles into the exported video.</summary>
public sealed record SubtitleStyle(double Size = 0.045, double Y = 0.9, string Color = "#FFFFFF", bool Background = true, bool Burn = true);

public enum AnnotationKind { Rectangle, Ellipse, Arrow, Highlight, Blur, Pixelate }

/// <summary>
/// A shape on the recording, in source space so it stays on the content under zoom. (X,Y)→(X2,Y2) are normalized to the
/// source frame: the arrow's tail→head, or two opposite corners for the other kinds. Thickness is in source pixels.
/// </summary>
public sealed record Annotation(double Start, double End, AnnotationKind Kind, double X, double Y, double X2, double Y2,
    string Color = "#EF4444", double Thickness = 6)
{
    [JsonIgnore] public bool IsMask => Kind is AnnotationKind.Blur or AnnotationKind.Pixelate;

    /// <summary>Box in source pixels, corners normalized (for masks and rectangle-like shapes).</summary>
    public (int X, int Y, int W, int H) BoxPx(int width, int height)
    {
        int x = (int)Math.Round(Math.Min(X, X2) * width), y = (int)Math.Round(Math.Min(Y, Y2) * height);
        int w = (int)Math.Round(Math.Abs(X2 - X) * width), h = (int)Math.Round(Math.Abs(Y2 - Y) * height);
        return (x, y, Math.Max(1, w), Math.Max(1, h));
    }
}

public enum CanvasAspect { Source, Wide, Vertical, Square, Classic }

/// <summary>
/// Output frame shape. The canvas shows the largest window of its aspect that fits the source; X/Y (normalized source
/// coordinates) is where that window is centered when no zoom is active, so reframing is editable, never automatic.
/// </summary>
/// <summary>
/// The recording shown as a rounded card on a gradient. Padding is the margin on each side as a fraction of the output
/// size (the screen keeps its aspect: it is scaled by 1 − 2·Padding); Radius is the corner radius as a fraction of the
/// output's short side. Webcam, texts and shortcuts stay positioned on the whole frame.
/// </summary>
public sealed record BackgroundStyle(bool Enabled = false, string Color1 = "#1E1B4B", string Color2 = "#7C3AED", double Padding = 0.06, double Radius = 0.02);

public sealed record ProjectCanvas(CanvasAspect Aspect = CanvasAspect.Source, double X = 0.5, double Y = 0.5)
{
    public double Ratio(double w, double h) => Aspect switch
    {
        CanvasAspect.Wide => 16 / 9.0, CanvasAspect.Vertical => 9 / 16.0, CanvasAspect.Square => 1, CanvasAspect.Classic => 4 / 3.0, _ => w / h,
    };

    /// <summary>Window size in source pixels.</summary>
    public (double W, double H) Window(double w, double h)
    {
        var r = Ratio(w, h);
        return r >= w / h ? (w, w / r) : (h * r, h);
    }
}

public enum KeyLook { Capsule, Minimal }

/// <summary>Keystroke overlay style. X/Y is the center normalized to the output; Size is relative to its short side.</summary>
public sealed record KeystrokeStyle(bool Visible = true, KeyLook Look = KeyLook.Capsule, double X = 0.5, double Y = 0.8, double Size = 0.04, double Duration = 1.2);

public enum ExportQuality { Fast, Balanced, High, Max }

public enum ExportFormat { Mp4H264, Mp4Hevc, MkvH264, Wav, Png, Mp4Av1 }

/// <summary>Height is the short side of the output in pixels; 0 keeps the canvas window's own pixel size.</summary>
/// <summary>Fps 0 keeps the recording's rate.</summary>
public sealed record ExportSettings(int Height = 0, ExportQuality Quality = ExportQuality.Balanced, ExportFormat Format = ExportFormat.Mp4H264, int Fps = 0)
{
    public string Extension => Format switch { ExportFormat.MkvH264 => ".mkv", ExportFormat.Wav => ".wav", ExportFormat.Png => ".png", _ => ".mp4" };
}

/// <summary>.angelstudio file. Immutable records: every edit produces a new value, which makes undo a stack of values.</summary>
public sealed record StudioProject
{
    public const int CurrentFormat = 1;
    public const string Extension = ".angelstudio";
    public int FormatVersion { get; init; } = CurrentFormat;
    /// <summary>Session folder, relative to the project file when it lives inside/next to it.</summary>
    public string SessionDir { get; init; } = ".";
    public List<Segment> Segments { get; init; } = [];
    public List<ZoomSegment> Zooms { get; init; } = [];
    public AutoZoomSettings AutoZoom { get; init; } = new();
    public CursorStyle Cursor { get; init; } = new();
    public WebcamLayout Webcam { get; init; } = new();
    public AudioMix Audio { get; init; } = new();
    public ExportSettings Export { get; init; } = new();
    public ProjectCanvas Canvas { get; init; } = new();
    public BackgroundStyle Background { get; init; } = new();
    public KeystrokeStyle Keys { get; init; } = new();
    public List<TextItem> Texts { get; init; } = [];
    public List<Cue> Subtitles { get; init; } = [];
    public List<Annotation> Annotations { get; init; } = [];
    public SubtitleStyle SubtitleStyle { get; init; } = new();
    public static StudioProject FromSession(double duration, IReadOnlyList<CursorEvent> cursor, int width, int height) => new()
    {
        Segments = [new Segment(0, duration)],
        Zooms = Core.AutoZoom.Propose(cursor, width, height, duration),
    };

    public string ToJson() => JsonSerializer.Serialize(this, SessionManifest.Json);

    public static StudioProject FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var version = doc.RootElement.TryGetProperty("formatVersion", out var v) ? v.GetInt32() : 0;
        if (version > CurrentFormat)
            throw new InvalidDataException($"El proyecto usa el formato {version}; esta versión solo abre hasta {CurrentFormat}.");
        // Migrations go here when CurrentFormat increases (version → version+1).
        var p = doc.Deserialize<StudioProject>(SessionManifest.Json) ?? throw new InvalidDataException("Proyecto vacío");
        return p with { FormatVersion = CurrentFormat };
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, ToJson());

    /// <summary>Loads a project; if the main file is corrupt, falls back to the .bak written by the previous save.</summary>
    public static StudioProject Load(string path)
    {
        try { return FromJson(File.ReadAllText(path)); }
        catch (JsonException) when (File.Exists(path + ".bak")) { return FromJson(File.ReadAllText(path + ".bak")); }
    }

    public string ResolveSessionDir(string projectPath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, SessionDir));

    [JsonIgnore] public double OutputDuration => Segments.Sum(s => s.Duration);

    /// <summary>Source time → output time (after cuts), or null if t is in a removed range.</summary>
    public double? ToOutput(double t)
    {
        double acc = 0;
        foreach (var s in Segments)
        {
            if (t >= s.Start && t <= s.End) return acc + t - s.Start;
            acc += s.Duration;
        }
        return null;
    }

    /// <summary>Output time → source time; clamped to the last kept frame.</summary>
    public double ToSource(double o)
    {
        foreach (var s in Segments)
        {
            if (o <= s.Duration) return s.Start + Math.Max(0, o);
            o -= s.Duration;
        }
        return Segments.Count > 0 ? Segments[^1].End : 0;
    }

    /// <summary>The output-time span of a source range, trimmed to what survives the cuts; null if fully removed.</summary>
    public (double Start, double End)? OutputRange(double a, double b)
    {
        // Pieces in output order; consecutive ones join. With reordered clips a range can split apart: the first run wins.
        // Mapped per clip: a boundary instant belongs to two clips, so ToOutput would be ambiguous here.
        var pieces = new List<(double S, double E)>();
        double acc = 0;
        foreach (var s in Segments)
        {
            double ps = Math.Max(s.Start, a), pe = Math.Min(s.End, b);
            if (pe > ps) pieces.Add((acc + ps - s.Start, acc + pe - s.Start));
            acc += s.Duration;
        }
        pieces.Sort();
        if (pieces.Count == 0) return null;
        var run = pieces[0];
        foreach (var r in pieces.Skip(1))
            if (r.S - run.E < 1e-6) run.E = Math.Max(run.E, r.E); else break;
        return run;
    }

    /// <summary>Moves the clip containing source time t one place earlier (dir -1) or later (+1) in the output.</summary>
    public StudioProject MoveSegment(double t, int dir)
    {
        var i = Segments.FindIndex(s => t >= s.Start && t < s.End);
        var j = i + Math.Sign(dir);
        if (i < 0 || j < 0 || j >= Segments.Count) return this;
        var list = new List<Segment>(Segments);
        (list[i], list[j]) = (list[j], list[i]);
        return this with { Segments = list };
    }

    /// <summary>
    /// Playback in output order: the clip index to play at source time t, given the one playing (-1 if none).
    /// Running off the end of the current clip continues with the next in the list; -1 means the end.
    /// </summary>
    public int PlaybackSegment(int current, double t)
    {
        if (current >= 0 && current < Segments.Count)
        {
            var c = Segments[current];
            if (t >= c.Start - 0.05 && t < c.End) return current;
            if (t >= c.End && t < c.End + 0.5) return current + 1 < Segments.Count ? current + 1 : -1; // natural run-off
        }
        var inside = Segments.FindIndex(s => t >= s.Start - 0.02 && t < s.End);
        if (inside >= 0) return inside;
        // In a removed range (e.g. after a seek): the next kept source range, else the end.
        var next = Segments.Select((s, k) => (s, k)).Where(x => x.s.Start > t).OrderBy(x => x.s.Start).FirstOrDefault();
        return next.s == null ? -1 : next.k;
    }

    /// <summary>SRT in output time, i.e. aligned with the exported video. Cues inside removed ranges are dropped.</summary>
    public string ExportSrt() => Srt.Format(Subtitles.OrderBy(c => c.Start)
        .Select(c => OutputRange(c.Start, c.End) is { } r ? new Cue(r.Start, r.End, c.Text) : null)
        .Where(c => c != null && c.End - c.Start > 0.01)!);

    /// <summary>Replaces subtitles with an SRT written against the exported video's timing.</summary>
    public StudioProject ImportSrt(string srt) =>
        this with { Subtitles = Srt.Parse(srt).Select(c => new Cue(ToSource(c.Start), ToSource(c.End), c.Text)).Where(c => c.End > c.Start).ToList() };

    /// <summary>The project reduced to the single frame at source time t, for a PNG still through the full pipeline.</summary>
    public StudioProject StillAt(double t, int fps) => this with
    {
        Segments = [new Segment(t, t + 1.5 / fps)],
        Export = Export with { Format = ExportFormat.Png },
    };

    /// <summary>Splits the segment containing t. No-op if t is at an edge or in a removed range.</summary>
    public StudioProject SplitAt(double t)
    {
        var i = Segments.FindIndex(s => t > s.Start + 0.05 && t < s.End - 0.05);
        if (i < 0) return this;
        var s = Segments[i];
        var list = new List<Segment>(Segments);
        list.RemoveAt(i);
        list.InsertRange(i, [s with { End = t }, s with { Start = t }]);
        return this with { Segments = list };
    }

    /// <summary>Removes the segment containing t, keeping at least one segment.</summary>
    public StudioProject DeleteAt(double t)
    {
        if (Segments.Count <= 1) return this;
        var i = Segments.FindIndex(s => t >= s.Start && t < s.End);
        if (i < 0) return this;
        var list = new List<Segment>(Segments);
        list.RemoveAt(i);
        return this with { Segments = list };
    }
}

/// <summary>Snapshot undo/redo — projects are small, so storing values is simpler and safer than command objects.</summary>
public sealed class History(StudioProject initial)
{
    private readonly Stack<StudioProject> _undo = new(), _redo = new();
    public StudioProject Current { get; private set; } = initial;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(StudioProject next)
    {
        if (next == Current || next.ToJson() == Current.ToJson()) return;
        _undo.Push(Current);
        _redo.Clear();
        Current = next;
    }

    /// <summary>Amends the current state without a new undo step (used while a slider is being dragged).</summary>
    public void Replace(StudioProject next)
    {
        if (!CanUndo) { Push(next); return; }
        _redo.Clear();
        Current = next;
    }

    public void Undo() { if (CanUndo) { _redo.Push(Current); Current = _undo.Pop(); } }
    public void Redo() { if (CanRedo) { _undo.Push(Current); Current = _redo.Pop(); } }
}
