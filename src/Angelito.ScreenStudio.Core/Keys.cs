using System.Globalization;
using System.Text.Json;

namespace Angelito.ScreenStudio.Core;

/// <summary>A shortcut shown on screen. T is seconds (session time while recording, video time after).</summary>
public sealed record KeyEvent(double T, string Combo);

[Flags]
public enum Mods { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8, AltGr = 16 }

/// <summary>
/// Decides which key presses may be stored. Privacy by construction: only combinations on a positive list are ever
/// returned, so typed text, passwords and form input never reach the log — no reliance on detecting password fields.
/// </summary>
public sealed class KeyFilter
{
    /// <summary>Toggles capture on/off; never logged itself.</summary>
    public const string SuspendCombo = "Ctrl+Shift+F12";

    public static readonly string[] DefaultAllowed =
    [
        // editing
        "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z", "Ctrl+S", "Ctrl+Shift+S", "Ctrl+F", "Ctrl+H",
        "Ctrl+P", "Ctrl+N", "Ctrl+O", "Ctrl+W", "Ctrl+T", "Ctrl+Shift+T", "Ctrl+R", "Ctrl+B", "Ctrl+I", "Ctrl+U", "Ctrl+D",
        "Ctrl+K", "Ctrl+L", "Ctrl+E", "Ctrl+G", "Ctrl+J", "Ctrl+Q",
        "Ctrl+Tab", "Ctrl+Shift+Tab", "Ctrl+Enter", "Ctrl+Space", "Ctrl+Backspace", "Ctrl+Delete", "Ctrl+Home", "Ctrl+End",
        "Ctrl+Left", "Ctrl+Right", "Ctrl+Up", "Ctrl+Down", "Ctrl+Plus", "Ctrl+Minus", "Ctrl+0", "Ctrl+Slash", "Ctrl+Backtick",
        // programming tools
        "Ctrl+Shift+P", "Ctrl+Shift+N", "Ctrl+Shift+F", "Ctrl+Shift+K", "Ctrl+Shift+E", "Ctrl+Shift+B", "Ctrl+Shift+Esc",
        "Ctrl+Shift+Left", "Ctrl+Shift+Right", "Ctrl+Shift+Up", "Ctrl+Shift+Down", "Alt+Shift+F", "Alt+Up", "Alt+Down",
        "Alt+Shift+Up", "Alt+Shift+Down", "Ctrl+Alt+Up", "Ctrl+Alt+Down", "Shift+Alt+Left", "Shift+Alt+Right",
        // windows
        "Alt+Tab", "Alt+Shift+Tab", "Alt+F4", "Alt+Enter", "Alt+Left", "Alt+Right",
        "Win+E", "Win+D", "Win+R", "Win+I", "Win+S", "Win+V", "Win+Tab", "Win+Shift+S", "Win+Left", "Win+Right", "Win+Up",
        "Win+Down", "Win+Period", "Win+X", "Win+A", "Win+M", "Win+Ctrl+D", "Win+Ctrl+Left", "Win+Ctrl+Right",
        // function keys alone don't type text
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "Shift+F5", "Shift+F10", "Ctrl+F5", "Ctrl+F12",
    ];

    /// <summary>Password managers: nothing is captured while they have focus.</summary>
    public static readonly string[] DefaultExcludedApps =
        ["keepass", "keepassxc", "1password", "bitwarden", "lastpass", "dashlane", "enpass", "nordpass", "protonpass", "roboform", "keeper", "credentialuibroker", "consent"];

    private readonly HashSet<string> _allowed, _excluded;
    public bool Suspended { get; private set; }

