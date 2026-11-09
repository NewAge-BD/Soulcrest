using System.Diagnostics;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorLivePolicyTests
{
    private static OverlaySettings Enabled => new() { Enabled = true, Widgets = [OverlayCatalog.CreateWidget("clock")] };
    private static readonly OverlayEditorLiveContext Context = new(true, true, false, OverlayTriggerArea.Abyss, new());

    [Fact]
    public void ActivationContentGameAndGlobalForegroundRemainIndependentGates()
    {
        Assert.True(OverlayEditorLivePolicy.IsVisible(Enabled, new(), Context));
        Assert.False(OverlayEditorLivePolicy.IsVisible(Enabled with { Enabled = false }, new(), Context));
        Assert.False(OverlayEditorLivePolicy.IsVisible(Enabled with { Widgets = [] }, new(), Context));
        Assert.False(OverlayEditorLivePolicy.IsVisible(Enabled, new(), Context with { GameAvailable = false }));
        var always = Enabled with { Visibility = "always" };
        Assert.False(OverlayEditorLivePolicy.IsVisible(always, new(), Context with { GameAvailable = false, GameForeground = false }));
        Assert.True(OverlayEditorLivePolicy.IsVisible(always, new(), Context with { GameAvailable = false, GameForeground = false, AllowBackground = true }));
    }

    [Fact]
    public void SessionVisibilityAndExcludedSessionTriggerDoNotInventStoppedTrackingObservations()
    {
        var settings = Enabled with { Visibility = "session" };
        Assert.False(OverlayEditorLivePolicy.IsVisible(settings, new() { SessionActive = true }, Context));
        Assert.True(OverlayEditorLivePolicy.IsVisible(settings, new() { SessionActive = true, TrackingAvailable = true }, Context));
        settings = Enabled with { TriggerRules = new() { SessionActive = OverlayTriggerRequirement.Excluded } };
        Assert.False(OverlayEditorLivePolicy.IsVisible(settings, new(), Context));
        Assert.True(OverlayEditorLivePolicy.IsVisible(settings, new() { TrackingAvailable = true }, Context));
    }

    [Theory]
    [InlineData(OverlayTriggerRequirement.Required, true, true)]
    [InlineData(OverlayTriggerRequirement.Required, false, false)]
    [InlineData(OverlayTriggerRequirement.Required, null, false)]
    [InlineData(OverlayTriggerRequirement.Excluded, false, true)]
    [InlineData(OverlayTriggerRequirement.Excluded, true, false)]
    [InlineData(OverlayTriggerRequirement.Excluded, null, false)]
    public void AdditionalWindowRulesRetainKnownFalseAndUnknown(OverlayTriggerRequirement rule, bool? observed, bool expected)
    {
        var settings = Enabled with { TriggerRules = new() { MinimapMissing = rule } };
        Assert.Equal(expected, OverlayEditorLivePolicy.IsVisible(settings, new(), Context with { Visual = new(MinimapMissing: observed) }));
    }

    [Theory]
    [InlineData(WorldContextStatus.Confirmed, WorldContextKind.Instance, "Abyss", OverlayTriggerArea.Abyss)]
    [InlineData(WorldContextStatus.Confirmed, WorldContextKind.Instance, "Dungeon", OverlayTriggerArea.Instances)]
    [InlineData(WorldContextStatus.Confirmed, WorldContextKind.OpenWorld, null, OverlayTriggerArea.OpenWorld)]
    [InlineData(WorldContextStatus.Stale, WorldContextKind.Instance, "Abyss", null)]
    [InlineData(WorldContextStatus.Transitioning, WorldContextKind.OpenWorld, null, null)]
    public void AreasUseConfirmedRawWorldContext(WorldContextStatus status, WorldContextKind kind, string? category, OverlayTriggerArea? expected)
    {
        var world = new WorldContextSnapshot(status, kind, 1, null, category, null, null, null, 1, null, "fixture");
        Assert.Equal(expected, OverlayEditorLivePolicy.Area(true, world));
        Assert.Null(OverlayEditorLivePolicy.Area(false, world));
    }

    [Fact]
    public void VisualSignalsRequireAnActualFreshFrameAndCurrentRunningContext()
    {
        var now = Stopwatch.GetTimestamp();
        var signal = new MapTrackingService.OverlayVisualSignal(false, now, 4);
        Assert.Equal(false, MapTrackingService.ReadOverlayVisualSignal(signal, 4, true, false, now));
        Assert.Null(MapTrackingService.ReadOverlayVisualSignal(signal, 5, true, false, now));
        Assert.Null(MapTrackingService.ReadOverlayVisualSignal(signal, 4, false, false, now));
        Assert.Null(MapTrackingService.ReadOverlayVisualSignal(signal, 4, true, true, now));
        Assert.Null(MapTrackingService.ReadOverlayVisualSignal(signal with { CapturedAt = 0 }, 4, true, false, now));
        Assert.Null(MapTrackingService.ReadOverlayVisualSignal(signal with { CapturedAt = now + 1 }, 4, true, false, now));
        Assert.Null(MapTrackingService.ReadOverlayVisualSignal(null, 4, true, false, now));
        var expiredAt = now + TimestampDelta(MapTrackingService.OverlayVisualObservationLifetime + TimeSpan.FromMilliseconds(1));
        Assert.Null(MapTrackingService.ReadOverlayVisualSignal(signal, 4, true, false, expiredAt));
    }

    [Fact]
    public void QuietDetectionCadenceAndProcessingDelayDoNotCreateUnknownVisibilityGaps()
    {
        var quietInterval = TimeSpan.FromMilliseconds(MapTrackingService.DetectionIntervalMs(
            found: true, worldMap: false, misses: 0, motionPixels: 0, flowLost: false, stillRuns: 10));
        Assert.Equal(TimeSpan.FromSeconds(2), MapTrackingService.MaximumFrameAge);
        Assert.Equal(quietInterval + MapTrackingService.MaximumFrameAge, MapTrackingService.OverlayVisualObservationLifetime);
        var settings = Enabled with
        {
            TriggerRules = new()
            {
                Areas = [OverlayTriggerArea.OpenWorld], Foreground = OverlayTriggerRequirement.Required,
                SessionActive = OverlayTriggerRequirement.Required, WorldMap = OverlayTriggerRequirement.Excluded,
                EscMenu = OverlayTriggerRequirement.Excluded,
            },
        };
        var frame = new OverlaySnapshot { TrackingAvailable = true, SessionActive = true };
        var context = Context with { Area = OverlayTriggerArea.OpenWorld };
        var capturedAt = Stopwatch.GetTimestamp();
        var signal = new MapTrackingService.OverlayVisualSignal(false, capturedAt, 7);
        // The next idle capture is scheduled two seconds later. While its detectors
        // still process the image, the previous actual observation remains known.
        foreach (var elapsed in new[] { TimeSpan.Zero, quietInterval, quietInterval + TimeSpan.FromMilliseconds(250),
            quietInterval + MapTrackingService.MaximumFrameAge })
        {
            var observed = MapTrackingService.ReadOverlayVisualSignal(signal, 7, true, false, capturedAt + TimestampDelta(elapsed));
            Assert.Equal(false, observed);
            Assert.True(OverlayEditorLivePolicy.IsVisible(settings, frame, context with { Visual = new(observed, observed) }));
        }
        var completedAt = capturedAt + TimestampDelta(quietInterval + TimeSpan.FromMilliseconds(250));
        var openedMenu = signal with { Value = true, CapturedAt = capturedAt + TimestampDelta(quietInterval) };
        var menu = MapTrackingService.ReadOverlayVisualSignal(openedMenu, 7, true, false, completedAt);
        Assert.False(OverlayEditorLivePolicy.IsVisible(settings, frame, context with { Visual = new(false, menu) }));
        // No accepted replacement, unavailable input or changed authority still fails closed.
        var expiredAt = capturedAt + TimestampDelta(MapTrackingService.OverlayVisualObservationLifetime + TimeSpan.FromMilliseconds(1));
        var expired = MapTrackingService.ReadOverlayVisualSignal(signal, 7, true, false, expiredAt);
        Assert.Null(expired);
        Assert.False(OverlayEditorLivePolicy.IsVisible(settings, frame, context with { Visual = new(expired, expired) }));
        Assert.Null(MapTrackingService.ReadOverlayVisualSignal(null, 7, true, false, completedAt));
        Assert.Null(MapTrackingService.ReadOverlayVisualSignal(signal, 8, true, false, completedAt));
    }

    private static long TimestampDelta(TimeSpan elapsed) => (long)Math.Round(elapsed.TotalSeconds * Stopwatch.Frequency);

    [Fact]
    public void RuntimeFeedbackNeverCreatesOrChangesLayoutsAndPublishesOnlyChangedStates()
    {
        var root = Path.Combine(Path.GetTempPath(), "soulcrest-live-state-" + Guid.NewGuid().ToString("N"));
        using var service = new OverlayService(root);
        var events = 0;
        service.Changed += () => events++;
        var id = service.SelectedOverlayId;
        service.SetRuntimeState(id, true);
        service.SetRuntimeState(id, true);
        Assert.True(service.GetState(id).IsVisible);
        Assert.Equal(1, events);
        service.SetRuntimeState(id, false, "fixture error");
        Assert.True(service.GetState(id).IsError);
        Assert.False(service.GetState(id).IsVisible);
        Assert.Null(service.LoadError);
        Assert.False(Directory.Exists(root));
        service.SetRuntimeState(id, false);
        Assert.False(service.GetState(id).IsError);
    }
}
