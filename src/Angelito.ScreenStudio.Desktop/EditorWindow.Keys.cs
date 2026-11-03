using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>Keystroke overlay in the editor: preview badge, style inspector, timeline markers.</summary>
public partial class EditorWindow
{
    private List<KeyEvent> _keyEvents = [];
    private List<(double Start, double End, string Combo)> _keyWindows = [];
    private readonly Dictionary<string, FrameworkElement> _keyEls = [];
    private (FrameworkElement El, Point Grab)? _keyDrag;
    private (double X, double Y)? _keyDragPos;

    private void LoadKeys() => _keyEvents = _m.LoadKeys(_sessionDir);

    private void RefreshKeys()
    {
        KeysSection.Visibility = _m.Keys != null ? Visibility.Visible : Visibility.Collapsed;
        var s = P.Keys;
        KeysVisible.IsChecked = s.Visible;
        KeysLook.SelectedIndex = (int)s.Look;
        KeysSize.Value = s.Size;
        KeysDuration.Value = s.Duration;
        KeysInfo.Text = _keyEvents.Count == 0 ? "No se registraron atajos en esta grabación." : $"{_keyEvents.Count} atajos registrados.";

        _keyWindows = s.Visible ? KeyLog.Windows(_keyEvents, s.Duration) : [];
        KeyLayer.Children.Clear();
        _keyEls.Clear();
        foreach (var combo in _keyWindows.Select(w => w.Combo).Distinct())
        {
            var el = KeystrokeVisual.Create(combo, s, Math.Min(SW, SH));
            el.Visibility = Visibility.Collapsed;
            el.Cursor = Cursors.SizeAll;
            el.MouseLeftButtonDown += (_, e) => { _keyDrag = (el, e.GetPosition(el)); el.CaptureMouse(); e.Handled = true; };
            el.MouseMove += (_, e) => DragKey(e);
            el.MouseLeftButtonUp += (_, _) => EndKeyDrag();
            Place(el, s.X, s.Y);
            KeyLayer.Children.Add(el);
            _keyEls[combo] = el;
        }
    }

    private void Place(FrameworkElement el, double x, double y)
    {
        Canvas.SetLeft(el, x * SW - el.DesiredSize.Width / 2);
        Canvas.SetTop(el, y * SH - el.DesiredSize.Height / 2);
    }

    private void ApplyKeys(double t)
    {
        string? current = null;
        var alpha = 1.0;
        foreach (var w in _keyWindows) if (t >= w.Start && t <= w.End) { current = w.Combo; alpha = Layout.FadeAlpha(t, w.Start, w.End); break; }
        foreach (var (combo, el) in _keyEls)
        {
            var dragged = _keyDrag?.El == el;
            el.Visibility = combo == current || dragged ? Visibility.Visible : Visibility.Collapsed;
            el.Opacity = dragged || !_playing ? 1 : alpha;
        }
    }

    private void DragKey(MouseEventArgs e)
    {
        if (_keyDrag is not { } d || !d.El.IsMouseCaptured) return;
        var p = e.GetPosition(Stage);
        double x = Math.Clamp((p.X - d.Grab.X + d.El.DesiredSize.Width / 2) / SW, 0, 1), y = Math.Clamp((p.Y - d.Grab.Y + d.El.DesiredSize.Height / 2) / SH, 0, 1);
        Place(d.El, x, y);
        _keyDragPos = (Math.Round(x, 4), Math.Round(y, 4)); // committed on release: a commit rebuilds the layer
    }

    private void EndKeyDrag()
    {
        if (_keyDrag is not { } d) return;
        d.El.ReleaseMouseCapture();
        _keyDrag = null;
        if (_keyDragPos is { } pos) Commit(P with { Keys = P.Keys with { X = pos.X, Y = pos.Y } }, "keydrag" + Guid.NewGuid());
        _keyDragPos = null;
    }

    private void DrawKeyMarkers(Func<double, double> x)
    {
        foreach (var k in _keyEvents)
            if (k.T >= _view.Start && k.T <= _view.End)
                Timeline.Children.Add(new Line { X1 = x(k.T), X2 = x(k.T), Y1 = ClickTop + 12, Y2 = ClickTop + 22, Stroke = (Brush)FindResource("Brand"), StrokeThickness = 2, ToolTip = k.Combo });
    }

    private void OnKeysEdit(object sender, RoutedEventArgs e)
    {
        if (_refreshing || KeysLook.SelectedIndex < 0) return;
        Commit(P with
        {
            Keys = P.Keys with { Visible = KeysVisible.IsChecked == true, Look = (KeyLook)KeysLook.SelectedIndex, Size = Math.Round(KeysSize.Value, 3), Duration = Math.Round(KeysDuration.Value, 2) },
        }, "keys" + sender.GetHashCode());
    }

    /// <summary>One PNG per distinct shortcut at the output's resolution, for the exporter.</summary>
    private Dictionary<string, (string Png, int W, int H)> RenderKeyImages(StudioProject project, string dir)
    {
        var map = new Dictionary<string, (string, int, int)>();
        if (!project.Keys.Visible) return map;
        var (ow, oh) = Layout.OutputSize(_m, project.Export, project.Canvas);
        var i = 0;
        foreach (var combo in _keyEvents.Select(k => k.Combo).Distinct())
            map[combo] = KeystrokeVisual.RenderPng(combo, project.Keys, Math.Min(ow, oh), System.IO.Path.Combine(dir, $"key{i++}.png"));
        return map;
    }
}
