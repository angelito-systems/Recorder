using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Angelito.ScreenStudio.Core;

/// <summary>SubRip (.srt). Times here are whatever timeline the caller uses; the project converts to/from output time.</summary>
public static partial class Srt
{
    public static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        var blocks = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            var ti = lines.FindIndex(l => Timing().IsMatch(l)); // the index line is optional in the wild
            if (ti < 0) continue;
            var m = Timing().Match(lines[ti]);
            var body = string.Join("\n", lines.Skip(ti + 1).Select(l => Tags().Replace(l, "")));
            double a = Time(m, 1), b = Time(m, 5);
            if (b > a && body.Length > 0) cues.Add(new Cue(a, b, body));
        }
        return cues.OrderBy(c => c.Start).ToList();
    }

    public static string Format(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder();
        var i = 1;
        foreach (var c in cues)
            sb.Append($"{i++}\r\n{Stamp(c.Start)} --> {Stamp(c.End)}\r\n{c.Text.Replace("\n", "\r\n")}\r\n\r\n");
        return sb.ToString();
    }

    private static double Time(Match m, int g) =>
        int.Parse(m.Groups[g].Value) * 3600 + int.Parse(m.Groups[g + 1].Value) * 60 + int.Parse(m.Groups[g + 2].Value)
        + int.Parse(m.Groups[g + 3].Value.PadRight(3, '0')[..3]) / 1000.0;

    private static string Stamp(double s)
    {
        var ms = (long)Math.Round(Math.Max(0, s) * 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    [GeneratedRegex(@"(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})")] private static partial Regex Timing();
    [GeneratedRegex(@"</?[ibu]>|\{\\[^}]*\}", RegexOptions.IgnoreCase)] private static partial Regex Tags();
}
