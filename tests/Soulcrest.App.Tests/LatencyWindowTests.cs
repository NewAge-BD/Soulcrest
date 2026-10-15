using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LatencyWindowTests
{
    [Fact]
    public void EmptyWindowHasZeroSummary()
    {
        var window = new LatencyWindow();

        Assert.Equal(default, window.TakeSummary());
        Assert.Equal(default, window.Drain());
    }

    [Fact]
    public void QuantilesUseExactNearestRankWithoutInterpolation()
    {
        var window = new LatencyWindow();
        for (var value = 20; value >= 1; value--) window.Add(value);

        var summary = window.TakeSummary();

        Assert.Equal(20, summary.Count);
        Assert.Equal(20, summary.Retained);
        Assert.Equal(10.5, summary.MeanMs, 10);
        Assert.Equal(10, summary.MedianMs);
        Assert.Equal(19, summary.P95Ms);
        Assert.Equal(20, summary.MaximumMs);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 2, 3)]
    [InlineData(21, 11, 20)]
    [InlineData(101, 51, 96)]
    public void QuantilesRoundRankUpForSmallAndUnevenWindows(int count, double median, double p95)
    {
        var window = new LatencyWindow();
        for (var value = 1; value <= count; value++) window.Add(value);

        var summary = window.Drain();

        Assert.Equal(median, summary.MedianMs);
        Assert.Equal(p95, summary.P95Ms);
    }

    [Fact]
    public void OverflowRetainsLatestSamplesButMeanAndMaximumCoverWholeWindow()
    {
        var window = new LatencyWindow(capacity: 3);
        foreach (var value in new[] { 100d, 20, 30, 1, 3, 2 }) window.Add(value);

        var summary = window.Drain();

        Assert.Equal(6, summary.Count);
        Assert.Equal(3, summary.Retained);
        Assert.Equal(26, summary.MeanMs, 10);
        Assert.Equal(2, summary.MedianMs);
        Assert.Equal(3, summary.P95Ms);
        Assert.Equal(100, summary.MaximumMs);
    }

    [Fact]
    public void DrainResetsAllStatisticsAndDoesNotLeakPreviousRingContents()
    {
        var window = new LatencyWindow(capacity: 2);
        window.Add(100);
        window.Add(200);
        window.Add(300);
        window.TakeSummary();

        Assert.Equal(default, window.Drain());
        window.Add(7);
        Assert.Equal(new LatencySummary(1, 1, 7, 7, 7, 7), window.TakeSummary());
        Assert.Equal(default, window.TakeSummary());
    }

    [Fact]
    public void InvalidSamplesAreIgnoredAndZeroIsAccepted()
    {
        var window = new LatencyWindow(capacity: 2);
        window.Add(double.NaN);
        window.Add(double.PositiveInfinity);
        window.Add(double.NegativeInfinity);
        window.Add(-1);
        Assert.Equal(default, window.TakeSummary());

        window.Add(0);
        window.Add(-0.1);
        window.Add(double.NaN);
        window.Add(4);

        Assert.Equal(new LatencySummary(2, 2, 2, 0, 4, 4), window.Drain());
    }

    [Fact]
    public void VeryLargeFiniteValuesDoNotOverflowMean()
    {
        var window = new LatencyWindow();
        window.Add(double.MaxValue);
        window.Add(double.MaxValue);

        var summary = window.Drain();

        Assert.Equal(2, summary.Count);
        Assert.Equal(double.MaxValue, summary.MeanMs);
        Assert.Equal(double.MaxValue, summary.MaximumMs);
    }

    [Fact]
    public void CapacityOneStillKeepsWholeWindowMeanAndMaximum()
    {
        var window = new LatencyWindow(capacity: 1);
        window.Add(6);
        window.Add(2);
        window.Add(1);

        Assert.Equal(new LatencySummary(3, 1, 3, 1, 1, 6), window.Drain());
    }

    [Fact]
    public void AddingSamplesDoesNotAllocateWhenTheRingWraps()
    {
        var window = new LatencyWindow(capacity: 8);
        for (var i = 0; i < 32; i++) window.Add(i);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) window.Add(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCapacityIsRejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyWindow(capacity));
    }
}
