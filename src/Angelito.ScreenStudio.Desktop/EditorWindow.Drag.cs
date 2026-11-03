using System.Windows;
using System.Windows.Input;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>Timeline mouse: drag bodies to move, edges to trim; magnetic snapping (hold Alt to disable); click to seek.</summary>
public partial class EditorWindow
{
    private enum Row { Segment, Zoom, Text, Cue, Annotation }

    private sealed record DragState(Row Row, int Index, DragMode Mode, double X0, double T0, double S0, double E0, string Key)
    {
        public bool Moved { get; set; }
    }

    private const double EdgePx = 6, MinLen = 0.2;
    private DragState? _drag;

    private double PxToTime => _view.Length / Math.Max(1, Timeline.ActualWidth);
    private double TimeAt(double x) => _view.ToTime(x, Timeline.ActualWidth);

    private IReadOnlyList<(double S, double E)> Items(Row r) => r switch
    {
        Row.Segment => P.Segments.Select(s => (s.Start, s.End)).ToList(),
        Row.Zoom => P.Zooms.Select(z => (z.Start, z.End)).ToList(),
        Row.Text => P.Texts.Select(t => (t.Start, t.End)).ToList(),
        Row.Annotation => P.Annotations.Select(a => (a.Start, a.End)).ToList(),
        _ => P.Subtitles.Select(c => (c.Start, c.End)).ToList(),
    };

    private StudioProject With(Row r, int i, double s, double e)
    {
        switch (r)
        {
            case Row.Segment: { var l = P.Segments.ToList(); l[i] = new Segment(s, e); return P with { Segments = l }; }
            case Row.Zoom: { var l = P.Zooms.ToList(); l[i] = l[i] with { Start = s, End = e }; return P with { Zooms = l }; }
            case Row.Text: { var l = P.Texts.ToList(); l[i] = l[i] with { Start = s, End = e }; return P with { Texts = l }; }
            case Row.Annotation: { var l = P.Annotations.ToList(); l[i] = l[i] with { Start = s, End = e }; return P with { Annotations = l }; }
            default: { var l = P.Subtitles.ToList(); l[i] = l[i] with { Start = s, End = e }; return P with { Subtitles = l }; }
        }
    }

    /// <summary>Clips, zooms and subtitles may not overlap their neighbors (lists are kept in time order); texts and annotations can stack.</summary>
    private (double Lo, double Hi) Bounds(Row r, int i)
    {
        if (r is Row.Text or Row.Annotation) return (0, _duration);
        var items = Items(r);
        return (i > 0 ? items[i - 1].E : 0, i < items.Count - 1 ? items[i + 1].S : _duration);
    }

    private Row? RowAt(double y) => !_advanced && y >= TextTop && y <= AnnTop + RowH ? null : RowAtAll(y); // hidden rows aren't editable in simple mode

    private static Row? RowAtAll(double y) =>
        y >= SegTop && y <= SegTop + SegH ? Row.Segment :
        y >= ZoomTop && y <= ZoomTop + ZoomH ? Row.Zoom :
        y >= TextTop && y <= TextTop + RowH ? Row.Text :
        y >= CueTop && y <= CueTop + RowH ? Row.Cue :
        y >= AnnTop && y <= AnnTop + RowH ? Row.Annotation : null;

    private (Row Row, int Index, DragMode? Mode)? HitTest(Point p)
    {
        if (RowAt(p.Y) is not { } row) return null;
        var items = Items(row);
        for (var i = items.Count - 1; i >= 0; i--) // topmost (last drawn) first
        {
            double xs = _view.ToX(items[i].S, Timeline.ActualWidth), xe = _view.ToX(items[i].E, Timeline.ActualWidth);
            if (p.X < xs - EdgePx || p.X > xe + EdgePx) continue;
            var wide = xe - xs > 3 * EdgePx; // tiny blocks: the whole block moves
            if (wide && Math.Abs(p.X - xs) <= EdgePx) return (row, i, DragMode.Start);
            if (wide && Math.Abs(p.X - xe) <= EdgePx) return (row, i, DragMode.End);
            if (p.X >= xs && p.X <= xe) return (row, i, row == Row.Segment ? null : DragMode.Move); // clips trim, they don't slide
        }
        return (row, -1, null);
    }

    private List<double> SnapPoints(Row row, int index)
    {
        var pts = new List<double> { 0, _duration, Now };
        foreach (var r in Enum.GetValues<Row>())
        {
            var items = Items(r);
            for (var i = 0; i < items.Count; i++)
                if (r != row || i != index) { pts.Add(items[i].S); pts.Add(items[i].E); }
        }
        return pts;
    }

