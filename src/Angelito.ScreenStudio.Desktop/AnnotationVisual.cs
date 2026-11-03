using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>
/// The one drawing of an annotation. The editor shows these elements live; the exporter overlays PNGs rendered from
/// the very same elements, so vector shapes look identical in preview and render. Units are source pixels.
/// </summary>
internal static class AnnotationVisual
{
    /// <summary>Element sized to <c>Bounds</c> (its top-left goes at Bounds.X/Y). Masks need the live video as <paramref name="source"/>.</summary>
    public static (FrameworkElement El, Int32Rect Bounds) Create(Annotation a, int w, int h, Visual? source = null)
    {
        var color = (Color)ColorConverter.ConvertFromString(a.Color);
        var stroke = new SolidColorBrush(color);
        var t = Math.Max(1, a.Thickness);
        var (bx, by, bw, bh) = a.BoxPx(w, h);
        var box = new Int32Rect(bx, by, bw, bh);
        switch (a.Kind)
        {
            case AnnotationKind.Rectangle:
                return (new Rectangle { Width = bw, Height = bh, Stroke = stroke, StrokeThickness = t, RadiusX = 4, RadiusY = 4 }, box);
            case AnnotationKind.Ellipse:
                return (new Ellipse { Width = bw, Height = bh, Stroke = stroke, StrokeThickness = t }, box);
            case AnnotationKind.Highlight:
                return (new Rectangle { Width = bw, Height = bh, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(Color.FromArgb(0x59, color.R, color.G, color.B)) }, box);
            case AnnotationKind.Arrow:
                return Arrow(a.X * w, a.Y * h, a.X2 * w, a.Y2 * h, t, stroke);
            default:
                return (Mask(a, box, source), box);
        }
    }

    private static (FrameworkElement, Int32Rect) Arrow(double x1, double y1, double x2, double y2, double t, Brush brush)
    {
        double head = Math.Max(16, t * 3.2), half = head * 0.6;
        var m = (int)Math.Ceiling(head + t);
        var b = new Int32Rect((int)Math.Floor(Math.Min(x1, x2)) - m, (int)Math.Floor(Math.Min(y1, y2)) - m,
            (int)Math.Ceiling(Math.Abs(x2 - x1)) + 2 * m, (int)Math.Ceiling(Math.Abs(y2 - y1)) + 2 * m);
        Point P(double x, double y) => new(x - b.X, y - b.Y);
        var len = Math.Max(1, Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)));
        double ux = (x2 - x1) / len, uy = (y2 - y1) / len;
        var bxp = x2 - ux * Math.Min(head, len); // base of the head
        var byp = y2 - uy * Math.Min(head, len);
        var g = new Grid { Width = b.Width, Height = b.Height };
        g.Children.Add(new Line
        {
            X1 = P(x1, y1).X, Y1 = P(x1, y1).Y, X2 = P(bxp, byp).X, Y2 = P(bxp, byp).Y,
            Stroke = brush, StrokeThickness = t, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Flat,
        });
        g.Children.Add(new Polygon
        {
            Points = [P(x2, y2), P(bxp - uy * half, byp + ux * half), P(bxp + uy * half, byp - ux * half)],
            Fill = brush, StrokeLineJoin = PenLineJoin.Round, Stroke = brush, StrokeThickness = 1,
        });
        return (g, b);
    }

    /// <summary>Preview of blur/pixelate (the export computes the real one in FFmpeg).</summary>
    private static FrameworkElement Mask(Annotation a, Int32Rect box, Visual? source)
    {
        var holder = new Border { Width = box.Width, Height = box.Height, ClipToBounds = true, Background = Brushes.Gray };
        if (source == null) return holder;
        if (a.Kind == AnnotationKind.Blur)
        {
            // Oversize the blurred copy so its soft edges fall outside the clip instead of revealing the original.
            var r = Exporter.PreviewBlurRadius(box.Width, box.Height);
            var pad = Math.Ceiling(r);
            holder.Child = new Rectangle
            {
                Width = box.Width + 2 * pad, Height = box.Height + 2 * pad, Margin = new Thickness(-pad),
                Fill = new VisualBrush(source) { Viewbox = new Rect(box.X - pad, box.Y - pad, box.Width + 2 * pad, box.Height + 2 * pad), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill },
                Effect = new BlurEffect { Radius = r, KernelType = KernelType.Gaussian },
            };
        }
        else
        {
            var rect = new Rectangle
            {
                Width = box.Width, Height = box.Height,
                Fill = new VisualBrush(source) { Viewbox = new Rect(box.X, box.Y, box.Width, box.Height), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill },
                CacheMode = new BitmapCache(1.0 / Exporter.PixelBlock), // rasterize small, scale up blocky
            };
            RenderOptions.SetBitmapScalingMode(rect, BitmapScalingMode.NearestNeighbor);
            holder.Child = rect;
        }
        return holder;
    }

    /// <summary>Renders a vector annotation to a transparent PNG for the exporter.</summary>
    public static (string Png, int X, int Y) RenderPng(Annotation a, int w, int h, string path)
    {
        var (el, b) = Create(a, w, h);
        el.Measure(new Size(b.Width, b.Height));
        el.Arrange(new Rect(0, 0, b.Width, b.Height));
        var bmp = new RenderTargetBitmap(Math.Max(1, b.Width), Math.Max(1, b.Height), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) enc.Save(fs);
        return (path, b.X, b.Y);
    }
}
