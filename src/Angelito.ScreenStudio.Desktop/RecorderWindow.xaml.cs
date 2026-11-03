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
    private Region? _region;
    private const string WholeScreen = "Pantalla completa o región";
    private CameraPreview? _camPreview;
    private NAudio.CoreAudioApi.WasapiCapture? _micProbe; // open only to light the mic meter before recording
    private NAudio.CoreAudioApi.MMDevice? _spkProbe;
    private float _micProbePeak;
    private System.Windows.Media.Animation.Storyboard? _pulse;

    public RecorderWindow()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => UpdateMeters();
        // Keep the recorder visible to the user but out of the captured video.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // ANGELITO_SHOW_IN_CAPTURE=1 keeps it visible to screenshots (docs, support); recordings would then include it.
            if (Environment.GetEnvironmentVariable("ANGELITO_SHOW_IN_CAPTURE") != "1")
                Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            // Global hotkeys (Ctrl+Shift+F9 pause/resume, Ctrl+Shift+F10 record/stop); freed by Windows when the window is destroyed.
            const uint CtrlShiftNoRepeat = 0x0002 | 0x0004 | 0x4000;
            if (!Native.RegisterHotKey(hwnd, 1, CtrlShiftNoRepeat, 0x78) | !Native.RegisterHotKey(hwnd, 2, CtrlShiftNoRepeat, 0x79))
                App.Log("global hotkey Ctrl+Shift+F9/F10 already taken by another app");
            HwndSource.FromHwnd(hwnd).AddHook((IntPtr _, int msg, IntPtr id, IntPtr _, ref bool handled) =>
            {
                if (msg != 0x0312) return IntPtr.Zero; // WM_HOTKEY
                handled = true;
                if (id == 1 && _rec != null && PauseBtn.IsEnabled) OnPause(this, new RoutedEventArgs());
                if (id == 2 && RecordBtn.IsEnabled) OnRecord(this, new RoutedEventArgs());
                return IntPtr.Zero;
            });
        };
        Loaded += async (_, _) => { await RecoverInterruptedAsync(); await DetectAsync(); };
    }

    private async Task DetectAsync()
    {
        try
        {
            var monitors = Devices.MonitorsAsync();
            var cams = Devices.WebcamsAsync();
            _cpuEnc = EncoderProbe.ForExportAsync();
            MicDevBox.ItemsSource = Devices.Audio(capture: true);
            SpkDevBox.ItemsSource = Devices.Audio(capture: false);
            MicDevBox.SelectedIndex = SpkDevBox.SelectedIndex = 0;
            MonitorBox.ItemsSource = await monitors;
            MonitorBox.SelectedIndex = 0;
            MonitorBox.SelectionChanged += (_, _) => OnFullScreen(this, new RoutedEventArgs());
            OnWindowListOpen(this, EventArgs.Empty);
            CamBox.ItemsSource = new[] { NoCam }.Concat(await cams).ToList();
            CamBox.SelectedIndex = 0;
            if (MonitorBox.Items.Count == 0) { Status.Text = "No se encontró ninguna pantalla que se pueda capturar."; return; }
            OnFullScreen(this, new RoutedEventArgs());
            _tick.Start(); // meters run before recording too, so levels can be checked first
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

    /// <summary>Editable project for a finished session folder (kept if one already exists, e.g. after a recovery).</summary>
    private static async Task<string> CreateProjectAsync(string dir)
    {
        var path = Path.Combine(dir, "proyecto" + StudioProject.Extension);
        if (File.Exists(path)) return path;
        var m = SessionManifest.Load(dir);
        var dur = await FfmpegProcess.ProbeDurationAsync(Path.Combine(dir, m.Screen));
        StudioProject.FromSession(dur, m.LoadCursor(dir), m.Width, m.Height).Save(path);
        return path;
    }

    /// <summary>Offers to finish recordings left behind by a crash or power cut; their files are never deleted on failure.</summary>
    private async Task RecoverInterruptedAsync()
    {
        var found = SessionFinalizer.FindInterrupted(App.RecordingsDir);
        if (found.Count == 0) return;
        var names = string.Join("\n", found.Select(d => "• " + Path.GetFileName(d)));
        if (MessageBox.Show(this, $"Se encontraron grabaciones que no se cerraron correctamente (por ejemplo, por un cierre inesperado o un corte de energía):\n\n{names}\n\n¿Recuperarlas ahora?",
                App.Product, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        Status.Text = "Recuperando grabaciones…";
        var enc = await EncoderProbe.ForExportAsync();
        string? last = null;
        var errors = new List<string>();
        foreach (var dir in found)
        {
            try
            {
                await SessionFinalizer.FinalizeAsync(dir, SessionManifest.Load(dir), enc, w => App.Log($"recover {dir}: {w}"));
                last = await CreateProjectAsync(dir);
            }
            catch (Exception ex)
            {
                App.Log($"recover {dir}: {ex}");
                errors.Add($"{Path.GetFileName(dir)}: {ex.Message.Split('\n')[0]}");
            }
        }
        var ok = found.Count - errors.Count;
        MessageBox.Show(this, $"Recuperadas: {ok} de {found.Count}." + (errors.Count > 0 ? "\n\nNo se pudieron recuperar (sus archivos se conservaron):\n" + string.Join("\n", errors) : ""),
            App.Product, MessageBoxButton.OK, errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        if (last != null) new EditorWindow(last).Show();
    }

    private Task<EncoderChoice> ScreenEncoder(MonitorInfo m)
    {
        if (!_screenEnc.TryGetValue(m.OutputIdx, out var t)) _screenEnc[m.OutputIdx] = t = EncoderProbe.ForCaptureAsync(m.OutputIdx);
        return t;
    }

    private async void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_rec != null) { await StopAsync(); return; }
        if (MonitorBox.SelectedItem is not MonitorInfo countdownOn) return;
        RecordBtn.IsEnabled = false;
        var go = await CountdownWindow.RunAsync(countdownOn);
        RecordBtn.IsEnabled = true;
        if (!go) return;
        await StopPreviewsAsync(); // a dshow camera serves one reader at a time
        try
        {
            var mon = (MonitorInfo)MonitorBox.SelectedItem;
            var cam = CamBox.SelectedItem as string;
            var dir = Path.Combine(App.RecordingsDir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            _rec = new Recorder(new RecordOptions(mon, _region, int.Parse((string)((ComboBoxItem)FpsBox.SelectedItem).Tag), cam == NoCam ? null : cam,
                MicBox.IsChecked == true, SysBox.IsChecked == true, await ScreenEncoder(mon), await _cpuEnc!, dir, KeyFilterOrNull(), WindowBox.SelectedItem as WindowInfo,
                (MicDevBox.SelectedItem as AudioDevice)?.Id, (SpkDevBox.SelectedItem as AudioDevice)?.Id));
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
            StartPreviews();
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
            new EditorWindow(await CreateProjectAsync(dir)).Show();
        }
        catch (Exception ex)
        {
            App.Log(ex.ToString());
            MessageBox.Show(this, "La grabación terminó con errores; los archivos parciales se conservaron.\n\n" + ex.Message, App.Product);
        }
        _rec = null;
        SetRecording(false);
        StartPreviews();
    }

    private void SetRecording(bool on)
    {
        RecordBtn.IsEnabled = true;
        RecordBtn.Content = on ? "Detener" : "Grabar";
        _pulse ??= PulseStoryboard();
        if (on) _pulse.Begin(this, true); else { _pulse.Stop(this); Pulse.Opacity = 0; }
        Timer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MonitorBox.IsEnabled = CamBox.IsEnabled = FpsBox.IsEnabled = MicBox.IsEnabled = SysBox.IsEnabled = KeysBox.IsEnabled = WindowBox.IsEnabled = RegionBtn.IsEnabled = FullBtn.IsEnabled = OpenBtn.IsEnabled
            = MicDevBox.IsEnabled = SpkDevBox.IsEnabled = !on;
        PauseBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PauseBtn.IsEnabled = true;
        PauseBtn.Content = "❚❚ Pausar";
        if (!on) OnWindowPick(this, null!); // a chosen window keeps the monitor/region controls disabled
        if (!on) MicMeter.Value = SysMeter.Value = 0;
    }

    private void UpdateMeters()
    {
        if (_rec == null)
        {
            MicMeter.Value = MicBox.IsChecked == true ? _micProbePeak : 0;
            try { SysMeter.Value = SysBox.IsChecked == true ? _spkProbe?.AudioMeterInformation.MasterPeakValue ?? 0 : 0; }
            catch (System.Runtime.InteropServices.COMException) { SysMeter.Value = 0; } // device unplugged
            _micProbePeak *= 0.85f; // decay between buffers
            return;
        }
        Timer.Text = TimeSpan.FromSeconds(_rec.Recorded).ToString(@"hh\:mm\:ss") + (_rec.IsPaused ? "  en pausa" : "") + (_rec.KeysSuspended ? "  · atajos suspendidos" : "");
        MicMeter.Value = _rec.MicPeak;
        SysMeter.Value = _rec.SystemPeak;
        MicMeter.Foreground = _rec.MicPeak > 0.98f ? (System.Windows.Media.Brush)FindResource("Rec") : (System.Windows.Media.Brush)FindResource("Brand");
    }

    /// <summary>Allowed-list filter plus the user's additions from keys.json; null when the overlay is off.</summary>
    private KeyFilter? KeyFilterOrNull()
    {
        if (KeysBox.IsChecked != true) return null;
        var s = KeySettings.Load(Path.Combine(App.DataDir, "keys.json"));
        return new KeyFilter(s.ExtraAllowed, s.ExcludedApps);
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        if (_rec == null) return;
        PauseBtn.IsEnabled = RecordBtn.IsEnabled = false;
        try
        {
            if (_rec.IsPaused) { _rec.Resume(); PauseBtn.Content = "❚❚ Pausar"; }
            else { await _rec.PauseAsync(); PauseBtn.Content = "▶ Reanudar"; }
        }
        catch (Exception ex)
        {
            App.Log(ex.ToString());
            Status.Text = "No se pudo pausar/reanudar: " + ex.Message;
        }
        PauseBtn.IsEnabled = RecordBtn.IsEnabled = true;
    }

    /// <summary>Refreshes the window list each time it opens (windows come and go); keeps the current pick if it still exists.</summary>
    private void OnWindowListOpen(object? sender, EventArgs e)
    {
        var current = WindowBox.SelectedItem as WindowInfo;
        var items = new List<object> { WholeScreen };
        items.AddRange(WindowCapture.List());
        WindowBox.ItemsSource = items;
        WindowBox.SelectedItem = items.OfType<WindowInfo>().FirstOrDefault(w => w.Handle == current?.Handle) ?? (object)WholeScreen;
    }

    private void OnWindowPick(object sender, SelectionChangedEventArgs e)
    {
        var window = WindowBox.SelectedItem is WindowInfo;
        AreaRow.IsEnabled = MonitorBox.IsEnabled = !window;
        AreaText.Text = window ? "Se graba solo la ventana elegida" : _region == null ? "Pantalla completa" : AreaText.Text;
    }

    private void OnRegion(object sender, RoutedEventArgs e)
    {
        if (MonitorBox.SelectedItem is not MonitorInfo mon) return;
        var w = new RegionWindow(mon);
        if (w.ShowDialog() != true || w.Result is not { } r) return;
        _region = r;
        AreaText.Text = $"Región {r.Width & ~1}×{r.Height & ~1} en ({r.X}, {r.Y})";
        FullBtn.Visibility = Visibility.Visible;
    }

    private void OnFullScreen(object sender, RoutedEventArgs e)
    {
        _region = null;
        AreaText.Text = MonitorBox.SelectedItem is MonitorInfo m ? $"Pantalla completa · {m.Width}×{m.Height}" : "Pantalla completa";
        FullBtn.Visibility = Visibility.Collapsed;
    }

    private void OnAbout(object sender, RoutedEventArgs e) => AboutWindow.ShowFor(this);

    private System.Windows.Media.Animation.Storyboard PulseStoryboard()
    {
        var sb = new System.Windows.Media.Animation.Storyboard { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
        var dur = TimeSpan.FromSeconds(1.4);
        void Add(System.Windows.Media.Animation.DoubleAnimation a, string path)
        {
            System.Windows.Media.Animation.Storyboard.SetTarget(a, Pulse);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(a, new PropertyPath(path));
            sb.Children.Add(a);
        }
        Add(new(0.8, 0, dur), "Opacity");
        Add(new(1, 1.35, dur), "RenderTransform.ScaleX");
        Add(new(1, 1.35, dur), "RenderTransform.ScaleY");
        return sb;
    }

    private void OnCameraPick(object sender, SelectionChangedEventArgs e)
    {
        if (_rec == null && IsLoaded) _ = RestartCameraAsync();
    }

    private void OnAudioPick(object sender, SelectionChangedEventArgs e)
    {
        if (_rec != null || MicDevBox.SelectedItem == null || SpkDevBox.SelectedItem == null) return;
        StopAudioProbes();
        StartAudioProbes();
    }

    private async Task RestartCameraAsync()
    {
        if (_camPreview is { } old) { _camPreview = null; await old.StopAsync(); old.Dispose(); }
        CamImage.Source = null;
        CamOff.Visibility = Visibility.Visible;
        if (CamBox.SelectedItem is not string cam || cam == NoCam) return;
        try
        {
            _camPreview = new CameraPreview(cam);
            CamImage.Source = _camPreview.Bitmap;
            CamOff.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException) { App.Log("camera preview: " + ex.Message); }
    }

    private void StartAudioProbes()
    {
        try
        {
            var mic = MicDevBox.SelectedItem as AudioDevice;
            _micProbe = mic?.Id is { } id ? new NAudio.CoreAudioApi.WasapiCapture(Devices.Endpoint(id)) : new NAudio.CoreAudioApi.WasapiCapture();
            _micProbe.DataAvailable += (_, a) => { var p = Peak(a.Buffer, a.BytesRecorded, _micProbe?.WaveFormat); if (p > _micProbePeak) _micProbePeak = p; };
            _micProbe.StartRecording();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { _micProbe = null; App.Log("mic meter: " + ex.Message); }
        try
        {
            var spk = SpkDevBox.SelectedItem as AudioDevice;
            if (spk?.Id is { } sid) _spkProbe = Devices.Endpoint(sid);
            else { using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator(); _spkProbe = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia); }
        }
        catch (System.Runtime.InteropServices.COMException ex) { _spkProbe = null; App.Log("speaker meter: " + ex.Message); }
    }

    /// <summary>Peak of a WASAPI buffer (32-bit float or 16-bit PCM), 0..1.</summary>
    private static float Peak(byte[] buf, int n, NAudio.Wave.WaveFormat? f)
    {
        float max = 0;
        if (f?.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat || f?.BitsPerSample == 32)
            for (var i = 0; i + 4 <= n; i += 4) max = Math.Max(max, Math.Abs(BitConverter.ToSingle(buf, i)));
        else
            for (var i = 0; i + 2 <= n; i += 2) max = Math.Max(max, Math.Abs(BitConverter.ToInt16(buf, i) / 32768f));
        return Math.Min(1, max);
    }

    private void StopAudioProbes()
    {
        try { _micProbe?.StopRecording(); } catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        _micProbe?.Dispose(); _micProbe = null; _micProbePeak = 0;
        _spkProbe?.Dispose(); _spkProbe = null;
    }

    private void StartPreviews() { StartAudioProbes(); _ = RestartCameraAsync(); }

    private async Task StopPreviewsAsync()
    {
        StopAudioProbes();
        if (_camPreview is { } c) { _camPreview = null; await c.StopAsync(); c.Dispose(); }
        CamImage.Source = null;
        CamOff.Visibility = Visibility.Visible;
    }

    private void OnEditKeys(object sender, RoutedEventArgs e) => KeysWindow.ShowFor(this);

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = $"Proyecto Angelito Studio|*{StudioProject.Extension}", InitialDirectory = App.RecordingsDir };
        if (dlg.ShowDialog(this) == true) new EditorWindow(dlg.FileName).Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAudioProbes();
        _camPreview?.Dispose();
        base.OnClosed(e);
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
