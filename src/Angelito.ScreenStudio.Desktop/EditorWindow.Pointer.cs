using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Angelito.ScreenStudio.Core;
using Microsoft.Win32;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>
/// The recorded Windows cursor drawn over the preview from <see cref="CursorTrack"/> — the same function the exporter
/// uses — inside the zoomed layer, so it follows zooms exactly like the export.
/// </summary>
public partial class EditorWindow
{
    private CursorTrack? _pointer;
    private (PointerStyle Style, double Duration)? _pointerFor;
    private readonly Dictionary<(string Id, int Frame), BitmapSource> _pointerFrames = [];

    /// <summary>Rebuilds the track only when its inputs change (smoothing a long project isn't free).</summary>
    private void RefreshPointer()
    {
        if (_pointerFor is { } f && f.Style == P.Pointer && f.Duration == _duration) return;
        _pointerFor = (P.Pointer, _duration);
        _pointer = CursorTrack.ForSession(P, _m, _sessionDir, _cursor, _duration);
        _pointerFrames.Remove((CursorTrack.CustomShapeId, 0)); // the custom image may have changed
    }

    private void ApplyPointer(double t)
    {
        var st = _pointer?.At(t, Exporter.Fps(P, _m)) ?? default;
        if (st.ShapeId == null || _pointer?.Shape(st.ShapeId) is not { } shape || PointerBitmap(st.ShapeId, st.Frame, shape) is not { } bmp)
        {
            PointerImg.Visibility = Visibility.Collapsed;
            return;
        }
        PointerImg.Source = bmp;
        PointerImg.Width = shape.W * st.Scale;
        PointerImg.Height = shape.H * st.Scale;
        Canvas.SetLeft(PointerImg, st.X);
        Canvas.SetTop(PointerImg, st.Y);
        PointerImg.Opacity = st.Alpha;
        PointerImg.Visibility = Visibility.Visible;
    }

    private BitmapSource? PointerBitmap(string id, int frame, CursorShape shape)
    {
        if (_pointerFrames.TryGetValue((id, frame), out var b)) return b;
        try
        {
            if (id == CursorTrack.CustomShapeId)
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                img.UriSource = new Uri(Path.Combine(_sessionDir, P.Pointer.CustomImage!));
                img.EndInit();
                b = img;
            }
            else
            {
                var px = File.ReadAllBytes(CursorShapeStore.PixelsPath(_sessionDir, id));
                var size = shape.W * shape.H * 4;
                b = BitmapSource.Create(shape.W, shape.H, 96, 96, PixelFormats.Bgra32, null, px[(frame * size)..((frame + 1) * size)], shape.W * 4);
            }
            b.Freeze();
            return _pointerFrames[(id, frame)] = b;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException) { App.Log("pointer image: " + e.Message); return null; }
    }

    private void RefreshPointerControls()
    {
        var baked = _m.CursorInVideo;
        PtrBakedNote.Visibility = baked ? Visibility.Visible : Visibility.Collapsed;
        PtrControls.Visibility = baked ? Visibility.Collapsed : Visibility.Visible;
        var s = P.Pointer;
        PtrVisible.IsChecked = s.Visible;
        PtrMode.SelectedIndex = (int)s.Mode;
        (PtrScale.Value, PtrSmooth.Value, PtrOpacity.Value) = (s.Scale, s.Smoothing, s.Opacity);
        PtrHideIdle.IsChecked = s.HideIdle;
        PtrScale.IsEnabled = s.Mode != PointerMode.Original;
        PtrSmooth.IsEnabled = s.Mode == PointerMode.Cinematic;
        PtrCustomPanel.Visibility = s.Mode == PointerMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        PtrScaleText.Text = s.Mode == PointerMode.Original ? "Tamaño: original" : $"Tamaño: {s.Scale:0.0#}×";
        PtrSmoothText.Text = s.Mode == PointerMode.Cinematic ? $"Suavizado: {s.Smoothing * 100:0} %" : "Suavizado: solo en modo cinematográfico";
        PtrOpacityText.Text = $"Opacidad: {s.Opacity * 100:0} %";
        PtrCustomInfo.Text = s.CustomImage == null ? "Sin imagen: se ve la flecha original." : $"{Path.GetFileName(s.CustomImage)} · punto de clic ({s.CustomHotX}, {s.CustomHotY}) px";
    }

    private void OnPointerEdit(object sender, RoutedEventArgs e)
    {
        if (_refreshing || PtrMode.SelectedIndex < 0) return;
        var key = sender is Slider ? "ptrslider" : "ptr" + Guid.NewGuid(); // one undo step per slider drag
        Commit(P with
        {
            Pointer = P.Pointer with
            {
                Visible = PtrVisible.IsChecked == true, Mode = (PointerMode)PtrMode.SelectedIndex, Scale = Math.Round(PtrScale.Value, 2),
                Smoothing = Math.Round(PtrSmooth.Value, 2), Opacity = Math.Round(PtrOpacity.Value, 2), HideIdle = PtrHideIdle.IsChecked == true,
            },
        }, key);
    }

    /// <summary>A PNG replaces the arrow (the other shapes stay real); copied into the session so the project stays portable.</summary>
    private void OnPointerImage(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Imagen PNG|*.png" };
        if (dlg.ShowDialog(this) != true) return;
        if (CursorTrack.PngSize(dlg.FileName) is not { } size || size.W > 512 || size.H > 512)
        {
            MessageBox.Show(this, "Elige un PNG de 512×512 píxeles como máximo.", App.Product);
            return;
        }
        var rel = Path.Combine(CursorShapeStore.Folder, "custom-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            Directory.CreateDirectory(Path.Combine(_sessionDir, CursorShapeStore.Folder));
            File.Copy(dlg.FileName, Path.Combine(_sessionDir, rel));
        }
        catch (IOException ex) { MessageBox.Show(this, "No se pudo copiar la imagen.\n\n" + ex.Message, App.Product); return; }
        var center = PtrHotCenter.IsChecked == true;
        Commit(P with { Pointer = P.Pointer with { Mode = PointerMode.Custom, CustomImage = rel, CustomHotX = center ? size.W / 2 : 0, CustomHotY = center ? size.H / 2 : 0 } }, "ptrimg" + Guid.NewGuid());
    }
}
