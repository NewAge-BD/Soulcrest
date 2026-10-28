namespace Soulcrest.App.Services;

/// <summary>A linear view of recorded session time. Zoom and pan never change its clock or omit gaps.</summary>
public sealed class LootTrackerTimelineViewport
{
    private double _center = .5;
    public TimeSpan Duration { get; private set; }
    public double Zoom { get; private set; } = 1;
    public TimeSpan From { get; private set; }
    public TimeSpan To { get; private set; }
    public TimeSpan Window => To - From;
    public double MaximumZoom => Math.Min(120, Math.Max(1, Duration.TotalSeconds));

    public void UpdateDuration(TimeSpan duration, bool reset = false)
    {
        Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        if (reset) { Zoom = 1; _center = .5; }
        Normalize();
    }
    public void Reset() { Zoom = 1; _center = .5; Normalize(); }
    public void ZoomBy(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        CenterOnWindow(); Zoom = Math.Clamp(Zoom * factor, 1, MaximumZoom); Normalize();
    }
    public void Pan(double windows)
    {
        if (!double.IsFinite(windows) || Duration <= TimeSpan.Zero) return;
        CenterOnWindow(); _center += windows / Zoom; Normalize();
    }
    public void PanPixels(double pixels, double width)
    {
        if (!double.IsFinite(pixels) || !double.IsFinite(width) || width <= 0) return;
        Pan(pixels / width);
    }
    public void EnsureVisible(TimeSpan at)
    {
        if (Duration <= TimeSpan.Zero || at >= From && at <= To) return;
        _center = Math.Clamp(at.TotalSeconds / Duration.TotalSeconds, 0, 1); Normalize();
    }
    public double Fraction(TimeSpan at) => Window <= TimeSpan.Zero ? 0 : (at - From).TotalSeconds / Window.TotalSeconds;
    public TimeSpan At(double fraction)
    {
        if (!double.IsFinite(fraction)) return From;
        return TimeSpan.FromTicks(From.Ticks + (long)(Window.Ticks * (decimal)Math.Clamp(fraction, 0, 1)));
    }
    public IReadOnlyList<TimeSpan> Ticks()
    {
        if (Window <= TimeSpan.Zero) return [TimeSpan.Zero];
        double[] steps = [.1, .25, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600, 7200];
        var step = steps.FirstOrDefault(value => Window.TotalSeconds / value <= 8, Math.Max(7200, Window.TotalSeconds / 8));
        var result = new List<TimeSpan>();
        for (var second = Math.Ceiling(From.TotalSeconds / step) * step; second <= To.TotalSeconds && result.Count < 10; second += step)
            result.Add(TimeSpan.FromSeconds(second));
        return result;
    }
    private void CenterOnWindow() => _center = Duration <= TimeSpan.Zero ? .5 : (From.TotalSeconds + Window.TotalSeconds / 2) / Duration.TotalSeconds;
    private void Normalize()
    {
        Zoom = Math.Clamp(Zoom, 1, MaximumZoom); _center = Math.Clamp(_center, 0, 1);
        var visible = (long)(Duration.Ticks / (decimal)Zoom);
        var from = Math.Clamp((decimal)_center * Duration.Ticks - visible / 2m, 0, Math.Max(0, Duration.Ticks - visible));
        From = TimeSpan.FromTicks((long)from); To = TimeSpan.FromTicks(Math.Min(Duration.Ticks, From.Ticks + visible));
    }
}
