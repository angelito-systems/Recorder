using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Angelito.ScreenStudio.Core;
using Microsoft.Win32;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>
/// Editor. The preview is a live WPF composition driven by the same Core math the exporter uses
/// (ZoomMath, Layout); media files are only read, never modified.
/// </summary>
public partial class EditorWindow : Window
{
    private readonly string _path;
    private readonly History _history;
    private SessionManifest _m = null!;
    private string _sessionDir = null!;
    private List<CursorEvent> _cursor = [];
    private List<CursorEvent> _clicks = [];
    private double _duration;
    private bool _refreshing = true; // until the first Refresh, control events fire from InitializeComponent
    private bool _playing, _dirty, _scrubbing, _camDragging;
    private int _playSeg = -1; // clip being played, as an index into P.Segments
    private int _selectedZoom = -1;
    private string? _lastEditKey;
    private DateTime _lastEditAt;
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(5) }; // a crash loses at most ~5 s of edits
    private readonly List<Ellipse> _rings = [];
    private readonly Line _playhead = new() { Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };

    private StudioProject P => _history.Current;
    private double Now => ScreenMedia.Position.TotalSeconds;

    public EditorWindow(string projectPath)
    {
        InitializeComponent();
        _path = projectPath;
        _history = new History(StudioProject.Load(projectPath));
        ProjectName.Text = System.IO.Path.GetFileNameWithoutExtension(projectPath);
        if (!LocateSession()) { Loaded += (_, _) => Close(); return; }

        _m = SessionManifest.Load(_sessionDir);
        _cursor = _m.LoadCursor(_sessionDir);
        _clicks = _cursor.Where(c => c.K != CursorKind.Move).ToList();
        LoadKeys();
        _duration = Math.Max(P.Segments.Max(s => s.End), 0.1);
        _view = TimelineView.Fit(_duration);
        ZoomHost.Width = ScreenMedia.Width = _m.Width;
        ZoomHost.Height = ScreenMedia.Height = _m.Height;

        Open(ScreenMedia, _m.Screen);
        Open(CamMedia, _m.Webcam);
        Open(MicMedia, _m.Mic);
        Open(SysMedia, _m.SystemAudio);
        StartMediaCache();
        CamSection.Visibility = CamFrame.Visibility = _m.Webcam != null ? Visibility.Visible : Visibility.Collapsed;
        MicSection.Visibility = _m.Mic != null ? Visibility.Visible : Visibility.Collapsed;
        SysSection.Visibility = _m.SystemAudio != null ? Visibility.Visible : Visibility.Collapsed;
        if (!_m.Complete) MessageBox.Show(this, "Esta grabación no se cerró correctamente. Se abrirá lo que se pudo recuperar.", App.Product);

        ScreenMedia.MediaOpened += (_, _) =>
        {
            if (ScreenMedia.NaturalDuration.HasTimeSpan) _duration = Math.Max(ScreenMedia.NaturalDuration.TimeSpan.TotalSeconds, P.Segments.Max(s => s.End));
            _view = TimelineView.Fit(_duration);
            Seek(P.Segments[0].Start);
            DrawTimeline();
        };
        CamMedia.MediaOpened += (_, _) => LayoutCam(); // real video aspect is known only now
        ScreenMedia.MediaFailed += (_, e) => MessageBox.Show(this, "No se pudo reproducir el video de pantalla: " + e.ErrorException.Message, App.Product);
        ScreenMedia.MediaEnded += (_, _) => Pause();

        CompositionTarget.Rendering += OnFrame;
        _autosave.Tick += (_, _) => { if (_dirty) Save(); };
        _autosave.Start();
        Refresh();
    }

    /// <summary>Finds the session folder; if media moved, asks the user to point to it and stores the new relative path.</summary>
    private bool LocateSession()
    {
        _sessionDir = P.ResolveSessionDir(_path);
        while (!File.Exists(System.IO.Path.Combine(_sessionDir, SessionManifest.FileName)))
        {
            MessageBox.Show(this, "No se encuentran los medios de este proyecto. Selecciona la carpeta de la grabación.", App.Product);
            var dlg = new OpenFolderDialog { Title = "Carpeta de la grabación" };
            if (dlg.ShowDialog() != true) return false;
            _sessionDir = dlg.FolderName;
            _history.Push(P with { SessionDir = System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path))!, _sessionDir) });
            _dirty = true;
        }
        return true;
    }

    private void Open(MediaElement el, string? file)
    {
        if (file == null) return;
        var full = System.IO.Path.Combine(_sessionDir, file);
        if (!File.Exists(full)) return;
        el.Source = new Uri(full);
        el.Play();
        el.Pause();
    }

    // ---------- state ----------

    /// <summary>Applies an edit. Consecutive edits from the same control within a second become one undo step.</summary>
    private void Commit(StudioProject next, string key)
    {
        if (_refreshing) return;
        var coalesce = key == _lastEditKey && (key.StartsWith("drag") || (DateTime.Now - _lastEditAt).TotalSeconds < 1);
        if (coalesce) _history.Replace(next); else _history.Push(next);
        _lastEditKey = key;
        _lastEditAt = DateTime.Now;
        _dirty = true;
        Refresh();
    }

    /// <summary>Pushes project values into the controls and redraws. Guarded so control events don't echo back as edits.</summary>
    private void Refresh()
    {
        _refreshing = true;
        var p = P;
        RefreshCanvas();
        if (_selectedZoom >= p.Zooms.Count) _selectedZoom = -1;
        ZoomPanel.IsEnabled = DelZoomBtn.IsEnabled = _selectedZoom >= 0;
        if (_selectedZoom >= 0)
        {
            var z = p.Zooms[_selectedZoom];
            ZoomScale.Value = z.Scale;
            ZoomRamp.Value = z.Ramp;
            ZoomInfo.Text = ZoomSummary(z, _selectedZoom);
        }
        else ZoomInfo.Text = p.Zooms.Count == 0 ? "Sin zooms. Activa el zoom automático o usa Añadir." : "Selecciona un zoom en la línea de tiempo.";
        var az = p.AutoZoom;
        AzEnabled.IsChecked = az.Enabled;
        AzFollow.IsChecked = az.Follow;
        AzFollow.IsEnabled = AzScale.IsEnabled = AzIdle.IsEnabled = AzHold.IsEnabled = AzIn.IsEnabled = AzOut.IsEnabled = az.Enabled;
        (AzScale.Value, AzIdle.Value, AzHold.Value, AzIn.Value, AzOut.Value) = (az.Scale, az.IdleReturn, az.MinHold, az.ZoomIn, az.ZoomOut);
        AzScaleText.Text = $"Zoom por clic: {az.Scale:0.0#}×";
        AzIdleText.Text = $"Volver al 100 % tras {az.IdleReturn:0.0} s sin clics";
        AzHoldText.Text = $"Permanencia mínima: {az.MinHold:0.0} s";
        AzInText.Text = $"Acercamiento: {az.ZoomIn * 1000:0} ms";
        AzOutText.Text = $"Alejamiento: {az.ZoomOut * 1000:0} ms";

        ShowClicks.IsChecked = p.Cursor.ShowClicks;
        RingSize.Value = p.Cursor.Size;
        RingDuration.Value = p.Cursor.Duration;

        CamVisible.IsChecked = p.Webcam.Visible;
        CamMirror.IsChecked = p.Webcam.Mirror;
        CamShape.SelectedIndex = (int)p.Webcam.Shape;
        CamSize.Value = p.Webcam.Size;
        LayoutCam();

        MicMute.IsChecked = p.Audio.MicMuted;
        SysMute.IsChecked = p.Audio.SystemMuted;
        MicGain.Value = p.Audio.MicGainDb;
        SysGain.Value = p.Audio.SystemGainDb;
        MicSync.Value = p.Audio.MicSync;
        SysSync.Value = p.Audio.SystemSync;
        MicLabel.Text = $"Micrófono  {p.Audio.MicGainDb:+0;-0;0} dB";
        SysLabel.Text = $"Audio del sistema  {p.Audio.SystemGainDb:+0;-0;0} dB";
        MicSyncLabel.Text = $"Desfase {p.Audio.MicSync * 1000:+0;-0;0} ms";
        SysSyncLabel.Text = $"Desfase {p.Audio.SystemSync * 1000:+0;-0;0} ms";
        MicMedia.Volume = p.Audio.MicMuted ? 0 : Math.Min(1, Math.Pow(10, p.Audio.MicGainDb / 20));
        SysMedia.Volume = p.Audio.SystemMuted ? 0 : Math.Min(1, Math.Pow(10, p.Audio.SystemGainDb / 20));

        ExportHeight.SelectedIndex = Math.Max(0, ExportHeight.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == p.Export.Height.ToString()));
        ExportQualityBox.SelectedIndex = (int)p.Export.Quality;
        ExportFormatBox.SelectedIndex = (int)p.Export.Format;
        ExportFpsBox.SelectedIndex = Math.Max(0, ExportFpsBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == p.Export.Fps.ToString()));
        PresetBox.SelectedIndex = 0;
        var (ow, oh) = Layout.OutputSize(_m, p.Export, p.Canvas);
        var size = Exporter.EstimateBytes(p, _m) / 1048576.0;
        ExportInfo.Text = p.Export.Format switch
        {
            ExportFormat.Wav => $"Audio 48 kHz estéreo · {Fmt(p.OutputDuration)} · ≈{size:0.#} MB",
            ExportFormat.Png => $"{ow}×{oh} · fotograma bajo el cabezal",
            _ => $"{ow}×{oh} · {Exporter.Fps(p, _m)} FPS · {Fmt(p.OutputDuration)} · ≈{size:0.#} MB (aprox.)",
        };

        UndoBtn.IsEnabled = _history.CanUndo;
        RedoBtn.IsEnabled = _history.CanRedo;
        RefreshText();
        RefreshAnnotations();
        RefreshKeys();
        ApplyMode();
        Title = $"{(_dirty ? "• " : "")}{App.Product} — Editor";
        _refreshing = false;
        DrawTimeline();
        ApplyVisuals(Now);
    }

    private void LayoutCam()
    {
        var (x, y, d) = Layout.WebcamRect((int)SW, (int)SH, P.Webcam);
        var r = Layout.CornerRadius(P.Webcam.Shape, d);
        CamFrame.Margin = new Thickness(x, y, 0, 0);
        CamFrame.Width = CamFrame.Height = d;
        CamFrame.Clip = new RectangleGeometry(new Rect(0, 0, d, d), r, r);
        // Center-crop like the exporter (scale to cover, crop the middle), with explicit sizes: Stretch=UniformToFill plus a
        // mirror transform drew the video shifted by half its overflow.
        var aspect = CamMedia.NaturalVideoHeight > 0 ? CamMedia.NaturalVideoWidth / (double)CamMedia.NaturalVideoHeight : 16 / 9.0;
        double mw = aspect >= 1 ? d * aspect : d, mh = aspect >= 1 ? d : d / aspect;
        CamMedia.Width = mw;
        CamMedia.Height = mh;
        CamMedia.Margin = new Thickness((d - mw) / 2, (d - mh) / 2, 0, 0);
        CamMedia.RenderTransform = new ScaleTransform(P.Webcam.Mirror ? -1 : 1, 1, mw / 2, 0);
        CamFrame.Opacity = P.Webcam.Visible ? 1 : 0.25; // faded, not hidden, so it can still be re-enabled and dragged
    }

    // ---------- playback ----------

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_m == null) return;
        var t = Now;
        if (_playing)
        {
            // Clips play in list (output) order, which may differ from source order.
            var next = P.PlaybackSegment(_playSeg, t);
            if (next < 0) Pause();
            else if (next != _playSeg || t < P.Segments[next].Start - 0.05)
            {
                var s = P.Segments[next];
                if (t < s.Start - 0.02 || t >= s.End) Seek(s.Start, keepPlaying: true);
                t = Now;
            }
            _playSeg = next;
            Sync(t, false);
        }
        ApplyVisuals(t);
    }

    private void ApplyVisuals(double t)
    {
        if (_m == null) return;
        var (vx, vy, vw, _) = ZoomMath.ViewRect(P.Zooms, t, _m.Width, _m.Height, P.Canvas);
        var s = SW / vw; // stage = canvas window in source-pixel units
        ZoomTransform.Matrix = new Matrix(s, 0, 0, s, -vx * s, -vy * s);

        var active = P.Cursor.ShowClicks ? _clicks.Where(c => t >= c.T && t <= c.T + P.Cursor.Duration).ToList() : [];
        while (_rings.Count < active.Count)
        {
            var el = new Ellipse();
            _rings.Add(el);
            RingLayer.Children.Add(el);
        }
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(P.Cursor.Color));
        var size = Layout.Even(P.Cursor.Size);
        for (var i = 0; i < _rings.Count; i++)
        {
            var el = _rings[i];
            el.Visibility = i < active.Count ? Visibility.Visible : Visibility.Collapsed;
            if (i >= active.Count) continue;
            el.Width = el.Height = size;
            el.Stroke = brush;
            el.StrokeThickness = Math.Max(3, size / 10);
            el.Opacity = 0.9;
            Canvas.SetLeft(el, active[i].X - size / 2);
            Canvas.SetTop(el, active[i].Y - size / 2);
        }

        ApplyText(t);
        ApplyAnnotations(t);
        ApplyKeys(t);
        TimeText.Text = $"{Fmt(t)} / {Fmt(_duration)}";
        var w = Timeline.ActualWidth;
        // Follow only while playing, so scrolling away by hand doesn't snap back.
        if (_playing && _drag == null && !_scrubbing && _view.Follow(t, _duration) is var f && f != _view) { _view = f; DrawTimeline(); }
        _playhead.X1 = _playhead.X2 = _view.ToX(t, w);
        _playhead.Visibility = t >= _view.Start && t <= _view.End ? Visibility.Visible : Visibility.Hidden;
        _playhead.Y1 = 0;
        _playhead.Y2 = Timeline.ActualHeight;
    }

    /// <summary>Keeps webcam and audio players aligned to the screen video (the master clock).</summary>
    private void Sync(double t, bool force)
    {
        Slave(CamMedia, _m.Webcam, t - (_m.WebcamOffset - _m.ScreenOffset), force);
        Slave(MicMedia, _m.Mic, t - (_m.MicOffset - _m.ScreenOffset + P.Audio.MicSync), force);
        Slave(SysMedia, _m.SystemAudio, t - (_m.SystemAudioOffset - _m.ScreenOffset + P.Audio.SystemSync), force);
    }

    private readonly Dictionary<MediaElement, bool> _slavePlaying = new();

    private void Slave(MediaElement el, string? file, double st, bool force)
    {
        if (file == null || el.Source == null) return;
        var len = el.NaturalDuration.HasTimeSpan ? el.NaturalDuration.TimeSpan.TotalSeconds : double.MaxValue;
        var shouldPlay = _playing && st >= 0 && st < len;
        _slavePlaying.TryGetValue(el, out var isPlaying);
        if (force || Math.Abs(el.Position.TotalSeconds - Math.Clamp(st, 0, len)) > 0.15)
            el.Position = TimeSpan.FromSeconds(Math.Clamp(st, 0, len));
        if (shouldPlay != isPlaying)
        {
            if (shouldPlay) el.Play(); else el.Pause();
            _slavePlaying[el] = shouldPlay;
        }
    }

    private void Seek(double t, bool keepPlaying = false)
    {
        ScreenMedia.Position = TimeSpan.FromSeconds(Math.Clamp(t, 0, _duration));
        if (!keepPlaying && _playing) Pause();
        Sync(Now, true);
        ApplyVisuals(Now);
    }

    private void Play()
    {
        var last = P.Segments[^1];
        if (Now >= last.End - 0.05 && Now <= last.End + 0.5) Seek(P.Segments[0].Start); // at the end of the output: restart
        _playSeg = -1;
        _playing = true;
        ScreenMedia.Play();
        PlayBtn.Content = "❚❚";
        Sync(Now, true);
    }

    private void Pause()
    {
        _playing = false;
        ScreenMedia.Pause();
        PlayBtn.Content = "▶";
        Sync(Now, true);
    }

    private void OnPlay(object sender, RoutedEventArgs e) { if (_playing) Pause(); else Play(); }

    // ---------- timeline ----------

    private const double RulerH = 18, SegTop = 26, SegH = 40, ZoomTop = 74, ZoomH = 26, TextTop = 108, CueTop = 132, AnnTop = 156, RowH = 20, AudioTop = 180, AudioH = 26, ClickTop = 210;

    private void OnTimelineResize(object sender, SizeChangedEventArgs e) => DrawTimeline();

    private TimelineView _view = TimelineView.Fit(1);

    /// <summary>On-screen span of [s, e], clamped to the visible area (deep zoom would otherwise create million-pixel elements).</summary>
    private (double X, double W)? Span(double s, double e)
    {
        var w = Timeline.ActualWidth;
        double x0 = _view.ToX(s, w), x1 = _view.ToX(e, w);
        if (x1 < -2 || x0 > w + 2) return null;
        x0 = Math.Max(x0, -8);
        x1 = Math.Min(x1, w + 8);
        return (x0, Math.Max(2, x1 - x0));
    }

    private void DrawTimeline()
    {
        if (_m == null || Timeline.ActualWidth <= 0) return;
        var w = Timeline.ActualWidth;
        _view = _view.Clamped(_duration);
        TimelineScroll.Visibility = _view.Length < _duration - 0.001 ? Visibility.Visible : Visibility.Collapsed;
        TimelineScroll.Maximum = Math.Max(0, _duration - _view.Length);
        TimelineScroll.ViewportSize = _view.Length;
        TimelineScroll.SmallChange = _view.Length * 0.15;
        TimelineScroll.LargeChange = _view.Length * 0.9;
        TimelineScroll.Value = _view.Start;
        double X(double t) => _view.ToX(t, w);
        Timeline.Children.Clear();

        // Ruler: the smallest step that keeps labels ≥ 70 px apart.
        var step = new[] { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 }.FirstOrDefault(s => s / _view.Length * w >= 70, 1200);
        for (var n = (long)Math.Floor(_view.Start / step); n * step <= _view.End; n++) // integer steps: no float drift in labels
        {
            var t = Math.Round(n * step, 3);
            Timeline.Children.Add(new Line { X1 = X(t), X2 = X(t), Y1 = 0, Y2 = 6, Stroke = Brushes.Gray });
            var lbl = new TextBlock { Text = step < 1 ? TimeSpan.FromSeconds(t).ToString(@"m\:ss\.f") : Fmt(t, false), Foreground = Brushes.Gray, FontSize = 10 };
            Canvas.SetLeft(lbl, X(t) + 3);
            Timeline.Children.Add(lbl);
        }

        var brand = (Brush)FindResource("Brand");
        for (var i = 0; i < P.Segments.Count; i++)
        {
            var s = P.Segments[i];
            if (Span(s.Start, s.End) is not { } sp) continue;
            var r = new Border
            {
                Width = Math.Max(2, sp.W - 2), Height = SegH, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x33, 0x66)), BorderBrush = brand, BorderThickness = new Thickness(1),
                Child = ClipContent(s, $"Clip {i + 1} · {Fmt(s.Duration, false)}", X(s.Start) + 1, sp.X + 1, Math.Max(2, sp.W - 2)),
                IsHitTestVisible = false, ClipToBounds = true,
            };
            Canvas.SetLeft(r, sp.X + 1);
            Canvas.SetTop(r, SegTop);
            Timeline.Children.Add(r);
        }

        for (var i = 0; i < P.Zooms.Count; i++)
        {
            var z = P.Zooms[i];
            if (Span(z.Start, z.End) is not { } sp) continue;
            var sel = i == _selectedZoom;
            var r = new Border
            {
                Width = Math.Max(4, sp.W), Height = ZoomH, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(sel ? Color.FromRgb(0x7C, 0x5C, 0xFA) : Color.FromRgb(0x2D, 0x4E, 0xC9)),
                BorderBrush = sel ? Brushes.White : null, BorderThickness = new Thickness(sel ? 1.5 : 0),
                Child = new TextBlock { Text = $"{(z.Auto ? "Auto" : "Zoom")} {z.Scale:0.0}×", Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(6, 7, 0, 0) },
                IsHitTestVisible = false,
                Opacity = z.Auto && !sel ? 0.85 : 1,
            };
            Canvas.SetLeft(r, sp.X);
            Canvas.SetTop(r, ZoomTop);
            Timeline.Children.Add(r);
        }

        if (_advanced) { DrawTextRows(); DrawAnnotationRow(); DrawKeyMarkers(X); }
        DrawAudioRow();

        foreach (var c in _clicks)
            if (c.T >= _view.Start && c.T <= _view.End)
                Timeline.Children.Add(new Line { X1 = X(c.T), X2 = X(c.T), Y1 = ClickTop, Y2 = ClickTop + 10, Stroke = Brushes.Goldenrod, StrokeThickness = 1 });
        Timeline.Children.Add(_playhead);
        ApplyVisuals(Now);
    }

    // ---------- edits ----------

    private void OnSplit(object sender, RoutedEventArgs e) => Commit(P.SplitAt(Now), "split" + Guid.NewGuid());
    private void OnDeleteSegment(object sender, RoutedEventArgs e) => Commit(P.DeleteAt(Now), "del" + Guid.NewGuid());
    private void OnMoveClipEarlier(object sender, RoutedEventArgs e) => Commit(P.MoveSegment(Now, -1), "move" + Guid.NewGuid());
    private void OnMoveClipLater(object sender, RoutedEventArgs e) => Commit(P.MoveSegment(Now, +1), "move" + Guid.NewGuid());

    private void OnAutoZoom(object sender, RoutedEventArgs e)
    {
        // Projects from before zooms were marked Auto: their old proposals (often one long zoom) look manual.
        var replaceAll = P.Zooms.Count > 0 && !P.Zooms.Any(z => z.Auto) &&
            MessageBox.Show(this, "Este proyecto tiene zooms de una versión anterior. ¿Reemplazarlos por los nuevos zooms automáticos?\n\nNo: se conservan y se añaden los automáticos.",
                App.Product, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        RegenerateAutoZoom(P.AutoZoom, "auto" + Guid.NewGuid(), replaceAll);
    }

    /// <summary>Replaces the automatic zooms (Auto = true) with fresh ones for these settings; manual zooms are kept.</summary>
    private void RegenerateAutoZoom(AutoZoomSettings s, string key, bool replaceAll = false)
    {
        _selectedZoom = -1;
        var zooms = P.Zooms.Where(z => !z.Auto && !replaceAll).Concat(AutoZoom.Propose(_cursor, _m.Width, _m.Height, _duration, s)).OrderBy(z => z.Start).ToList();
        Commit(P with { AutoZoom = s, Zooms = zooms }, key);
    }

    private void OnAutoZoomSetting(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        RegenerateAutoZoom(P.AutoZoom with { Enabled = AzEnabled.IsChecked == true, Follow = AzFollow.IsChecked == true }, "azset" + Guid.NewGuid());
    }

    private void OnAutoZoomSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing) return;
        RegenerateAutoZoom(P.AutoZoom with
        {
            Scale = Math.Round(AzScale.Value, 2), IdleReturn = Math.Round(AzIdle.Value, 1), MinHold = Math.Round(AzHold.Value, 1),
            ZoomIn = Math.Round(AzIn.Value, 2), ZoomOut = Math.Round(AzOut.Value, 2),
        }, "azslider"); // one undo step per slider drag
    }

    /// <summary>Start, duration, region, intensity and curve of a zoom, for the inspector and the timeline tooltip.</summary>
    private static string ZoomSummary(ZoomSegment z, int i)
    {
        var (ri, ro) = ZoomMath.Ramps(z);
        return $"{(z.Auto ? "Zoom automático" : "Zoom")} {i + 1}: {Fmt(z.Start)} → {Fmt(z.End)} ({z.End - z.Start:0.0} s) · {z.Scale:0.0#}× · " +
               $"centro {z.X * 100:0}% / {z.Y * 100:0}% · entrada {ri * 1000:0} ms, salida {ro * 1000:0} ms (suave) · luego vuelve a 1.0×";
    }

    private void OnAddZoom(object sender, RoutedEventArgs e)
    {
        var t = Now;
        var end = Math.Min(_duration, t + 3);
        // New zooms never overlap existing ones, so weights never stack.
        var nextStart = P.Zooms.Where(z => z.Start > t).Select(z => z.Start).DefaultIfEmpty(end).Min();
        if (P.Zooms.Any(z => t >= z.Start && t < z.End) || Math.Min(end, nextStart) - t < 0.5) return;
        var zooms = P.Zooms.Append(new ZoomSegment(t, Math.Min(end, nextStart), 2, 0.5, 0.5)).OrderBy(z => z.Start).ToList();
        Select(zoom: zooms.FindIndex(z => z.Start == t), refresh: false);
        Commit(P with { Zooms = zooms }, "add" + Guid.NewGuid());
    }

    private void OnDeleteZoom(object sender, RoutedEventArgs e)
    {
        if (_selectedZoom < 0) return;
        var zooms = P.Zooms.Where((_, i) => i != _selectedZoom).ToList();
        _selectedZoom = -1;
        Commit(P with { Zooms = zooms }, "delzoom" + Guid.NewGuid());
    }

    private void EditZoom(Func<ZoomSegment, ZoomSegment> f, string key)
    {
        if (_selectedZoom < 0 || _refreshing) return;
        var zooms = P.Zooms.ToList();
        var z = f(zooms[_selectedZoom]);
        // Overlaps are safe (weights are averaged, see ZoomMath.Evaluate) and auto-zoom pans rely on them; just stay in range.
        z = z with { Start = Math.Clamp(z.Start, 0, _duration - 0.3), End = Math.Clamp(z.End, 0.3, _duration), Auto = false };
        if (z.End - z.Start < 0.3) return;
        zooms[_selectedZoom] = z;
        Commit(P with { Zooms = zooms }, key);
    }

    private void OnZoomEdit(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        EditZoom(z => z with { Scale = Math.Round(ZoomScale.Value, 2), Ramp = Math.Round(ZoomRamp.Value, 2), RampOut = Math.Round(ZoomRamp.Value, 2) }, "zoomslider");

    private void OnZoomStartHere(object sender, RoutedEventArgs e) { var t = Now; EditZoom(z => z with { Start = t }, "zs" + Guid.NewGuid()); }
    private void OnZoomEndHere(object sender, RoutedEventArgs e) { var t = Now; EditZoom(z => z with { End = t }, "ze" + Guid.NewGuid()); }

    private void OnStageClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Source == CamMedia) return;
        if (_selectedZoom < 0) { BeginFraming(e); return; }
        var p = e.GetPosition(ZoomHost); // source pixels, accounting for the current zoom transform
        EditZoom(z => z with { X = Math.Round(Math.Clamp(p.X / _m.Width, 0, 1), 4), Y = Math.Round(Math.Clamp(p.Y / _m.Height, 0, 1), 4) }, "center" + Guid.NewGuid());
    }

    private void OnCursorEdit(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        Commit(P with { Cursor = P.Cursor with { ShowClicks = ShowClicks.IsChecked == true, Size = (int)RingSize.Value, Duration = Math.Round(RingDuration.Value, 2) } }, "cursor" + sender.GetHashCode());
    }

    private void OnSwatch(object sender, RoutedEventArgs e) =>
        Commit(P with { Cursor = P.Cursor with { Color = (string)((Button)sender).Tag } }, "color" + Guid.NewGuid());

    private void OnCamEdit(object sender, RoutedEventArgs e)
    {
        if (_refreshing || CamShape.SelectedIndex < 0) return;
        Commit(P with
        {
            Webcam = P.Webcam with
            {
                Visible = CamVisible.IsChecked == true, Mirror = CamMirror.IsChecked == true,
                Shape = (WebcamShape)CamShape.SelectedIndex, Size = Math.Round(CamSize.Value, 3),
            },
        }, "cam" + sender.GetHashCode());
    }

    private void OnCamPreset(object sender, RoutedEventArgs e)
    {
        var v = ((string)((Button)sender).Tag).Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Commit(P with { Webcam = P.Webcam with { X = v[0], Y = v[1], Size = v.Length > 2 ? v[2] : P.Webcam.Size, Visible = true } }, "campreset" + Guid.NewGuid());
    }

    private Point _camGrab;

    private void OnCamDown(object sender, MouseButtonEventArgs e)
    {
        _camDragging = true;
        _camGrab = e.GetPosition(CamFrame);
        CamFrame.CaptureMouse();
        _lastEditKey = null; // the whole drag becomes one undo step
        e.Handled = true;
    }

    private void OnCamMove(object sender, MouseEventArgs e)
    {
        if (!_camDragging) return;
        var p = e.GetPosition(Stage);
        var d = CamFrame.Width;
        Commit(P with { Webcam = P.Webcam with { X = Math.Round((p.X - _camGrab.X + d / 2) / SW, 4), Y = Math.Round((p.Y - _camGrab.Y + d / 2) / SH, 4) } }, "camdrag");
    }

    private void OnCamUp(object sender, MouseButtonEventArgs e)
    {
        _camDragging = false;
        CamFrame.ReleaseMouseCapture();
        _lastEditKey = null;
    }

    private void OnAudioEdit(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        Commit(P with
        {
            Audio = new AudioMix(Math.Round(MicGain.Value), Math.Round(SysGain.Value), MicMute.IsChecked == true, SysMute.IsChecked == true,
                Math.Round(MicSync.Value, 3), Math.Round(SysSync.Value, 3)),
        }, "audio" + sender.GetHashCode());
        Sync(Now, true);
    }

    private void OnExportEdit(object sender, RoutedEventArgs e)
    {
        if (_refreshing || ExportHeight.SelectedItem is not ComboBoxItem h || ExportFpsBox.SelectedItem is not ComboBoxItem f
            || ExportQualityBox.SelectedIndex < 0 || ExportFormatBox.SelectedIndex < 0) return;
        Commit(P with
        {
            Export = new ExportSettings(int.Parse((string)h.Tag), (ExportQuality)ExportQualityBox.SelectedIndex, (ExportFormat)ExportFormatBox.SelectedIndex, int.Parse((string)f.Tag)),
        }, "export" + Guid.NewGuid());
    }

    /// <summary>Presets only fill in the settings; everything stays editable afterwards.</summary>
    private void OnPreset(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || PresetBox.SelectedIndex <= 0) return;
        var ex = P.Export;
        var next = PresetBox.SelectedIndex switch
        {
            1 => P with { Export = ex with { Format = ExportFormat.Mp4H264, Quality = ExportQuality.Fast } },
            2 => P with { Export = ex with { Format = ExportFormat.Mp4H264, Quality = ExportQuality.Balanced } },
            3 => P with { Export = ex with { Format = ExportFormat.Mp4H264, Quality = ExportQuality.High } },
            4 => P with { Export = ex with { Format = ExportFormat.Mp4H264, Quality = ExportQuality.Max } },
            5 => P with { Export = ex with { Format = ExportFormat.Mp4H264, Quality = ExportQuality.High, Height = 2160 } },
            _ => P with { Canvas = P.Canvas with { Aspect = CanvasAspect.Vertical }, Export = ex with { Format = ExportFormat.Mp4H264, Quality = ExportQuality.Balanced, Height = 1080, Fps = 30 } },
        };
        Commit(next, "preset" + Guid.NewGuid());
    }

    private void OnShowQueue(object sender, RoutedEventArgs e) => RenderQueueWindow.ShowQueue();

    private void OnUndo(object sender, RoutedEventArgs e) { _history.Undo(); _lastEditKey = null; _dirty = true; Refresh(); }
    private void OnRedo(object sender, RoutedEventArgs e) { _history.Redo(); _lastEditKey = null; _dirty = true; Refresh(); }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var frame = 1.0 / Math.Max(1, _m.Fps);
        switch (e.Key)
        {
            case Key.Space: OnPlay(this, e); break;
            case Key.S when ctrl: OnSave(this, e); break;
            case Key.O when ctrl: OnOpen(this, e); break;
            case Key.S: OnSplit(this, e); break;
            case Key.Delete: OnDeleteSegment(this, e); break;
            case Key.Z when ctrl: OnUndo(this, e); break;
            case Key.Y when ctrl: OnRedo(this, e); break;
            case Key.Left when ctrl: OnMoveClipEarlier(this, e); break;
            case Key.Right when ctrl: OnMoveClipLater(this, e); break;
            case Key.Left: Seek(Now - frame); RevealPlayhead(); break;
            case Key.Right: Seek(Now + frame); RevealPlayhead(); break;
            case Key.OemPlus or Key.Add when ctrl: ZoomTimeline(1.5); break;
            case Key.OemMinus or Key.Subtract when ctrl: ZoomTimeline(1 / 1.5); break;
            case Key.D0 or Key.NumPad0 when ctrl: OnFitTimeline(this, e); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------- files ----------

    private void Save()
    {
        try
        {
            P.Save(_path);
            Snapshot();
            _dirty = false;
            Title = $"{App.Product} — Editor";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(ex.ToString());
            MessageBox.Show(this, "No se pudo guardar el proyecto: " + ex.Message, App.Product, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = $"Proyecto Angelito Studio|*{StudioProject.Extension}", InitialDirectory = App.RecordingsDir };
        if (dlg.ShowDialog(this) == true) new EditorWindow(dlg.FileName).Show();
    }

    /// <summary>Queues an export of the project as it is now. PNG exports the frame under the playhead.</summary>
    private void OnExport(object sender, RoutedEventArgs e)
    {
        var fmt = P.Export.Format;
        var dlg = new SaveFileDialog
        {
            Filter = fmt switch
            {
                ExportFormat.MkvH264 => "Video MKV (H.264)|*.mkv", ExportFormat.Mp4Hevc => "Video MP4 (HEVC/H.265)|*.mp4", ExportFormat.Mp4Av1 => "Video MP4 (AV1)|*.mp4",
                ExportFormat.Wav => "Audio WAV|*.wav", ExportFormat.Png => "Imagen PNG|*.png", _ => "Video MP4 (H.264)|*.mp4",
            },
            FileName = ProjectName.Text + (fmt == ExportFormat.Png ? $"-{Now:0.00}s" : "") + P.Export.Extension,
            InitialDirectory = Environment.GetFolderPath(fmt == ExportFormat.Png ? Environment.SpecialFolder.MyPictures : Environment.SpecialFolder.MyVideos),
        };
        if (dlg.ShowDialog(this) != true) return;
        var media = new[] { _m.Screen, _m.Webcam, _m.Mic, _m.SystemAudio }.Where(f => f != null).Select(f => System.IO.Path.GetFullPath(System.IO.Path.Combine(_sessionDir, f!)));
        if (media.Contains(System.IO.Path.GetFullPath(dlg.FileName), StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Elige otro nombre: no se sobrescriben los archivos originales.", App.Product);
            return;
        }
        if (RenderQueue.Jobs.Any(j => j.IsActive && string.Equals(j.OutputPath, dlg.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Ya hay una exportación en cola hacia ese archivo.", App.Product);
            return;
        }

        Save();
        var project = fmt == ExportFormat.Png ? P.StillAt(Now, Exporter.Fps(P, _m)) : P;
        var temp = System.IO.Path.Combine(App.DataDir, "jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var (ow, oh) = Layout.OutputSize(_m, project.Export, project.Canvas);
        RenderQueue.Enqueue(new RenderJob
        {
            Project = project, Manifest = _m, SessionDir = _sessionDir, Cursor = _cursor, OutputPath = dlg.FileName, TempDir = temp,
            AnnotationImages = fmt == ExportFormat.Wav ? [] : RenderAnnotationImages(temp),
            Keys = _keyEvents, KeyImages = fmt == ExportFormat.Wav ? [] : RenderKeyImages(project, temp),
            Details = fmt switch
            {
                ExportFormat.Wav => $"WAV · {Fmt(project.OutputDuration)}",
                ExportFormat.Png => $"{ow}×{oh} · PNG · fotograma en {Fmt(Now)}",
                _ => $"{ow}×{oh} · {FormatName(fmt)} · {Fmt(project.OutputDuration)}",
            },
        });
        RenderQueueWindow.ShowQueue();
    }

    private static string FormatName(ExportFormat f) => f switch
    {
        ExportFormat.Mp4Hevc => "MP4 HEVC", ExportFormat.Mp4Av1 => "MP4 AV1", ExportFormat.MkvH264 => "MKV H.264", ExportFormat.Wav => "WAV", ExportFormat.Png => "PNG", _ => "MP4 H.264",
    };
    private static void TryDelete(string f) { try { File.Delete(f); } catch (IOException) { } }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnFrame;
        _cacheCts.Cancel();
        _autosave.Stop();
        if (_dirty && _m != null) Save();
        foreach (var el in new[] { ScreenMedia, CamMedia, MicMedia, SysMedia }) el.Close();
        base.OnClosed(e);
    }

    private static string Fmt(double s, bool ms = true) =>
        ms ? TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"m\:ss\.ff") : TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"m\:ss");
}