    private void OnTimelineDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(Timeline);
        var hit = HitTest(pos);
        Timeline.CaptureMouse();
        if (hit is { Index: >= 0 } h)
        {
            switch (h.Row)
            {
                case Row.Zoom: Select(zoom: h.Index); break;
                case Row.Text: Select(text: h.Index); break;
                case Row.Cue: Select(cue: h.Index); break;
                case Row.Annotation: Select(ann: h.Index); break;
            }
            if (h.Mode is { } mode)
            {
                var (s, en) = Items(h.Row)[h.Index];
                _drag = new DragState(h.Row, h.Index, mode, pos.X, TimeAt(pos.X), s, en, "drag" + Guid.NewGuid());
                return; // seek happens on release if the mouse didn't move
            }
        }
        else if (hit is { Row: not Row.Segment }) Select();
        _scrubbing = true;
        Seek(TimeAt(pos.X));
    }

    private void OnTimelineMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Timeline);
        if (_drag is { Moved: true } || _scrubbing) AutoScroll(pos.X);
        if (_drag is { } d)
        {
            if (!d.Moved && Math.Abs(pos.X - d.X0) < 3) return;
            d.Moved = true;
            var (lo, hi) = Bounds(d.Row, d.Index);
            var tol = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? 0 : EdgePx * PxToTime;
            var (s, en) = TimelineEdit.Drag(d.S0, d.E0, d.Mode, TimeAt(pos.X) - d.T0, lo, hi, MinLen, SnapPoints(d.Row, d.Index), tol);
            Commit(With(d.Row, d.Index, Math.Round(s, 3), Math.Round(en, 3)), d.Key); // one key per drag = one undo step
            if (d.Mode != DragMode.Move) Seek(d.Mode == DragMode.Start ? s : en); // show the frame at the edge being trimmed
            return;
        }
        if (_scrubbing) { Seek(TimeAt(pos.X)); return; }
        Timeline.Cursor = HitTest(pos) switch
        {
            { Mode: DragMode.Start or DragMode.End } => Cursors.SizeWE,
            { Mode: DragMode.Move } => Cursors.SizeAll,
            _ => null,
        };
    }

    private void OnTimelineWheel(object sender, MouseWheelEventArgs e)
    {
        var steps = e.Delta / 120.0;
        _view = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            ? _view.ZoomAt(TimeAt(e.GetPosition(Timeline).X), Math.Pow(1.25, steps), _duration)
            : _view.ScrollBy(-steps * _view.Length * 0.15, _duration);
        DrawTimeline();
        e.Handled = true;
    }

    /// <summary>Zooms around the playhead when it's visible, otherwise around the center.</summary>
    private void ZoomTimeline(double factor)
    {
        var anchor = Now >= _view.Start && Now <= _view.End ? Now : _view.Start + _view.Length / 2;
        _view = _view.ZoomAt(anchor, factor, _duration);
        DrawTimeline();
    }

    private void RevealPlayhead()
    {
        var f = _view.Follow(Now, _duration);
        if (f != _view) { _view = f; DrawTimeline(); }
    }

    /// <summary>Scrolls while dragging within 24 px of either edge (or past it, the mouse is captured).</summary>
    // shortcut: only scrolls on mouse moves, so holding still at the edge stops; add a timer if users ask.
    private void AutoScroll(double x)
    {
        const double Edge = 24;
        var w = Timeline.ActualWidth;
        var dir = x < Edge ? -1 : x > w - Edge ? 1 : 0;
        if (dir == 0) return;
        var v = _view.ScrollBy(dir * _view.Length * 0.03, _duration);
        if (v != _view) { _view = v; DrawTimeline(); }
    }

    private void OnTimelineScroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
    {
        _view = (_view with { Start = e.NewValue }).Clamped(_duration);
        DrawTimeline();
    }

    private void OnZoomInTimeline(object sender, RoutedEventArgs e) => ZoomTimeline(1.5);
    private void OnZoomOutTimeline(object sender, RoutedEventArgs e) => ZoomTimeline(1 / 1.5);
    private void OnFitTimeline(object sender, RoutedEventArgs e) { _view = TimelineView.Fit(_duration); DrawTimeline(); }

    private void OnTimelineUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is { Moved: false }) Seek(TimeAt(e.GetPosition(Timeline).X));
        _drag = null;
        _scrubbing = false;
        _lastEditKey = null;
        Timeline.ReleaseMouseCapture();
    }
}
