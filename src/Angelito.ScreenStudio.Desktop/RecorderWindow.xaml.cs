using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Angelito.ScreenStudio.Core;
using Angelito.ScreenStudio.Desktop.Capture;
using Microsoft.Win32;

namespace Angelito.ScreenStudio.Desktop;

public partial class RecorderWindow : Window
{
    private const string NoCam = "Sin cámara";
    private Recorder? _rec;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<int, Task<EncoderChoice>> _screenEnc = new();
    private Task<EncoderChoice>? _cpuEnc;

    public RecorderWindow()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => UpdateMeters();
        // Keep the recorder visible to the user but out of the captured video.
        SourceInitialized += (_, _) => Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        Loaded += async (_, _) => await DetectAsync();
    }

    private async Task DetectAsync()
    {
        try
        {
            var monitors = Devices.MonitorsAsync();
            var cams = Devices.WebcamsAsync();
            _cpuEnc = EncoderProbe.ForExportAsync();
            MonitorBox.ItemsSource = await monitors;
            MonitorBox.SelectedIndex = 0;
            CamBox.ItemsSource = new[] { NoCam }.Concat(await cams).ToList();
            CamBox.SelectedIndex = 0;
            if (MonitorBox.Items.Count == 0) { Status.Text = "No se encontró ninguna pantalla que se pueda capturar."; return; }
            var enc = await ScreenEncoder((MonitorInfo)MonitorBox.SelectedItem);
            Status.Text = $"Codificador: {enc.Name}{(enc.Hardware ? " (GPU)" : " (CPU)")} · guardado en {App.RecordingsDir}";
            RecordBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
            App.Log(ex.ToString());
        }
    }

    private Task<EncoderChoice> ScreenEncoder(MonitorInfo m)
    {
        if (!_screenEnc.TryGetValue(m.OutputIdx, out var t)) _screenEnc[m.OutputIdx] = t = EncoderProbe.ForCaptureAsync(m.OutputIdx);
        return t;
    }

    private async void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_rec != null) { await StopAsync(); return; }
        try
        {
            var mon = (MonitorInfo)MonitorBox.SelectedItem;
            var cam = CamBox.SelectedItem as string;
            var dir = Path.Combine(App.RecordingsDir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            _rec = new Recorder(new RecordOptions(mon, int.Parse((string)((ComboBoxItem)FpsBox.SelectedItem).Tag), cam == NoCam ? null : cam,
                MicBox.IsChecked == true, SysBox.IsChecked == true, await ScreenEncoder(mon), await _cpuEnc!, dir));
            _rec.Warning += msg => Dispatcher.BeginInvoke(() => { App.Log(msg); Status.Text = msg; });
            _rec.Start();
            SetRecording(true);
        }
        catch (Exception ex)
        {
            App.Log(ex.ToString());
            if (_rec != null) { try { await _rec.StopAsync(); } catch (Exception inner) { App.Log(inner.ToString()); } }
            _rec = null;
            SetRecording(false);
            MessageBox.Show(this, "No se pudo iniciar la grabación.\n\n" + ex.Message, App.Product, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StopAsync()
    {
        RecordBtn.IsEnabled = false;
        RecordBtn.Content = "Finalizando…";
        _tick.Stop();
        try
        {
            var dir = await _rec!.StopAsync();
            var m = SessionManifest.Load(dir);
            var dur = await FfmpegProcess.ProbeDurationAsync(Path.Combine(dir, m.Screen));
            var project = StudioProject.FromSession(dur, m.LoadCursor(dir), m.Width, m.Height);
            var path = Path.Combine(dir, "proyecto" + StudioProject.Extension);
            project.Save(path);
            new EditorWindow(path).Show();
        }
        catch (Exception ex)
        {
            App.Log(ex.ToString());
            MessageBox.Show(this, "La grabación terminó con errores; los archivos parciales se conservaron.\n\n" + ex.Message, App.Product);
        }
        _rec = null;
        SetRecording(false);
    }

    private void SetRecording(bool on)
    {
        RecordBtn.IsEnabled = true;
        RecordBtn.Content = on ? "■ Detener" : "● Grabar";
        Timer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MonitorBox.IsEnabled = CamBox.IsEnabled = FpsBox.IsEnabled = MicBox.IsEnabled = SysBox.IsEnabled = !on;
        if (on) _tick.Start(); else { _tick.Stop(); MicMeter.Value = SysMeter.Value = 0; }
    }

    private void UpdateMeters()
    {
        if (_rec == null) return;
        Timer.Text = TimeSpan.FromSeconds(_rec.Elapsed).ToString(@"hh\:mm\:ss");
        MicMeter.Value = _rec.MicPeak;
        SysMeter.Value = _rec.SystemPeak;
        MicMeter.Foreground = _rec.MicPeak > 0.98f ? (System.Windows.Media.Brush)FindResource("Rec") : (System.Windows.Media.Brush)FindResource("Brand");
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = $"Proyecto Angelito Studio|*{StudioProject.Extension}", InitialDirectory = App.RecordingsDir };
        if (dlg.ShowDialog(this) == true) new EditorWindow(dlg.FileName).Show();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_rec != null && MessageBox.Show(this, "Hay una grabación en curso. ¿Detenerla y salir?", App.Product, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            e.Cancel = true;
        else if (_rec != null) { e.Cancel = true; _ = StopAndClose(); }
        base.OnClosing(e);
    }

    private async Task StopAndClose() { await _rec!.StopAsync(); _rec = null; Close(); }
}
