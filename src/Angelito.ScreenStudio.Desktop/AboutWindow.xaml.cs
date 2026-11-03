using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Navigation;
using Angelito.ScreenStudio.Core;
using Angelito.ScreenStudio.Desktop.Capture;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>Version, credits and a copyable diagnostics report (hardware, FFmpeg, encoders, folders — no user content).</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Versión {App.Version}";
        Loaded += async (_, _) => Diag.Text = await Task.Run(BuildReportAsync);
        Diag.Text = "Recopilando información…";
    }

    private static async Task<string> BuildReportAsync()
    {
        var sb = new StringBuilder();
        void L(string k, object? v) => sb.AppendLine($"{k,-22}{v}");
        L("Versión", App.Version);
        L("Windows", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        L(".NET", RuntimeInformation.FrameworkDescription);
        L("Procesador", Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString()?.Trim());
        L("Memoria", $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1L << 30)} GB");
        foreach (var gpu in Gpus()) L("GPU", gpu);
        try { foreach (var m in await Devices.MonitorsAsync()) L("Pantalla", $"{m} en ({m.X}, {m.Y})"); }
        catch (Exception e) { L("Pantalla", "no disponible: " + e.Message); }

        try
        {
            var ffmpeg = FfmpegProcess.Locate();
            L("FFmpeg", ffmpeg);
            L("FFmpeg versión", await FirstLineAsync(ffmpeg, "-version"));
            L("Codificador export.", (await EncoderProbe.ForExportAsync()).Name);
            L("Codificador HEVC", (await EncoderProbe.ForExportAsync(ExportFormat.Mp4Hevc)).Name);
            L("Codificador AV1", (await EncoderProbe.ForExportAsync(ExportFormat.Mp4Av1)).Name);
            L("Cámara (zoom)", await EncoderProbe.CameraGpuAsync() is var vk and >= 0 ? $"GPU, Vulkan {vk} (libplacebo)" : "CPU (perspective)");
            L("Codificador AV1", (await EncoderProbe.ForExportAsync(ExportFormat.Mp4Av1)).Name);
        }
        catch (FileNotFoundException e) { L("FFmpeg", "NO ENCONTRADO — " + e.Message); }

        L("Grabaciones", App.RecordingsDir);
        L("Datos y registros", App.DataDir);
        try { L("Espacio libre", $"{new DriveInfo(Path.GetPathRoot(App.RecordingsDir)!).AvailableFreeSpace / (1L << 30)} GB"); } catch (IOException) { }
        var log = Path.Combine(App.DataDir, "incidents.log");
        L("Incidentes", File.Exists(log) ? $"{File.ReadLines(log).Count(l => l.StartsWith('['))} registrados" : "ninguno");
        var versions = Path.Combine(App.DataDir, "versions.log");
        if (File.Exists(versions)) { sb.AppendLine(); sb.AppendLine("Historial de versiones en este equipo:"); foreach (var v in File.ReadLines(versions)) sb.AppendLine("  " + v); }
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID, DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);

    /// <summary>Display adapters (each listed once), without needing WMI.</summary>
    private static List<string> Gpus()
    {
        var names = new List<string>();
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if (!names.Contains(dd.DeviceString)) names.Add(dd.DeviceString);
        }
        return names;
    }

    private static async Task<string> FirstLineAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var line = await p.StandardOutput.ReadLineAsync();
        await p.WaitForExitAsync();
        return line ?? "";
    }

    private void OnCopy(object sender, RoutedEventArgs e) => Clipboard.SetText(Diag.Text);

    private void OnOpenLogs(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.DataDir}\"") { UseShellExecute = true });

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    public static void ShowFor(Window owner) => new AboutWindow { Owner = owner }.ShowDialog();
}
