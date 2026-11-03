using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Angelito.ScreenStudio.Core;
using Microsoft.Win32;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>Texts and subtitles: preview layer, timeline rows and inspector. Layout mirrors Exporter's drawtext.</summary>
public partial class EditorWindow
{
    private int _selectedText = -1, _selectedCue = -1;
    private readonly List<(double Start, double End, FrameworkElement El)> _textEls = [];
    private (FrameworkElement El, Point Grab, int Text, bool Cue)? _textDrag;

    /// <summary>Single selection across zooms, texts and subtitles.</summary>
    private void Select(int zoom = -1, int text = -1, int cue = -1, bool refresh = true)
    {
        _selectedZoom = zoom;
        _selectedText = text;
        _selectedCue = cue;
        if (refresh) Refresh();
    }

    private void RefreshText()
    {
        var p = P;
        if (_selectedText >= p.Texts.Count) _selectedText = -1;
        if (_selectedCue >= p.Subtitles.Count) _selectedCue = -1;

        TextPanel.IsEnabled = DelTextBtn.IsEnabled = _selectedText >= 0;
        if (_selectedText >= 0)
        {
            var t = p.Texts[_selectedText];
            if (TextContent.Text != t.Text) TextContent.Text = t.Text; // don't reset the caret while typing
            TextSize.Value = t.Size;
            TextBg.IsChecked = t.Background;
            TextBold.IsChecked = t.Bold;
        }
        else TextContent.Text = "";

        CuePanel.IsEnabled = DelCueBtn.IsEnabled = _selectedCue >= 0;
        if (_selectedCue >= 0)
        {
            var c = p.Subtitles[_selectedCue];
            if (CueContent.Text != c.Text) CueContent.Text = c.Text;
            CueInfo.Text = $"Subtítulo {_selectedCue + 1} de {p.Subtitles.Count}: {Fmt(c.Start)} → {Fmt(c.End)}";
        }
        else
        {
            CueContent.Text = "";
            CueInfo.Text = p.Subtitles.Count == 0 ? "Sin subtítulos." : $"{p.Subtitles.Count} subtítulos. Selecciona uno en la línea de tiempo.";
        }
        var s = p.SubtitleStyle;
        SubSize.Value = s.Size;
        SubY.Value = s.Y;
        SubBg.IsChecked = s.Background;
        SubBurn.IsChecked = s.Burn;

        BuildTextLayer();
    }

    private void BuildTextLayer()
    {
        TextLayer.Children.Clear();
        _textEls.Clear();
        if (_m == null) return;
        for (var i = 0; i < P.Texts.Count; i++)
        {
            var t = P.Texts[i];
            AddTextEl(t.Start, t.End, t.Text, t.X, t.Y, t.Size, t.Color, t.Background, t.Bold, i, false, i == _selectedText);
        }
        var st = P.SubtitleStyle;
        for (var i = 0; i < P.Subtitles.Count; i++)
        {
            var c = P.Subtitles[i];
            AddTextEl(c.Start, c.End, c.Text, 0.5, st.Y, st.Size, st.Color, st.Background, false, i, true, i == _selectedCue);
        }
    }

