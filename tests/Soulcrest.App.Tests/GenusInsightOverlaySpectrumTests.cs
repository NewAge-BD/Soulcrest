using System.Globalization;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GenusInsightOverlaySpectrumTests
{
    private const string Stat = "test-stat";

    [Fact]
    public void LabelsUseIntervalMidpointsInRankOrderAndLocalizeExactBoundaries()
    {
        var spectrum = new GenusInsightOverlaySpectrum(new()
        {
            StatId = Stat, Unit = "Percent", Minimum = .1m, Maximum = 1.6m,
            RarityOptions = [new(5, Stat, .8m, 1.6m), new(2, Stat, .2m, .6m),
                new(3, Stat, .4m, .8m), new(1, Stat, .1m, .3m), new(4, Stat, .6m, 1.2m)]
        });
        var labels = spectrum.Labels(CultureInfo.GetCultureInfo("de-DE"), 272, Measure);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, labels.Select(label => label.Rank));
        Assert.Equal((.4 - .1) / 1.5, labels[1].Center, 12);
        Assert.Equal((.6 - .1) / 1.5, labels[2].Center, 12);
        Assert.Equal("0,2–0,6", labels[1].Text);
        Assert.Equal("0.2–0.6", spectrum.Labels(CultureInfo.GetCultureInfo("en-US"), 272, Measure)[1].Text);
        Assert.Empty(new GenusInsightOverlaySpectrum(new()).Labels(CultureInfo.InvariantCulture, 272, Measure));
    }

    [Fact]
    public void AllFiveIntervalsUseTheSameScaleWithGapsAndThreeWayOverlap()
    {
        var spectrum = new GenusInsightOverlaySpectrum(new()
        {
            StatId = Stat, Unit = "Points", Minimum = 0, Maximum = 100, Value = 55,
            Rank = 3, IsCurrent = true, RollFraction = .55,
            RarityOptions = [new(5, Stat, 50, 100), new(3, Stat, 30, 60), new(1, Stat, 0, 20),
                new(4, Stat, 40, 80), new(2, Stat, 23, 40)]
        });

        var intervals = Enumerable.Range(1, 5).Select(rank => spectrum.Interval((byte)rank)!.Value).ToArray();
        Assert.Equal(new[] { (0d, .20), (.23, .40), (.30, .60), (.40, .80), (.50, 1d) },
            intervals.Select(interval => (interval.Start, interval.End)));
        Assert.Equal(.55, spectrum.Current);
        Assert.Equal(3, intervals.Count(interval => interval.Start <= spectrum.Current && spectrum.Current <= interval.End));
        Assert.DoesNotContain(intervals, interval => interval.Start <= .21 && .21 <= interval.End);
    }

    [Fact]
    public void MissingRanksKeepTheirLaneAndCoincidentOrVeryNarrowIntervalsKeepTheirBounds()
    {
        var spectrum = new GenusInsightOverlaySpectrum(new()
        {
            StatId = Stat, Unit = "Percent", Minimum = 0, Maximum = 100,
            RarityOptions = [new(1, Stat, 0, 100), new(3, Stat, 0, 100),
                new(4, Stat, 99.9999m, 100), new(5, Stat, 100, 100)]
        });

        Assert.Null(spectrum.Interval(2));
        Assert.Null(spectrum.Current);
        var common = spectrum.Interval(1)!.Value;
        var epic = spectrum.Interval(3)!.Value;
        Assert.Equal((common.Start, common.End), (epic.Start, epic.End));
        Assert.Equal((.999999, 1d), (spectrum.Interval(4)!.Value.Start, spectrum.Interval(4)!.Value.End));
        Assert.Equal((1d, 1d), (spectrum.Interval(5)!.Value.Start, spectrum.Interval(5)!.Value.End));
        var labels = spectrum.Labels(CultureInfo.InvariantCulture, 272, Measure);
        Assert.Equal(new byte[] { 1, 3, 4, 5 }, labels.Select(label => label.Rank));
        Assert.Equal(.5, labels[0].Center);
        Assert.Equal(.5, labels[1].Center);
        Assert.NotEqual(labels[0].Row, labels[1].Row);
        Assert.Equal(1, labels[^1].Center);
        Assert.NotEqual(labels[^2].Row, labels[^1].Row);
    }

    [Fact]
    public void AOneValueScaleKeepsItsEndpointAndOnlyMarksAConfirmedRoll()
    {
        var slot = new GenusInsightSlotView
        {
            StatId = Stat, Unit = "Points", Minimum = 8, Maximum = 8, Value = 8,
            Rank = 2, RollFraction = 1d, IsCurrent = true, RarityOptions = [new(2, Stat, 8, 8)]
        };
        var spectrum = new GenusInsightOverlaySpectrum(slot);

        Assert.Equal(1d, spectrum.Current);
        Assert.Equal((1d, 1d), (spectrum.Interval(2)!.Value.Start, spectrum.Interval(2)!.Value.End));
        var label = Assert.Single(spectrum.Labels(CultureInfo.InvariantCulture, 272, Measure));
        Assert.Equal(1, label.Center);
        Assert.Null(new GenusInsightOverlaySpectrum(slot with { IsCurrent = false }).Current);
        Assert.Null(new GenusInsightOverlaySpectrum(slot with { RollFraction = double.NaN }).Current);
        Assert.Null(new GenusInsightOverlaySpectrum(slot with { Value = 9 }).Current);
    }

    private static double Measure(string text) => text.Length * 7;
}
