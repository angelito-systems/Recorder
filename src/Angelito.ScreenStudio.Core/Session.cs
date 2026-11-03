using System.Text.Json;
using System.Text.Json.Serialization;

namespace Angelito.ScreenStudio.Core;

/// <summary>Mouse event kinds. Keyboard input is never recorded.</summary>
public enum CursorKind { Move = 0, Left = 1, Right = 2, Middle = 3 }

/// <summary>T is seconds since session start; X/Y are pixels relative to the captured area.</summary>
public readonly record struct CursorEvent(double T, int X, int Y, CursorKind K);

/// <summary>
/// session.json — describes one recording. Each stream is a separate file; offsets are seconds from
/// session start (t0) to that stream's first sample, so streams can be re-aligned in the editor.
/// </summary>
public sealed class SessionManifest
{
    public const string FileName = "session.json";
    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool Complete { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public string Screen { get; set; } = "screen.mp4";
    public double ScreenOffset { get; set; }
    public string? Webcam { get; set; }
    public double WebcamOffset { get; set; }
    public string? Mic { get; set; }
    public double MicOffset { get; set; }
    public string? SystemAudio { get; set; }
    public double SystemAudioOffset { get; set; }
    public string Cursor { get; set; } = "cursor.txt";

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static SessionManifest Load(string dir) =>
        JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(Path.Combine(dir, FileName)), Json)
        ?? throw new InvalidDataException("session.json vacío");

    public void Save(string dir) => AtomicFile.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(this, Json));

    /// <summary>Cursor events converted to screen-video time (t - ScreenOffset). Truncated trailing lines from a crash are skipped.</summary>
    public List<CursorEvent> LoadCursor(string dir)
    {
        var path = Path.Combine(dir, Cursor);
        var list = new List<CursorEvent>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            if (CursorLog.TryParse(line, out var e)) list.Add(e with { T = e.T - ScreenOffset });
        }
        return list;
    }
}

/// <summary>Compact line format "t x y k" — cheap to write from a mouse hook.</summary>
public static class CursorLog
{
    public static string Format(in CursorEvent e) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{e.T:F4} {e.X} {e.Y} {(int)e.K}");

    public static bool TryParse(string line, out CursorEvent e)
    {
        e = default;
        var p = line.Split(' ');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (p.Length != 4 || !double.TryParse(p[0], inv, out var t) || !int.TryParse(p[1], inv, out var x)
            || !int.TryParse(p[2], inv, out var y) || !int.TryParse(p[3], inv, out var k) || k is < 0 or > 3) return false;
        e = new CursorEvent(t, x, y, (CursorKind)k);
        return true;
    }
}

public static class AtomicFile
{
    /// <summary>Write to a temp file then replace, keeping the previous version as .bak — a crash never leaves a half-written file.</summary>
    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }
}
