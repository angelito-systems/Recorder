using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>Annotations and privacy masks: preview layer (inside the zoom), handles, inspector, timeline row.</summary>
public partial class EditorWindow
{
    private int _selectedAnn = -1;
    private readonly List<(double Start, double End, FrameworkElement El)> _annEls = [];
    private enum AnnGrip { Body, P1, P2 }
    private (AnnGrip Grip, Point Down, Annotation Orig, string Key)? _annDrag;

    private static string KindName(AnnotationKind k) => k switch
    {
        AnnotationKind.Arrow => "Flecha", AnnotationKind.Rectangle => "Rectángulo", AnnotationKind.Ellipse => "Círculo",
        AnnotationKind.Highlight => "Resaltado", AnnotationKind.Blur => "Desenfoque", _ => "Pixelado",
    };

    private void RefreshAnnotations()
    {
        if (_selectedAnn >= P.Annotations.Count) _selectedAnn = -1;
        AnnPanel.IsEnabled = _selectedAnn >= 0;
        if (_selectedAnn >= 0)
        {
            var a = P.Annotations[_selectedAnn];
            AnnInfo.Text = $"{KindName(a.Kind)}: {Fmt(a.Start)} → {Fmt(a.End)}";
            AnnStyle.Visibility = a.IsMask ? Visibility.Collapsed : Visibility.Visible;
            AnnThickness.Value = a.Thickness;
        }
        else AnnInfo.Text = P.Annotations.Count == 0 ? "" : "Selecciona una anotación en la línea de tiempo o en la vista previa.";
        BuildAnnotationLayer();
    }

    private void BuildAnnotationLayer()
    {
        AnnLayer.Children.Clear();
        HandleLayer.Children.Clear();
        _annEls.Clear();
        if (_m == null) return;
        for (var i = 0; i < P.Annotations.Count; i++)
        {
            var a = P.Annotations[i];
            var (el, b) = AnnotationVisual.Create(a, _m.Width, _m.Height, ScreenMedia);
            Canvas.SetLeft(el, b.X);
            Canvas.SetTop(el, b.Y);
            el.Cursor = Cursors.SizeAll;
            var index = i;
            el.MouseLeftButtonDown += (_, e) => BeginAnnDrag(index, AnnGrip.Body, e);
            AnnLayer.Children.Add(el);
            _annEls.Add((a.Start, a.End, el));
        }
        if (_selectedAnn >= 0)
        {
            var a = P.Annotations[_selectedAnn];
            AddHandle(a.X, a.Y, AnnGrip.P1);
            AddHandle(a.X2, a.Y2, AnnGrip.P2);
        }
    }

    private void AddHandle(double nx, double ny, AnnGrip grip)
    {
        var size = Math.Max(12, _m.Height / 60.0);
        var h = new Ellipse { Width = size, Height = size, Fill = Brushes.White, Stroke = (Brush)FindResource("Brand"), StrokeThickness = size / 5, Cursor = Cursors.Cross };
        Canvas.SetLeft(h, nx * _m.Width - size / 2);
        Canvas.SetTop(h, ny * _m.Height - size / 2);
        h.MouseLeftButtonDown += (_, e) => BeginAnnDrag(_selectedAnn, grip, e);
        HandleLayer.Children.Add(h);
    }

