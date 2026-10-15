namespace Soulcrest.App.Services;

/// <summary>
/// Bounded diagnostic window. The caller synchronizes access; Add allocates nothing.
/// Mean and maximum cover all accepted samples; quantiles cover the most recent retained samples.
/// </summary>
internal sealed class LatencyWindow
{
    private readonly double[] _samples;
    private int _next;
    private int _retained;
    private long _count;
    private double _mean;
    private double _maximum;

    internal LatencyWindow(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _samples = new double[capacity];
    }

    internal void Add(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;

        _samples[_next] = milliseconds;
        if (++_next == _samples.Length) _next = 0;
        if (_retained < _samples.Length) _retained++;
        _count++;
        // An online mean avoids overflowing a sum even when accepted values are very large.
        _mean += (milliseconds - _mean) / _count;
        _maximum = Math.Max(_maximum, milliseconds);
    }

    internal LatencySummary TakeSummary()
    {
        if (_count == 0) return default;

        var sorted = new double[_retained];
        Array.Copy(_samples, sorted, _retained);
        Array.Sort(sorted);
        var summary = new LatencySummary(_count, _retained, _mean,
            NearestRank(sorted, 50), NearestRank(sorted, 95), _maximum);

        _next = 0;
        _retained = 0;
        _count = 0;
        _mean = 0;
        _maximum = 0;
        return summary;
    }

    internal LatencySummary Drain() => TakeSummary();

    private static double NearestRank(double[] sorted, int percentile)
    {
        var rank = ((long)sorted.Length * percentile + 99) / 100;
        return sorted[(int)rank - 1];
    }
}

/// <summary>Median and p95 use nearest rank over Retained samples, not interpolated values.</summary>
internal readonly record struct LatencySummary(
    long Count,
    int Retained,
    double MeanMs,
    double MedianMs,
    double P95Ms,
    double MaximumMs);
