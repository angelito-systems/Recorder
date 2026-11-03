using System.Windows;
using System.Windows.Input;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>Timeline mouse: drag bodies to move, edges to trim; magnetic snapping (hold Alt to disable); click to seek.</summary>
public partial class EditorWindow
{
    private enum Row { Segment, Zoom, Text, Cue }

    private sealed record DragState(Row Row, int Index, DragMode Mode, double X0, double S0, double E0, string Key)
    {
        public bool Moved { get; set; }
    }

    private const double EdgePx = 6, MinLen = 0.2;
    private DragState? _drag;

    private double PxToTime => _duration / Math.Max(1, Timeline.ActualWidth);

    private IReadOnlyList<(double S, double E)> Items(Row r) => r switch
    {
        Row.Segment => P.Segments.Select(s => (s.Start, s.End)).ToList(),
        Row.Zoom => P.Zooms.Select(z => (z.Start, z.End)).ToList(),
        Row.Text => P.Texts.Select(t => (t.Start, t.End)).ToList(),
        _ => P.Subtitles.Select(c => (c.Start, c.End)).ToList(),
    };

    private StudioProject With(Row r, int i, double s, double e)
    {
        switch (r)
        {
            case Row.Segment: { var l = P.Segments.ToList(); l[i] = new Segment(s, e); return P with { Segments = l }; }
            case Row.Zoom: { var l = P.Zooms.ToList(); l[i] = l[i] with { Start = s, End = e }; return P with { Zooms = l }; }
            case Row.Text: { var l = P.Texts.ToList(); l[i] = l[i] with { Start = s, End = e }; return P with { Texts = l }; }
            default: { var l = P.Subtitles.ToList(); l[i] = l[i] with { Start = s, End = e }; return P with { Subtitles = l }; }
        }
    }

    /// <summary>Clips, zooms and subtitles may not overlap their neighbors (lists are kept in time order); texts can stack.</summary>
    private (double Lo, double Hi) Bounds(Row r, int i)
    {
        if (r == Row.Text) return (0, _duration);
        var items = Items(r);
        return (i > 0 ? items[i - 1].E : 0, i < items.Count - 1 ? items[i + 1].S : _duration);
    }

    private static Row? RowAt(double y) =>
        y >= SegTop && y <= SegTop + SegH ? Row.Segment :
        y >= ZoomTop && y <= ZoomTop + ZoomH ? Row.Zoom :
        y >= TextTop && y <= TextTop + RowH ? Row.Text :
        y >= CueTop && y <= CueTop + RowH ? Row.Cue : null;

    private (Row Row, int Index, DragMode? Mode)? HitTest(Point p)
    {
        if (RowAt(p.Y) is not { } row) return null;
        var items = Items(row);
        var k = 1 / PxToTime;
        for (var i = items.Count - 1; i >= 0; i--) // topmost (last drawn) first
        {
            double xs = items[i].S * k, xe = items[i].E * k;
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
            }
            if (h.Mode is { } mode)
            {
                var (s, en) = Items(h.Row)[h.Index];
                _drag = new DragState(h.Row, h.Index, mode, pos.X, s, en, "drag" + Guid.NewGuid());
                return; // seek happens on release if the mouse didn't move
            }
        }
        else if (hit is { Row: not Row.Segment }) Select();
        _scrubbing = true;
        Seek(pos.X * PxToTime);
    }

    private void OnTimelineMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Timeline);
        if (_drag is { } d)
        {
            if (!d.Moved && Math.Abs(pos.X - d.X0) < 3) return;
            d.Moved = true;
            var (lo, hi) = Bounds(d.Row, d.Index);
            var tol = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? 0 : EdgePx * PxToTime;
            var (s, en) = TimelineEdit.Drag(d.S0, d.E0, d.Mode, (pos.X - d.X0) * PxToTime, lo, hi, MinLen, SnapPoints(d.Row, d.Index), tol);
            Commit(With(d.Row, d.Index, Math.Round(s, 3), Math.Round(en, 3)), d.Key); // one key per drag = one undo step
            if (d.Mode != DragMode.Move) Seek(d.Mode == DragMode.Start ? s : en); // show the frame at the edge being trimmed
            return;
        }
        if (_scrubbing) { Seek(pos.X * PxToTime); return; }
        Timeline.Cursor = HitTest(pos) switch
        {
            { Mode: DragMode.Start or DragMode.End } => Cursors.SizeWE,
            { Mode: DragMode.Move } => Cursors.SizeAll,
            _ => null,
        };
    }

    private void OnTimelineUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is { Moved: false }) Seek(e.GetPosition(Timeline).X * PxToTime);
        _drag = null;
        _scrubbing = false;
        _lastEditKey = null;
        Timeline.ReleaseMouseCapture();
    }
}