    private void ApplyAnnotations(double t)
    {
        foreach (var (s, e, el) in _annEls) el.Visibility = t >= s && t <= e ? Visibility.Visible : Visibility.Collapsed;
        HandleLayer.Visibility = _selectedAnn >= 0 && t >= P.Annotations[_selectedAnn].Start && t <= P.Annotations[_selectedAnn].End
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DrawAnnotationRow()
    {
        for (var i = 0; i < P.Annotations.Count; i++)
        {
            var a = P.Annotations[i];
            if (Span(a.Start, a.End) is not { } sp) continue;
            var sel = i == _selectedAnn;
            var r = new Border
            {
                Width = Math.Max(4, sp.W), Height = RowH, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(a.IsMask ? Color.FromRgb(0x47, 0x55, 0x69) : Color.FromRgb(0xB9, 0x1C, 0x1C)),
                BorderBrush = sel ? Brushes.White : null, BorderThickness = new Thickness(sel ? 1.5 : 0),
                Child = new TextBlock { Text = KindName(a.Kind), Foreground = Brushes.White, FontSize = 10, Margin = new Thickness(5, 3, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(r, sp.X);
            Canvas.SetTop(r, AnnTop);
            Timeline.Children.Add(r);
        }
    }

    // ---------- preview dragging (captured on ZoomHost, which survives layer rebuilds) ----------

    private void BeginAnnDrag(int index, AnnGrip grip, MouseButtonEventArgs e)
    {
        if (index < 0) return;
        if (_selectedAnn != index) Select(ann: index);
        _annDrag = (grip, e.GetPosition(ZoomHost), P.Annotations[index], "drag" + Guid.NewGuid());
        ZoomHost.CaptureMouse();
        e.Handled = true; // not a zoom-center click
    }

    private void OnAnnMove(object sender, MouseEventArgs e)
    {
        if (_annDrag is not { } d || _selectedAnn < 0) return;
        var p = e.GetPosition(ZoomHost);
        double dx = (p.X - d.Down.X) / _m.Width, dy = (p.Y - d.Down.Y) / _m.Height;
        var o = d.Orig;
        static double C(double v) => Math.Round(Math.Clamp(v, 0, 1), 4);
        var a = d.Grip switch
        {
            AnnGrip.P1 => o with { X = C(o.X + dx), Y = C(o.Y + dy) },
            AnnGrip.P2 => o with { X2 = C(o.X2 + dx), Y2 = C(o.Y2 + dy) },
            // Moving keeps the shape whole: clamp the shift so both points stay in frame.
            _ => o with
            {
                X = o.X + Math.Clamp(dx, -Math.Min(o.X, o.X2), 1 - Math.Max(o.X, o.X2)), X2 = o.X2 + Math.Clamp(dx, -Math.Min(o.X, o.X2), 1 - Math.Max(o.X, o.X2)),
                Y = o.Y + Math.Clamp(dy, -Math.Min(o.Y, o.Y2), 1 - Math.Max(o.Y, o.Y2)), Y2 = o.Y2 + Math.Clamp(dy, -Math.Min(o.Y, o.Y2), 1 - Math.Max(o.Y, o.Y2)),
            },
        };
        EditAnn(_ => a, d.Key);
    }

    private void OnAnnUp(object sender, MouseButtonEventArgs e)
    {
        if (_annDrag == null) return;
        _annDrag = null;
        _lastEditKey = null;
        ZoomHost.ReleaseMouseCapture();
    }

    // ---------- inspector ----------

    private void EditAnn(Func<Annotation, Annotation> f, string key)
    {
        if (_selectedAnn < 0 || _refreshing) return;
        var list = P.Annotations.ToList();
        var a = f(list[_selectedAnn]);
        if (a.End - a.Start < 0.1) return;
        list[_selectedAnn] = a;
        Commit(P with { Annotations = list }, key);
    }

    private void OnAddAnn(object sender, RoutedEventArgs e)
    {
        var kind = Enum.Parse<AnnotationKind>((string)((Button)sender).Tag);
        var t = Now;
        var a = kind == AnnotationKind.Arrow
            ? new Annotation(t, Math.Min(_duration, t + 3), kind, 0.3, 0.3, 0.45, 0.45)
            : new Annotation(t, Math.Min(_duration, t + 3), kind, 0.35, 0.35, 0.65, 0.6, kind == AnnotationKind.Highlight ? "#FACC15" : "#EF4444");
        var list = P.Annotations.Append(a).ToList();
        Select(ann: list.Count - 1, refresh: false);
        Commit(P with { Annotations = list }, "addann" + Guid.NewGuid());
    }

    private void OnDeleteAnn(object sender, RoutedEventArgs e)
    {
        if (_selectedAnn < 0) return;
        var list = P.Annotations.Where((_, i) => i != _selectedAnn).ToList();
        Select(refresh: false);
        Commit(P with { Annotations = list }, "delann" + Guid.NewGuid());
    }

    private void OnAnnColor(object sender, RoutedEventArgs e) => EditAnn(a => a with { Color = (string)((Button)sender).Tag }, "anncolor" + Guid.NewGuid());
    private void OnAnnThickness(object sender, RoutedPropertyChangedEventArgs<double> e) => EditAnn(a => a with { Thickness = Math.Round(AnnThickness.Value) }, "annthick");
    private void OnAnnStartHere(object sender, RoutedEventArgs e) { var n = Now; EditAnn(a => a with { Start = n }, "as" + Guid.NewGuid()); }
    private void OnAnnEndHere(object sender, RoutedEventArgs e) { var n = Now; EditAnn(a => a with { End = n }, "ae" + Guid.NewGuid()); }

    /// <summary>Renders vector annotations to PNGs (in <paramref name="dir"/>) for the exporter, keyed by annotation index.</summary>
    private Dictionary<int, (string Png, int X, int Y)> RenderAnnotationImages(string dir)
    {
        var map = new Dictionary<int, (string, int, int)>();
        for (var i = 0; i < P.Annotations.Count; i++)
            if (!P.Annotations[i].IsMask)
                map[i] = AnnotationVisual.RenderPng(P.Annotations[i], _m.Width, _m.Height, System.IO.Path.Combine(dir, $"ann{i}.png"));
        return map;
    }
}
