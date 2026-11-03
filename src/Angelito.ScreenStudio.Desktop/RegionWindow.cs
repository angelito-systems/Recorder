using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Angelito.ScreenStudio.Desktop.Capture;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>Dimmed overlay over one monitor; drag a rectangle, release to accept, Esc to cancel. Result is in physical pixels.</summary>
public sealed class RegionWindow : Window
{
    private readonly MonitorInfo _mon;
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)) };
    private readonly Rectangle _rect = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFA)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) };
    private readonly TextBlock _label = new() { Foreground = Brushes.White, FontSize = 14, Text = "Arrastra para seleccionar la región · Esc para cancelar" };
    private Point? _start;
    public Region? Result { get; private set; }

    public RegionWindow(MonitorInfo mon)
    {
        _mon = mon;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Cross;
        _canvas.Children.Add(_rect);
        _canvas.Children.Add(_label);
        Canvas.SetLeft(_label, 24);
        Canvas.SetTop(_label, 24);
        _rect.Visibility = Visibility.Collapsed;
        Content = _canvas;
        // Cover the monitor in physical pixels regardless of its DPI scale.
        SourceInitialized += (_, _) => Native.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, mon.X, mon.Y, mon.Width, mon.Height, 0x0004 /*SWP_NOZORDER*/);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); _rect.Visibility = Visibility.Visible; CaptureMouse(); Update(_start.Value); };
        MouseMove += (_, e) => { if (_start != null) Update(e.GetPosition(_canvas)); };
        MouseLeftButtonUp += (_, e) => Accept(e.GetPosition(_canvas));
    }

    private (double X, double Y, double W, double H) Box(Point p) =>
        (Math.Min(_start!.Value.X, p.X), Math.Min(_start.Value.Y, p.Y), Math.Abs(p.X - _start.Value.X), Math.Abs(p.Y - _start.Value.Y));

    private void Update(Point p)
    {
        var (x, y, w, h) = Box(p);
        Canvas.SetLeft(_rect, x);
        Canvas.SetTop(_rect, y);
        _rect.Width = w;
        _rect.Height = h;
        var s = VisualTreeHelper.GetDpi(this);
        _label.Text = $"{(int)(w * s.DpiScaleX) & ~1} × {(int)(h * s.DpiScaleY) & ~1}";
    }

    private void Accept(Point p)
    {
        if (_start == null) return;
        ReleaseMouseCapture();
        var (x, y, w, h) = Box(p);
        var s = VisualTreeHelper.GetDpi(this);
        int px = (int)Math.Round(x * s.DpiScaleX), py = (int)Math.Round(y * s.DpiScaleY);
        int pw = Math.Min((int)Math.Round(w * s.DpiScaleX), _mon.Width - px), ph = Math.Min((int)Math.Round(h * s.DpiScaleY), _mon.Height - py);
        if (pw < 64 || ph < 64) { _start = null; _rect.Visibility = Visibility.Collapsed; _label.Text = "Región demasiado pequeña (mínimo 64×64). Vuelve a arrastrar."; return; }
        Result = new Region(px, py, pw, ph);
        DialogResult = true;
    }
}
