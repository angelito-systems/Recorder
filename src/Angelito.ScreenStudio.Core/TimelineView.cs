namespace Angelito.ScreenStudio.Core;

/// <summary>The visible time window of the timeline: [Start, Start + Length] seconds, always inside [0, duration].</summary>
public readonly record struct TimelineView(double Start, double Length)
{
    public const double MinLength = 0.25;
    public double End => Start + Length;

    public static TimelineView Fit(double duration) => new(0, Math.Max(MinLength, duration));

    public double ToX(double t, double width) => (t - Start) / Length * width;
    public double ToTime(double x, double width) => Start + x / Math.Max(1, width) * Length;

    /// <summary>Zooms by <paramref name="factor"/> (&gt;1 = closer) keeping <paramref name="anchor"/> at the same screen position.</summary>
    public TimelineView ZoomAt(double anchor, double factor, double duration)
    {
        var len = Math.Clamp(Length / factor, Math.Min(MinLength, duration), Math.Max(MinLength, duration));
        var rel = Length > 0 ? (anchor - Start) / Length : 0.5;
        return new TimelineView(anchor - rel * len, len).Clamped(duration);
    }

    public TimelineView ScrollBy(double seconds, double duration) => (this with { Start = Start + seconds }).Clamped(duration);

    /// <summary>Pages the view so t is visible, leaving a little context before it. No change if already visible.</summary>
    public TimelineView Follow(double t, double duration) =>
        t >= Start && t <= End ? this : (this with { Start = t - Length * 0.1 }).Clamped(duration);

    public TimelineView Clamped(double duration)
    {
        var len = Math.Min(Length, Math.Max(MinLength, duration));
        return new TimelineView(Math.Clamp(Start, 0, Math.Max(0, duration - len)), len);
    }
}
