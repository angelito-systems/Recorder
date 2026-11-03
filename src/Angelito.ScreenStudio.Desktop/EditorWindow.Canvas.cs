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
        FormatBox.SelectedIndex = (int)P.Canvas.Aspect;
        FramingHint.Visibility = P.Canvas.Aspect == CanvasAspect.Source ? Visibility.Collapsed : Visibility.Visible;
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
