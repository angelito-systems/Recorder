using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>Timeline thumbnails and audio waveforms, built in the background from the session's disk cache.</summary>
public partial class EditorWindow
{
    private readonly CancellationTokenSource _cacheCts = new();
    private string? _thumbDir;
    private double _thumbInterval;
    private int _thumbCount;
    private byte[]? _micPeaks, _sysPeaks;
    private const int MaxBitmaps = 300; // decoded thumbnails kept in memory
    private readonly Dictionary<int, BitmapImage> _bitmaps = [];
    private readonly LinkedList<int> _bitmapOrder = [];

    private void StartMediaCache()
    {
        var ct = _cacheCts.Token;
        _ = Background(async () =>
        {
            var video = System.IO.Path.Combine(_sessionDir, _m.Screen);
            var dur = await FfmpegProcess.ProbeDurationAsync(video);
            var dir = await MediaCache.ThumbnailsAsync(video, _sessionDir, dur, ct);
            var count = Directory.GetFiles(dir, "*.jpg").Length;
            await Dispatcher.InvokeAsync(() => { _thumbDir = dir; _thumbInterval = MediaCache.ThumbInterval(dur); _thumbCount = count; DrawTimeline(); });
        });
        if (_m.Mic != null) _ = Background(async () => { var p = await MediaCache.PeaksAsync(System.IO.Path.Combine(_sessionDir, _m.Mic), _sessionDir, ct); await Dispatcher.InvokeAsync(() => { _micPeaks = p; DrawTimeline(); }); });
        if (_m.SystemAudio != null) _ = Background(async () => { var p = await MediaCache.PeaksAsync(System.IO.Path.Combine(_sessionDir, _m.SystemAudio), _sessionDir, ct); await Dispatcher.InvokeAsync(() => { _sysPeaks = p; DrawTimeline(); }); });
    }

    /// <summary>Cache work is optional: failures are logged, the editor keeps working without thumbnails/waveforms.</summary>
    private static Task Background(Func<Task> work) => Task.Run(async () =>
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("media cache: " + ex.Message); }
    });

    /// <summary>
    /// Clip block content: thumbnails tiled from the clip's true start (so they don't slide while scrolling), created only
    /// for the visible part [visX, visX + visW]; the label sits at the visible left edge.
    /// </summary>
    private UIElement ClipContent(Segment s, string label, double clipX, double visX, double visW)
    {
        var g = new Grid();
        if (_thumbDir != null && _thumbCount > 0 && _m.Height > 0)
        {
            var strip = new Canvas { ClipToBounds = true, Opacity = 0.75 };
            var tw = (SegH - 2) * _m.Width / (double)_m.Height;
            var secPerPx = _view.Length / Math.Max(1, Timeline.ActualWidth);
            var first = Math.Max(0, (int)Math.Floor((visX - clipX) / tw));
            for (var k = first; clipX + k * tw < visX + visW; k++)
            {
                var t = s.Start + (k * tw + tw / 2) * secPerPx;
                if (t > s.End) break;
                var bmp = Thumb(Math.Clamp((int)(t / _thumbInterval), 0, _thumbCount - 1));
                if (bmp == null) continue;
                var img = new Image { Source = bmp, Width = tw, Height = SegH - 2, Stretch = Stretch.UniformToFill };
                Canvas.SetLeft(img, clipX + k * tw - visX);
                strip.Children.Add(img);
            }
            g.Children.Add(strip);
        }
        g.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x1E, 0x1A, 0x38)), CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 3, 0, 0), Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 11 },
        });
        return g;
    }

    /// <summary>LRU of decoded thumbnails, decoded at display height.</summary>
    private BitmapImage? Thumb(int index)
    {
        if (_bitmaps.TryGetValue(index, out var hit))
        {
            _bitmapOrder.Remove(index);
            _bitmapOrder.AddFirst(index);
            return hit;
        }
        var path = MediaCache.ThumbPath(_thumbDir!, index);
        if (!File.Exists(path)) return index > 0 ? Thumb(index - 1) : null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path);
        bmp.DecodePixelHeight = (int)SegH;
        bmp.CacheOption = BitmapCacheOption.OnLoad; // read now, don't keep the file open
        bmp.EndInit();
        bmp.Freeze();
        _bitmaps[index] = bmp;
        _bitmapOrder.AddFirst(index);
        if (_bitmapOrder.Count > MaxBitmaps) { _bitmaps.Remove(_bitmapOrder.Last!.Value); _bitmapOrder.RemoveLast(); }
        return bmp;
    }

    private void DrawAudioRow()
    {
        var w = Timeline.ActualWidth;
        void Wave(byte[]? peaks, double delay, bool muted, Color color)
        {
            if (peaks == null || peaks.Length == 0) return;
            var geo = new StreamGeometry();
            var mid = AudioTop + AudioH / 2.0;
            using (var ctx = geo.Open())
            {
                for (var c = 0; c < (int)w; c++)
                {
                    // Peaks covering this pixel column, shifted onto screen time like the player and exporter do.
                    double t0 = _view.ToTime(c, w) - delay, t1 = _view.ToTime(c + 1, w) - delay;
                    var i0 = Math.Max(0, (int)(t0 * MediaCache.PeaksPerSecond));
                    var i1 = Math.Min(peaks.Length, Math.Max(i0 + 1, (int)Math.Ceiling(t1 * MediaCache.PeaksPerSecond))); // deep zoom: ≥1 bin per column
                    var max = 0;
                    for (var i = i0; i < i1; i++) max = Math.Max(max, peaks[i]);
                    if (max == 0) continue;
                    var hgt = Math.Sqrt(max / 255.0) * (AudioH / 2.0 - 1); // sqrt: quiet speech stays visible
                    ctx.BeginFigure(new Point(c + 0.5, mid - hgt), false, false);
                    ctx.LineTo(new Point(c + 0.5, mid + hgt), true, false);
                }
            }
            geo.Freeze();
            Timeline.Children.Add(new System.Windows.Shapes.Path { Data = geo, Stroke = new SolidColorBrush(color), StrokeThickness = 1, Opacity = muted ? 0.25 : 0.8, IsHitTestVisible = false });
        }
        Wave(_sysPeaks, _m.SystemAudioOffset - _m.ScreenOffset + P.Audio.SystemSync, P.Audio.SystemMuted, Color.FromRgb(0x2D, 0xD4, 0xBF));
        Wave(_micPeaks, _m.MicOffset - _m.ScreenOffset + P.Audio.MicSync, P.Audio.MicMuted, Color.FromRgb(0xA7, 0x8B, 0xFA));
    }
}
