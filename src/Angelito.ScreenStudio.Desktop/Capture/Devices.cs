using System.Text.RegularExpressions;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop.Capture;

public sealed record MonitorInfo(int OutputIdx, int X, int Y, int Width, int Height)
{
    public override string ToString() => $"Pantalla {OutputIdx + 1} — {Width}×{Height}";
}

public static partial class Devices
{
    /// <summary>Monitors that Desktop Duplication (ddagrab) can actually open, with their desktop origin.</summary>
    public static async Task<List<MonitorInfo>> MonitorsAsync()
    {
        var rects = new List<Native.RECT>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfo(h, ref mi)) rects.Add(mi.rcMonitor);
            return true;
        }, IntPtr.Zero);

        var result = new List<MonitorInfo>();
        for (var i = 0; i < 8; i++)
        {
            int w = 0, h = 0;
            using var f = new FfmpegProcess(["-v", "verbose", "-f", "lavfi", "-i", $"ddagrab=output_idx={i}", "-frames:v", "1", "-f", "null", "-"]);
            f.StdErr += l => { var m = DxgiDims().Match(l); if (m.Success) { w = int.Parse(m.Groups[1].Value); h = int.Parse(m.Groups[2].Value); } };
            f.Start();
            await f.WaitAsync();
            if (w == 0) break;
            // shortcut: DXGI outputs are matched to monitors by size in enumeration order; verify on multi-monitor rigs with equal-size screens.
            var r = rects.FirstOrDefault(r => r.Right - r.Left == w && r.Bottom - r.Top == h && !result.Any(m => m.X == r.Left && m.Y == r.Top));
            rects.Remove(r);
            result.Add(new MonitorInfo(i, r.Left, r.Top, w, h));
        }
        return result;
    }

    public static async Task<List<string>> WebcamsAsync()
    {
        var list = new List<string>();
        using var f = new FfmpegProcess(["-list_devices", "true", "-f", "dshow", "-i", "dummy"]);
        f.StdErr += l => { var m = DshowVideo().Match(l); if (m.Success) lock (list) list.Add(m.Groups[1].Value); };
        f.Start();
        await f.WaitAsync();
        return list;
    }

    [GeneratedRegex(@"Opened dxgi output \d+ with dimensions (\d+)x(\d+)")] private static partial Regex DxgiDims();
    [GeneratedRegex("\"(.+)\" \\(video\\)")] private static partial Regex DshowVideo();
}