    public KeyFilter(IEnumerable<string>? extraAllowed = null, IEnumerable<string>? extraExcludedApps = null)
    {
        _allowed = new(DefaultAllowed.Concat(extraAllowed ?? []).Select(Normalize).Where(c => c != null)!, StringComparer.OrdinalIgnoreCase);
        _allowed.Remove(SuspendCombo);
        _excluded = new(DefaultExcludedApps.Concat(extraExcludedApps ?? []).Select(a => a.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The combo to log for a key-down of <paramref name="vk"/>, or null. <paramref name="app"/> is the foreground process name.</summary>
    public string? Accept(int vk, Mods mods, string? app)
    {
        if (IsModifier(vk)) return null;
        if (mods.HasFlag(Mods.AltGr)) return null; // AltGr types characters (e.g. "@" on Spanish layouts)
        var key = KeyName(vk);
        if (key == null) return null;
        var combo = Format(mods, key);
        if (combo == SuspendCombo) { Suspended = !Suspended; return null; }
        if (Suspended || !_allowed.Contains(combo)) return null;
        if (app != null && _excluded.Contains(app.ToLowerInvariant())) return null;
        return combo;
    }

    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;

    /// <summary>Canonical "Ctrl+Alt+Shift+Win+Key" form.</summary>
    public static string Format(Mods m, string key) =>
        string.Concat(m.HasFlag(Mods.Ctrl) ? "Ctrl+" : "", m.HasFlag(Mods.Alt) ? "Alt+" : "", m.HasFlag(Mods.Shift) ? "Shift+" : "", m.HasFlag(Mods.Win) ? "Win+" : "", key);

    /// <summary>Normalizes a user-written combo ("shift+ctrl+s" → "Ctrl+Shift+S"); null if it isn't one.</summary>
    public static string? Normalize(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var m = Mods.None;
        foreach (var p in parts[..^1])
            m |= p.ToLowerInvariant() switch { "ctrl" or "control" => Mods.Ctrl, "alt" => Mods.Alt, "shift" => Mods.Shift, "win" or "windows" => Mods.Win, _ => Mods.AltGr };
        if (m.HasFlag(Mods.AltGr)) return null;
        return KnownKeys.TryGetValue(parts[^1], out var key) ? Format(m, key) : null;
    }

    private static readonly Dictionary<string, string> KnownKeys =
        Enumerable.Range(0, 256).Select(KeyName).OfType<string>().Distinct().ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);

    public static string? KeyName(int vk) => vk switch
    {
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc", 0x20 => "Space",
        0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x2D => "Insert", 0x2E => "Delete",
        0xBB or 0x6B => "Plus", 0xBD or 0x6D => "Minus", 0xBE => "Period", 0xBC => "Comma", 0xBF or 0x6F => "Slash", 0xC0 => "Backtick",
        _ => null,
    };

    /// <summary>Display form: arrows and named keys as symbols people recognize.</summary>
    public static string[] DisplayParts(string combo) => combo.Split('+').Select(p => p switch
    {
        "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓", "Plus" => "+", "Minus" => "−", "Period" => ".", "Comma" => ",",
        "Slash" => "/", "Backtick" => "`", "Enter" => "Enter", "Win" => "⊞ Win", "Shift" => "⇧ Shift", "Backspace" => "⌫", _ => p,
    }).ToArray();
}

/// <summary>keys.json in the app data folder: extra allowed shortcuts and excluded apps (process names).</summary>
public sealed record KeySettings(List<string> ExtraAllowed, List<string> ExcludedApps)
{
    public static KeySettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<KeySettings>(File.ReadAllText(path), SessionManifest.Json) ?? Empty;
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Empty, SessionManifest.Json)); // discoverable template
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return Empty;
    }

    private static KeySettings Empty => new([], []);
}

public static class KeyLog
{
    public static string Format(KeyEvent e) => string.Create(CultureInfo.InvariantCulture, $"{e.T:F4} {e.Combo}");

    public static bool TryParse(string line, out KeyEvent e)
    {
        e = null!;
        var i = line.IndexOf(' ');
        if (i <= 0 || !double.TryParse(line.AsSpan(0, i), CultureInfo.InvariantCulture, out var t)) return false;
        var combo = KeyFilter.Normalize(line[(i + 1)..]);
        if (combo == null) return false;
        e = new KeyEvent(t, combo);
        return true;
    }

    /// <summary>Display windows: each shortcut shows for <paramref name="duration"/> or until the next one replaces it.</summary>
    public static List<(double Start, double End, string Combo)> Windows(IReadOnlyList<KeyEvent> events, double duration)
    {
        var sorted = events.OrderBy(e => e.T).ToList();
        var list = new List<(double, double, string)>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var end = sorted[i].T + duration;
            if (i + 1 < sorted.Count) end = Math.Min(end, sorted[i + 1].T);
            if (end - sorted[i].T > 0.02) list.Add((sorted[i].T, end, sorted[i].Combo));
        }
        return list;
    }
}
