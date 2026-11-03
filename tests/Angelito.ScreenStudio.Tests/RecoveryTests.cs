using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Tests;

/// <summary>Recovers a session left behind by a crash: killed ffmpeg, half-flushed audio, torn log lines.</summary>
public class RecoveryTests
{
    private static async Task<string> CrashedSession()
    {
        var root = Directory.CreateTempSubdirectory("ass-recover").FullName;
        var dir = Path.Combine(root, "2026-11-03_10-00-00");
        Directory.CreateDirectory(dir);

        // Part 0: real-time capture killed mid-write (what a crash does to ffmpeg) → MKV without duration/cues.
        using (var f = new FfmpegProcess(["-y", "-re", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=30", "-c:v", "libx264", "-g", "60", .. SessionFinalizer.CrashSafeMkvArgs, "screen_0.mkv"], dir))
        {
            f.Start();
            await Task.Delay(3000);
            f.Kill();
            await f.WaitAsync();
        }
        // Part 1: finished normally (the user had paused and resumed).
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-c:v", "libx264", "screen_1.mkv"], workingDir: dir)).Code);

        // Mic: 10 s of samples on disk, but the header was last flushed at 6 s.
        var wav = Path.Combine(dir, "mic.raw.wav");
        Assert.Equal(0, (await FfmpegProcess.RunAsync(["-y", "-f", "lavfi", "-i", "sine=f=300:d=10:r=48000", "-c:a", "pcm_s16le", wav])).Code);
        var bytes = File.ReadAllBytes(wav);
        var dataAt = IndexOf(bytes, "data"u8.ToArray());
        var dataSize = 6 * 48000 * 2;
        BitConverter.GetBytes(dataSize).CopyTo(bytes, dataAt + 4);
        BitConverter.GetBytes(dataAt + 8 + dataSize - 8).CopyTo(bytes, 4);
        File.WriteAllBytes(wav, bytes);

        File.WriteAllText(Path.Combine(dir, "cursor.raw.txt"), "1.0000 10 20 1\n6.0000 30 40 2\n6.5000 5"); // last line torn by the crash

        new SessionManifest
        {
            Width = 320, Height = 180, Fps = 30, Complete = false,
            Recording = new RecordingJournal
            {
                Mic = true, MicOffset = 0.1,
                Parts = [new() { Screen = "screen_0.mkv", ScreenStart = 0.2 }, new() { Screen = "screen_1.mkv", ScreenStart = 5.0, Stop = 7.0 }],
            },
        }.Save(dir);
        return dir;
    }

    [Fact]
    public async Task InterruptedSessionIsFoundAndRecovered()
    {
        var dir = await CrashedSession();
        var root = Path.GetDirectoryName(dir)!;
        Assert.Equal([dir], SessionFinalizer.FindInterrupted(root));

        var m = SessionManifest.Load(dir);
        await SessionFinalizer.FinalizeAsync(dir, m, EncoderProbe.Cpu);

        var back = SessionManifest.Load(dir);
        Assert.True(back.Complete);
        Assert.Null(back.Recording);
        Assert.Empty(SessionFinalizer.FindInterrupted(root));

        var video = await FfmpegProcess.ProbeDurationAsync(Path.Combine(dir, "screen.mp4"));
        Assert.InRange(video, 3.0, 5.5);                       // ~1–3 s salvaged from the killed part + 2 s
        Assert.True(File.Exists(Path.Combine(dir, "mic.wav")));
        var cursor = File.ReadAllLines(Path.Combine(dir, "cursor.txt"));
        Assert.Equal(2, cursor.Length);                         // torn line dropped, both clicks kept
        Assert.False(File.Exists(Path.Combine(dir, "screen_0.mkv")));
        Assert.False(File.Exists(Path.Combine(dir, "mic.raw.wav")));
    }

    [Fact]
    public async Task LiveRecordingIsNotOfferedForRecovery()
    {
        var dir = await CrashedSession();
        using (SessionFinalizer.AcquireLock(dir))
            Assert.Empty(SessionFinalizer.FindInterrupted(Path.GetDirectoryName(dir)!));
        Assert.Single(SessionFinalizer.FindInterrupted(Path.GetDirectoryName(dir)!)); // released with the handle
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (var i = 0; i <= hay.Length - needle.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
