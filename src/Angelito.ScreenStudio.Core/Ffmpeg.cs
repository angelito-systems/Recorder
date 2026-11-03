using System.Diagnostics;
using System.Globalization;

namespace Angelito.ScreenStudio.Core;

/// <summary>
/// FFmpeg runs as a separate long-lived process per job (one per recording stream or export), never per frame.
/// Keeping it out-of-process also keeps the app's license independent of the FFmpeg build that ships next to it.
/// </summary>
public sealed class FfmpegProcess : IDisposable
{
    private readonly Process _p;
    public event Action<string>? StdErr;
    public event Action<string>? StdOut;

    public static string Locate()
    {
        var env = Environment.GetEnvironmentVariable("ANGELITO_FFMPEG");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var p in new[] { Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"), Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") })
            if (File.Exists(p)) return p;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("No se encontró ffmpeg.exe. Colóquelo junto a la aplicación (carpeta ffmpeg\\) o en el PATH.");
    }

    public FfmpegProcess(IEnumerable<string> args, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(Locate())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("-hide_banner");
        foreach (var a in args) psi.ArgumentList.Add(a);
        _p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _p.ErrorDataReceived += (_, e) => { if (e.Data != null) StdErr?.Invoke(e.Data); };
        _p.OutputDataReceived += (_, e) => { if (e.Data != null) StdOut?.Invoke(e.Data); };
    }

    public void Start()
    {
        _p.Start();
        ChildProcessJob.Attach(_p);
        _p.BeginErrorReadLine();
        _p.BeginOutputReadLine();
    }

    public bool HasExited => _p.HasExited;
    public int ExitCode => _p.ExitCode;
    public Task WaitAsync(CancellationToken ct = default) => _p.WaitForExitAsync(ct);

    /// <summary>Asks ffmpeg to finish cleanly ("q"), so the container gets finalized; kills only after the timeout.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (_p.HasExited) return;
        try { await _p.StandardInput.WriteAsync('q'); await _p.StandardInput.FlushAsync(); } catch (IOException) { }
        using var cts = new CancellationTokenSource(timeout);
        try { await _p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { Kill(); }
    }

    /// <summary>stdin, for processes reading raw frames from "pipe:0".</summary>
    public Stream Input => _p.StandardInput.BaseStream;

    /// <summary>Ends a pipe-fed process cleanly: EOF on stdin lets ffmpeg finalize the file ("q" would corrupt the frame stream).</summary>
    public async Task CloseInputAsync(TimeSpan timeout)
    {
        if (_p.HasExited) return;
        try { _p.StandardInput.Close(); } catch (IOException) { }
        using var cts = new CancellationTokenSource(timeout);
        try { await _p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { Kill(); }
    }

    public void Kill() { try { if (!_p.HasExited) _p.Kill(true); } catch (InvalidOperationException) { } }

    /// <summary>Runs to completion. Returns exit code and the last stderr lines (for error messages).</summary>
    public static async Task<(int Code, string Log)> RunAsync(IEnumerable<string> args, Action<double>? progressSeconds = null,
        CancellationToken ct = default, string? workingDir = null)
    {
        using var f = new FfmpegProcess(args, workingDir);
        var tail = new Queue<string>();
        f.StdErr += l => { lock (tail) { tail.Enqueue(l); if (tail.Count > 30) tail.Dequeue(); } };
        f.StdOut += l =>
        {
            // -progress pipe:1 emits "out_time_us=123456"
            if (progressSeconds != null && l.StartsWith("out_time_us=") && long.TryParse(l.AsSpan(12), out var us)) progressSeconds(us / 1e6);
        };
        f.Start();
        try { await f.WaitAsync(ct); }
        catch (OperationCanceledException) { f.Kill(); throw; }
        lock (tail) return (f.ExitCode, string.Join("\n", tail));
    }

    public void Dispose() { Kill(); _p.Dispose(); }

    /// <summary>Duration in seconds via ffmpeg's own probe output (avoids requiring ffprobe).</summary>
    public static async Task<double> ProbeDurationAsync(string file)
    {
        double? result = null;
        using var f = new FfmpegProcess(["-i", file, "-f", "null", "-t", "0", "-"]);
        f.StdErr += line =>
        {
            var i = line.IndexOf("Duration: ", StringComparison.Ordinal);
            if (i >= 0 && line.Length >= i + 21 && TimeSpan.TryParse(line.AsSpan(i + 10, 11), CultureInfo.InvariantCulture, out var d))
                result ??= d.TotalSeconds;
        };
        f.Start();
        await f.WaitAsync();
        return result ?? throw new InvalidDataException($"No se pudo leer la duración de {Path.GetFileName(file)}");
    }
}

