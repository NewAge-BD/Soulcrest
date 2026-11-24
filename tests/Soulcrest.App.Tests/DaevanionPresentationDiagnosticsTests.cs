using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.App.Capture;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionPresentationDiagnosticsTests
{
    private static DaevanionAnimationHealth Sample(long frames = 12) => new(frames, 2, 1, 1.2, 4, .8, 2, 33, 51, 3.2, 29);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeVisibilityAloneCannotReportHealthyWithoutConfirmedBoardEvidence(bool visible)
    {
        var settings = new SettingsService();
        settings.Current.DaevanionOverlayEnabled = true;
        var runtime = new DiagnosticOverlayRuntime();
        runtime.Observe("daevanion", visible, true, false);
        using var capture = new GameCaptureService();
        var services = new DiagnosticServiceRegistry(settings, runtime, capture);
        var contributor = Assert.Single(DiagnosticContributors.Create(services), item => item.FeatureIds.Contains("overlay-daevanion"));
        var result = contributor.Collect("overlay-daevanion");
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Null(result.Facts["boardSourceCurrent"]);
        Assert.Null(result.Facts["highlightPresentedFrames"]);
        Assert.Null(services.GetService(typeof(DaevanionService)));
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }

    [Fact]
    public void RegistrationAndReleaseNeverActivateTheWindowOrSource()
    {
        using var observation = new DaevanionAnimationObservation();
        Assert.Null(observation.Snapshot);
        var reads = 0;
        using var registration = observation.Register(() => { reads++; return Sample(); });
        Assert.Equal(0, reads);
        Assert.Equal(12, observation.Snapshot!.PresentedFrames);
        Assert.Equal(1, reads);
        registration.Dispose();
        Assert.Null(observation.Snapshot);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void AnOldWindowCannotDetachItsReplacementAndDisposalReleasesTheReader()
    {
        using var observation = new DaevanionAnimationObservation();
        using var first = observation.Register(() => Sample(1));
        using var second = observation.Register(() => Sample(2));
        first.Dispose();
        Assert.Equal(2, observation.Snapshot!.PresentedFrames);
        observation.Dispose();
        Assert.Null(observation.Snapshot);
        Assert.Throws<ObjectDisposedException>(() => observation.Register(() => Sample()));
    }

    [Fact]
    public void MissingPresentationIsUnknownAndKnownPresentationContainsOnlyAnonymousCounters()
    {
        var facts = new List<(string, object?)>();
        DaevanionPresentationDiagnosticFacts.Append(facts, null);
        Assert.Equal(16, facts.Count);
        Assert.All(facts, fact => Assert.Null(fact.Item2));
        facts.Clear();
        DaevanionPresentationDiagnosticFacts.Append(facts, Sample());
        Assert.All(facts, fact => Assert.True(fact.Item2 is long or int or double));
        Assert.Equal(1, facts.Single(f => f.Item1 == "highlightNativeBufferAllocations").Item2);
    }

    [Fact]
    public void InvalidDurationsCannotLeakNonFiniteValuesIntoDiagnosticJson()
    {
        var facts = new List<(string, object?)>();
        DaevanionPresentationDiagnosticFacts.Append(facts, Sample() with
        { LastRasterMilliseconds = double.NaN, LastTransferMilliseconds = double.PositiveInfinity, LastTotalMilliseconds = -1 });
        Assert.Null(facts.Single(f => f.Item1 == "highlightRasterMs").Item2);
        Assert.Null(facts.Single(f => f.Item1 == "highlightTransferMs").Item2);
        Assert.Null(facts.Single(f => f.Item1 == "highlightTotalMs").Item2);
    }
}
