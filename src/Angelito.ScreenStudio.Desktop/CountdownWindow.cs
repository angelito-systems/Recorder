using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Angelito.ScreenStudio.Desktop.Capture;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>3-2-1 over the screen about to be recorded. Excluded from capture; Esc or a click cancels.</summary>
public sealed class CountdownWindow : Window
{
    private readonly TextBlock _num = new() { FontSize = 120, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TaskCompletionSource<bool> _done = new();

    private CountdownWindow(MonitorInfo mon)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Height = 240;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Monitor pixels → WPF units happens after the handle exists; center roughly first, refine in SourceInitialized.
        Left = mon.X + mon.Width / 2.0 - Width / 2;
        Top = mon.Y + mon.Height / 2.0 - Height / 2;
        _num.RenderTransformOrigin = new Point(0.5, 0.5);
        _num.RenderTransform = _scale;
        Content = new Grid
        {
            Children =
            {
                new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(200, 20, 20, 28)), Stroke = (Brush)Application.Current.FindResource("Rec"), StrokeThickness = 6 },
                _num,
                new TextBlock { Text = "Esc para cancelar", Foreground = Brushes.White, Opacity = 0.7, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 34) },
            },
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = (mon.X + mon.Width / 2.0) / dpi.DpiScaleX - Width / 2;
            Top = (mon.Y + mon.Height / 2.0) / dpi.DpiScaleY - Height / 2;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(false); };
        MouseLeftButtonDown += (_, _) => Finish(false);
        Loaded += async (_, _) =>
        {
            Activate();
            for (var n = 3; n >= 1 && !_done.Task.IsCompleted; n--)
            {
                _num.Text = n.ToString();
                var grow = new DoubleAnimation(1.6, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } };
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                _num.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(250)));
                await Task.Delay(1000);
            }
            Finish(true);
        };
    }

    private void Finish(bool go)
    {
        if (!_done.TrySetResult(go)) return;
        Close();
    }

    /// <summary>True when the countdown ran out, false if the user cancelled.</summary>
    public static Task<bool> RunAsync(MonitorInfo mon)
    {
        var w = new CountdownWindow(mon);
        w.Show();
        return w._done.Task;
    }
}
