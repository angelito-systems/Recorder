using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>Output format (16:9, 9:16, 1:1, 4:3) and manual reframing by dragging the video.</summary>
public partial class EditorWindow
{
    /// <summary>Stage size = the canvas window, in source-pixel units (output-space layers are laid out on it).</summary>
    private double SW => P.Canvas.Window(_m.Width, _m.Height).W;
    private double SH => P.Canvas.Window(_m.Width, _m.Height).H;

    private (Point Down, ProjectCanvas Orig, string Key)? _framing;

    private void RefreshCanvas()
    {
        Stage.Width = SW;
        Stage.Height = SH;
        RefreshBackground();
        FormatBox.SelectedIndex = (int)P.Canvas.Aspect;
        FramingHint.Visibility = P.Canvas.Aspect == CanvasAspect.Source ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Preview of <see cref="BackgroundStyle"/>: same rect as Layout.ScreenRect, on the stage's units.</summary>
    private void RefreshBackground()
    {
        var bg = P.Background;
        var (x, y, w, h, r) = Layout.ScreenRect((int)Math.Round(SW), (int)Math.Round(SH), bg);
        double kx = w / SW, ky = h / SH;
        ScreenScale.ScaleX = kx;
        ScreenScale.ScaleY = ky;
        // The clip is in the frame's own (unscaled) units, so the radius is divided by the scale to land at r on screen.
        ScreenFrame.Clip = bg.Enabled ? new System.Windows.Media.RectangleGeometry(new Rect(0, 0, SW, SH), r / kx, r / ky) : null;
        Stage.Background = bg.Enabled
            ? new System.Windows.Media.LinearGradientBrush(Brush(bg.Color1), Brush(bg.Color2), new Point(0, 0), new Point(1, 1))
            : System.Windows.Media.Brushes.Black;
        BgEnabled.IsChecked = bg.Enabled;
        BgControls.IsEnabled = bg.Enabled;
        BgPadding.Value = bg.Padding;
        BgRadius.Value = bg.Radius;
        BgPaddingText.Text = $"Margen: {bg.Padding * 100:0.#} %";
        BgRadiusText.Text = $"Esquinas redondeadas: {bg.Radius * 100:0.#} %";
        static System.Windows.Media.Color Brush(string hex) => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }

    private void OnBgEdit(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        var key = sender is Slider ? "bgslider" : "bg" + Guid.NewGuid(); // a slider drag is one undo step
        Commit(P with { Background = P.Background with { Enabled = BgEnabled.IsChecked == true, Padding = Math.Round(BgPadding.Value, 3), Radius = Math.Round(BgRadius.Value, 3) } }, key);
    }

    private void OnBgPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || tag.Split(',') is not [var c1, var c2]) return;
        Commit(P with { Background = P.Background with { Enabled = true, Color1 = c1, Color2 = c2 } }, "bgpreset" + Guid.NewGuid());
    }

    private void OnFormatEdit(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || FormatBox.SelectedIndex < 0) return;
        var aspect = (CanvasAspect)FormatBox.SelectedIndex;
        // Social formats usually want an exact size: default to 1080 on the short side when switching away from "original".
        var export = aspect != CanvasAspect.Source && P.Canvas.Aspect == CanvasAspect.Source && P.Export.Height == 0 ? P.Export with { Height = 1080 } : P.Export;
        Commit(P with { Canvas = P.Canvas with { Aspect = aspect }, Export = export }, "format" + Guid.NewGuid());
    }

    /// <summary>Dragging the picture (no zoom selected) moves the canvas window over the recording.</summary>
    private void BeginFraming(MouseButtonEventArgs e)
    {
        if (P.Canvas.Aspect == CanvasAspect.Source) return;
        _framing = (e.GetPosition(Stage), P.Canvas, "drag" + Guid.NewGuid());
        Stage.CaptureMouse();
        e.Handled = true;
    }

    private void OnStageMove(object sender, MouseEventArgs e)
    {
        if (_framing is not { } f) return;
        var p = e.GetPosition(Stage);
        // The content follows the mouse, so the window moves the opposite way; clamping keeps it inside the frame.
        var (cw, ch) = f.Orig.Window(_m.Width, _m.Height);
        double x = f.Orig.X - (p.X - f.Down.X) / _m.Width, y = f.Orig.Y - (p.Y - f.Down.Y) / _m.Height;
        x = Math.Clamp(x, cw / 2 / _m.Width, 1 - cw / 2 / _m.Width);
        y = Math.Clamp(y, ch / 2 / _m.Height, 1 - ch / 2 / _m.Height);
        Commit(P with { Canvas = f.Orig with { X = Math.Round(x, 4), Y = Math.Round(y, 4) } }, f.Key);
    }

    private void OnStageUp(object sender, MouseButtonEventArgs e)
    {
        if (_framing == null) return;
        _framing = null;
        _lastEditKey = null;
        Stage.ReleaseMouseCapture();
    }
}
