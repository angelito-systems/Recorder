using System.Text.Json;
using System.Text.Json.Serialization;

namespace Angelito.ScreenStudio.Core;

/// <summary>A kept range of the source recording, in screen-video seconds.</summary>
public sealed record Segment(double Start, double End)
{
    [JsonIgnore] public double Duration => End - Start;
}

/// <summary>X/Y are the zoom center normalized to 0..1 of the source frame. Ramp is ease-in/out time in seconds.</summary>
public sealed record ZoomSegment(double Start, double End, double Scale, double X, double Y, double Ramp = 0.6);

public sealed record CursorStyle(bool ShowClicks = true, string Color = "#8B5CF6", int Size = 56, double Duration = 0.5);

public enum WebcamShape { Circle, Rounded }

/// <summary>X/Y are the webcam center normalized to the output frame; Size is the diameter as a fraction of output height.</summary>
public sealed record WebcamLayout(bool Visible = true, WebcamShape Shape = WebcamShape.Circle, double X = 0.88, double Y = 0.8,
    double Size = 0.28, bool Mirror = true);

public sealed record AudioMix(double MicGainDb = 0, double SystemGainDb = 0, bool MicMuted = false, bool SystemMuted = false,
    double MicSync = 0, double SystemSync = 0);

public enum ExportQuality { Fast, Balanced, High }

/// <summary>Height 0 keeps the source size.</summary>
public sealed record ExportSettings(int Height = 0, ExportQuality Quality = ExportQuality.Balanced);

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
    public CursorStyle Cursor { get; init; } = new();
    public WebcamLayout Webcam { get; init; } = new();
    public AudioMix Audio { get; init; } = new();
    public ExportSettings Export { get; init; } = new();

    public static StudioProject FromSession(double duration, IReadOnlyList<CursorEvent> cursor, int width, int height) => new()
    {
        Segments = [new Segment(0, duration)],
        Zooms = AutoZoom.Propose(cursor, width, height, duration),
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
