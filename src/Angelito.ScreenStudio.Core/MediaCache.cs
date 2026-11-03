using System.Diagnostics;
using System.Globalization;

namespace Angelito.ScreenStudio.Core;

/// <summary>
/// Timeline thumbnails and audio peaks, generated once per media file into the session's ".cache" folder (new files only;
/// originals untouched) and reused until the source changes. Each is one streaming ffmpeg pass — nothing is held in RAM.
/// </summary>
public static class MediaCache
{
    public const int PeaksPerSecond = 100, ThumbHeight = 72;
    private const int PeakRate = 8000; // mono samples/s decoded for peaks; 80 samples per bin

    /// <summary>Seconds between thumbnails: 2 s (the recorder's keyframe interval), widened so there are at most ~600.</summary>
    public static double ThumbInterval(double duration) => Math.Max(2, Math.Ceiling(duration / 600));

    public static string ThumbPath(string dir, int index) => Path.Combine(dir, $"{index + 1:D5}.jpg"); // ffmpeg numbers from 1

    /// <summary>Generates (or reuses) thumbnails; thumbnail i shows time i × interval. Returns the folder.</summary>
    public static async Task<string> ThumbnailsAsync(string video, string sessionDir, double duration, CancellationToken ct)
    {
        var interval = ThumbInterval(duration);
        var dir = Path.Combine(sessionDir, ".cache", F($"thumbs-{Path.GetFileNameWithoutExtension(video)}-{interval}"));
        if (IsFresh(dir, video)) return dir;
        var tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        // Decoding keyframes only keeps this fast even for hour-long recordings.
        // shortcut: assumes a keyframe at least every interval (true for our recordings, -g = 2 s); sparser GOPs give slightly stale thumbs.
        var (code, log) = await FfmpegProcess.RunAsync(["-y", "-loglevel", "error", "-skip_frame", "nokey", "-i", video,
            "-vf", F($"fps=1/{interval},scale=-2:{ThumbHeight}"), "-q:v", "6", Path.Combine(tmp, "%05d.jpg")], ct: ct);
        if (code != 0) throw new InvalidDataException("No se pudieron generar miniaturas:\n" + log);
        Commit(tmp, dir);
        return dir;
    }

    /// <summary>Peak per 1/<see cref="PeaksPerSecond"/> s, 0..255, mono mix of all channels.</summary>
    public static async Task<byte[]> PeaksAsync(string audio, string sessionDir, CancellationToken ct)
    {
        var dir = Path.Combine(sessionDir, ".cache", $"peaks-{Path.GetFileNameWithoutExtension(audio)}");
        var file = Path.Combine(dir, "peaks.bin");
        if (IsFresh(dir, audio)) return await File.ReadAllBytesAsync(file, ct);

        var psi = new ProcessStartInfo(FfmpegProcess.Locate()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", audio, "-vn", "-ac", "1", "-ar", PeakRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(ct);
        var peaks = new List<byte>();
        var buf = new byte[PeakRate / PeaksPerSecond * 2 * 64];
        int max = 0, inBin = 0, carry = -1;
        var stream = p.StandardOutput.BaseStream;
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf, ct)) > 0)
            {
                var i = 0;
                if (carry >= 0) { Add((short)(carry | buf[0] << 8)); carry = -1; i = 1; } // sample split across reads
                for (; i + 1 < n; i += 2) Add((short)(buf[i] | buf[i + 1] << 8));
                if (i < n) carry = buf[i];
            }
        }
        catch (OperationCanceledException) { try { p.Kill(); } catch (InvalidOperationException) { } throw; }
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidDataException("No se pudo leer el audio:\n" + await err);
        if (inBin > 0) peaks.Add((byte)(max >> 7));

        var result = peaks.ToArray();
        var tmp = dir + ".tmp";
        Directory.CreateDirectory(tmp);
        await File.WriteAllBytesAsync(Path.Combine(tmp, "peaks.bin"), result, ct);
        Commit(tmp, dir);
        return result;

        void Add(short s)
        {
            max = Math.Max(max, Math.Abs((int)s));
            if (++inBin < PeakRate / PeaksPerSecond) return;
            peaks.Add((byte)Math.Min(255, max >> 7));
            max = inBin = 0;
        }
    }

    /// <summary>A cache folder is valid once complete (marker written last) and newer than its source.</summary>
    private static bool IsFresh(string dir, string source)
    {
        var marker = Path.Combine(dir, ".done");
        return File.Exists(marker) && File.GetLastWriteTimeUtc(marker) >= File.GetLastWriteTimeUtc(source);
    }

    private static void Commit(string tmp, string dir)
    {
        File.WriteAllText(Path.Combine(tmp, ".done"), "");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