/// <summary>An H.264 encoder option plus the filter needed to feed it from ddagrab's GPU frames.</summary>
public sealed record EncoderChoice(string Name, string CaptureFilter, bool Hardware)
{
    public bool Hevc => Name.StartsWith("hevc") || Name == "libx265";

    public string[] QualityArgs(ExportQuality q)
    {
        var (cq, i) = q switch { ExportQuality.Fast => ("28", 0), ExportQuality.High => ("19", 2), ExportQuality.Max => ("16", 3), _ => ("23", 1) };
        return Name switch
        {
            "h264_nvenc" or "hevc_nvenc" => ["-c:v", Name, "-preset", new[] { "p2", "p4", "p6", "p7" }[i], "-rc", "vbr", "-cq", cq, "-b:v", "0"],
            "h264_qsv" or "hevc_qsv" => ["-c:v", Name, "-preset", new[] { "veryfast", "medium", "slower", "veryslow" }[i], "-global_quality", cq],
            "h264_amf" or "hevc_amf" => ["-c:v", Name, "-quality", new[] { "speed", "balanced", "quality", "quality" }[i], "-rc", "cqp", "-qp_i", cq, "-qp_p", cq],
            "libx265" => ["-c:v", Name, "-preset", new[] { "veryfast", "medium", "slow", "slower" }[i], "-crf", cq],
            _ => ["-c:v", "libx264", "-preset", new[] { "veryfast", "medium", "slow", "slower" }[i], "-crf", cq],
        };
    }

    /// <summary>Pixel format for frames coming from system memory (export path).</summary>
    public string CpuPixFmt => Name.EndsWith("_qsv") || Name.EndsWith("_amf") ? "nv12" : "yuv420p";
}

/// <summary>Detects what actually works on this machine by encoding a few frames — driver presence alone is not proof.</summary>
public static class EncoderProbe
{
    public static readonly EncoderChoice Cpu = new("libx264", "hwdownload,format=bgra,format=yuv420p", false);
    public static readonly EncoderChoice CpuHevc = new("libx265", "", false);

    // Order matters: zero-copy GPU paths first, then hardware encoders fed from system memory, then CPU.
    private static readonly EncoderChoice[] CaptureCandidates =
    [
        new("h264_nvenc", "", true),
        new("h264_qsv", "hwmap=derive_device=qsv,format=qsv", true),
        new("h264_amf", "", true),
        new("h264_nvenc", "hwdownload,format=bgra", true),
        new("h264_amf", "hwdownload,format=bgra", true),
        Cpu,
    ];

    public static async Task<EncoderChoice> ForCaptureAsync(int outputIdx)
    {
        foreach (var c in CaptureCandidates)
        {
            var args = new List<string> { "-loglevel", "error", "-f", "lavfi", "-i", $"ddagrab=output_idx={outputIdx}:framerate=30", "-t", "0.3" };
            if (c.CaptureFilter != "") args.AddRange(["-vf", c.CaptureFilter]);
            args.AddRange(c.QualityArgs(ExportQuality.Fast));
            args.AddRange(["-f", "null", "-"]);
            if (await Works(args)) return c;
        }
        return Cpu;
    }

    public static async Task<EncoderChoice> ForExportAsync(bool hevc = false)
    {
        foreach (var name in hevc ? new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf" } : new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            var c = new EncoderChoice(name, "", true);
            if (await Works(["-loglevel", "error", "-f", "lavfi", "-i", "color=s=640x360:d=0.2", "-pix_fmt", c.CpuPixFmt, .. c.QualityArgs(ExportQuality.Fast), "-f", "null", "-"]))
                return c;
        }
        return hevc ? CpuHevc : Cpu;
    }

    private static async Task<bool> Works(IEnumerable<string> args)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { return (await FfmpegProcess.RunAsync(args, ct: cts.Token)).Code == 0; }
        catch (OperationCanceledException) { return false; }
    }
}

/// <summary>
/// A Windows job object with kill-on-close: every ffmpeg we start is assigned to it, so if this app crashes or is killed,
/// the OS ends the children too instead of leaving a capture running forever. Partial MKVs stay recoverable.
/// </summary>
internal static class ChildProcessJob
{
    private static readonly IntPtr Job = Create();

    public static void Attach(Process p)
    {
        if (Job != IntPtr.Zero) AssignProcessToJobObject(Job, p.Handle);
    }

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = new() { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        var size = System.Runtime.InteropServices.Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(info, ptr, false);
            return SetInformationJobObject(job, 9 /*ExtendedLimitInformation*/, ptr, (uint)size) ? job : IntPtr.Zero;
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}