    private void AddTextEl(double start, double end, string text, double cx, double cy, double size, string color, bool box, bool bold, int index, bool cue, bool selected)
    {
        text = text.Replace("\r\n", "\n").Trim();
        if (text.Length == 0) return;
        var px = Math.Max(8, Math.Round(size * _m.Height));
        var el = new Border
        {
            Padding = new Thickness(Exporter.TextPad((int)px)),
            Background = box ? new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)) : Brushes.Transparent,
            BorderBrush = selected ? (Brush)FindResource("Brand") : null,
            BorderThickness = new Thickness(selected ? 2 : 0),
            Cursor = cue ? Cursors.SizeNS : Cursors.SizeAll,
            Child = new TextBlock
            {
                Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = px, TextAlignment = TextAlignment.Center,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            },
        };
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(el, cx * _m.Width - el.DesiredSize.Width / 2);
        Canvas.SetTop(el, cy * _m.Height - el.DesiredSize.Height / 2);
        el.MouseLeftButtonDown += (_, e) =>
        {
            // Refresh is deferred to mouse-up: it rebuilds the layer and would drop the captured element.
            if (cue) Select(cue: index, refresh: false); else Select(text: index, refresh: false);
            _textDrag = (el, e.GetPosition(el), index, cue);
            _lastEditKey = null;
            el.CaptureMouse();
            e.Handled = true;
        };
        el.MouseMove += (_, e) => DragText(e);
        el.MouseLeftButtonUp += (_, _) => EndTextDrag();
        TextLayer.Children.Add(el);
        _textEls.Add((start, end, el));
    }

    private void DragText(MouseEventArgs e)
    {
        if (_textDrag is not { } d || !d.El.IsMouseCaptured) return;
        var p = e.GetPosition(Stage);
        double cx = (p.X - d.Grab.X + d.El.ActualWidth / 2) / _m.Width, cy = (p.Y - d.Grab.Y + d.El.ActualHeight / 2) / _m.Height;
        cx = Math.Round(Math.Clamp(cx, 0, 1), 4);
        cy = Math.Round(Math.Clamp(cy, 0, 1), 4);
        Canvas.SetLeft(d.El, cx * _m.Width - d.El.ActualWidth / 2);
        Canvas.SetTop(d.El, cy * _m.Height - d.El.ActualHeight / 2);
        _textDragPos = (cx, cy);
    }

    private (double X, double Y)? _textDragPos;

    private void EndTextDrag()
    {
        if (_textDrag is not { } d) return;
        d.El.ReleaseMouseCapture();
        _textDrag = null;
        if (_textDragPos is not { } pos) { Refresh(); return; }
        _textDragPos = null;
        if (d.Cue) Commit(P with { SubtitleStyle = P.SubtitleStyle with { Y = pos.Y } }, "subdrag" + Guid.NewGuid());
        else EditText(t => t with { X = pos.X, Y = pos.Y }, "textdrag" + Guid.NewGuid());
    }

    private void ApplyText(double t)
    {
        foreach (var (s, e, el) in _textEls) el.Visibility = t >= s && t <= e ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DrawTextRows(Func<double, double> x)
    {
        void Row(double top, double s, double e, string label, Color color, bool sel)
        {
            var r = new Border
            {
                Width = Math.Max(4, x(e) - x(s)), Height = RowH, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color), BorderBrush = sel ? Brushes.White : null, BorderThickness = new Thickness(sel ? 1.5 : 0),
                Child = new TextBlock { Text = label.Replace('\n', ' '), Foreground = Brushes.White, FontSize = 10, Margin = new Thickness(5, 3, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(r, x(s));
            Canvas.SetTop(r, top);
            Timeline.Children.Add(r);
        }
        for (var i = 0; i < P.Texts.Count; i++) Row(TextTop, P.Texts[i].Start, P.Texts[i].End, P.Texts[i].Text, Color.FromRgb(0x16, 0x83, 0x4A), i == _selectedText);
        for (var i = 0; i < P.Subtitles.Count; i++) Row(CueTop, P.Subtitles[i].Start, P.Subtitles[i].End, P.Subtitles[i].Text, Color.FromRgb(0xC2, 0x4D, 0x0C), i == _selectedCue);
    }

    // ---------- text edits ----------

    private void EditText(Func<TextItem, TextItem> f, string key)
    {
        if (_selectedText < 0 || _refreshing) return;
        var list = P.Texts.ToList();
        var t = f(list[_selectedText]);
        if (t.End - t.Start < 0.1) return;
        list[_selectedText] = t;
        Commit(P with { Texts = list }, key);
    }

    private void OnAddText(object sender, RoutedEventArgs e)
    {
        var t = Now;
        var list = P.Texts.Append(new TextItem(t, Math.Min(_duration, t + 3), "Título")).ToList();
        Select(text: list.Count - 1, refresh: false);
        Commit(P with { Texts = list }, "addtext" + Guid.NewGuid());
        TextContent.Focus();
        TextContent.SelectAll();
    }

    private void OnDeleteText(object sender, RoutedEventArgs e)
    {
        if (_selectedText < 0) return;
        var list = P.Texts.Where((_, i) => i != _selectedText).ToList();
        Select(refresh: false);
        Commit(P with { Texts = list }, "deltext" + Guid.NewGuid());
    }

    private void OnTextEdit(object sender, RoutedEventArgs e) =>
        EditText(t => t with { Text = TextContent.Text, Size = Math.Round(TextSize.Value, 3), Background = TextBg.IsChecked == true, Bold = TextBold.IsChecked == true },
            sender == TextContent ? "textcontent" : "text" + sender.GetHashCode());

    private void OnTextColor(object sender, RoutedEventArgs e) => EditText(t => t with { Color = (string)((Button)sender).Tag }, "textcolor" + Guid.NewGuid());
    private void OnTextStartHere(object sender, RoutedEventArgs e) { var n = Now; EditText(t => t with { Start = n }, "ts" + Guid.NewGuid()); }
    private void OnTextEndHere(object sender, RoutedEventArgs e) { var n = Now; EditText(t => t with { End = n }, "te" + Guid.NewGuid()); }

    // ---------- subtitle edits ----------

    private void EditCue(Func<Cue, Cue> f, string key)
    {
        if (_selectedCue < 0 || _refreshing) return;
        var list = P.Subtitles.ToList();
        var c = f(list[_selectedCue]);
        if (c.End - c.Start < 0.1) return;
        list[_selectedCue] = c;
        Commit(P with { Subtitles = list }, key);
    }

    private void OnAddCue(object sender, RoutedEventArgs e)
    {
        var t = Now;
        var next = P.Subtitles.Where(c => c.Start > t).Select(c => c.Start).DefaultIfEmpty(_duration).Min();
        var end = Math.Min(Math.Min(t + 2, next), _duration);
        if (end - t < 0.2 || P.Subtitles.Any(c => t >= c.Start && t < c.End)) return; // no overlapping cues
        var list = P.Subtitles.Append(new Cue(t, end, "Subtítulo")).OrderBy(c => c.Start).ToList();
        Select(cue: list.FindIndex(c => c.Start == t), refresh: false);
        Commit(P with { Subtitles = list }, "addcue" + Guid.NewGuid());
        CueContent.Focus();
        CueContent.SelectAll();
    }

    private void OnDeleteCue(object sender, RoutedEventArgs e)
    {
        if (_selectedCue < 0) return;
        var list = P.Subtitles.Where((_, i) => i != _selectedCue).ToList();
        Select(refresh: false);
        Commit(P with { Subtitles = list }, "delcue" + Guid.NewGuid());
    }

    private void OnCueEdit(object sender, RoutedEventArgs e) => EditCue(c => c with { Text = CueContent.Text }, "cuecontent");
    private void OnCueStartHere(object sender, RoutedEventArgs e) { var n = Now; EditCue(c => c with { Start = n }, "cs" + Guid.NewGuid()); }
    private void OnCueEndHere(object sender, RoutedEventArgs e) { var n = Now; EditCue(c => c with { End = n }, "ce" + Guid.NewGuid()); }

    private void OnSubStyleEdit(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        Commit(P with
        {
            SubtitleStyle = P.SubtitleStyle with
            {
                Size = Math.Round(SubSize.Value, 3), Y = Math.Round(SubY.Value, 3), Background = SubBg.IsChecked == true, Burn = SubBurn.IsChecked == true,
            },
        }, "substyle" + sender.GetHashCode());
    }

    private void OnImportSrt(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Subtítulos SubRip|*.srt" };
        if (dlg.ShowDialog(this) != true) return;
        if (P.Subtitles.Count > 0 && MessageBox.Show(this, "¿Reemplazar los subtítulos actuales?", App.Product, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            var next = P.ImportSrt(File.ReadAllText(dlg.FileName));
            if (next.Subtitles.Count == 0) { MessageBox.Show(this, "El archivo no contiene subtítulos válidos.", App.Product); return; }
            Select(refresh: false);
            Commit(next, "importsrt" + Guid.NewGuid());
        }
        catch (IOException ex) { MessageBox.Show(this, ex.Message, App.Product); }
    }

    private void OnExportSrt(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Subtítulos SubRip|*.srt", FileName = ProjectName.Text + ".srt" };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, P.ExportSrt()); }
        catch (IOException ex) { MessageBox.Show(this, ex.Message, App.Product); }
    }
}
