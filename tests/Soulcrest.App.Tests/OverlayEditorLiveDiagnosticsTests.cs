using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorLiveDiagnosticsTests
{
    [Fact]
    public void UserDiagnosisLeasesObserveExistingRuntimeWithoutADeveloperPort()
    {
        var previous = Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT");
        Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", null);
        try
        {
            Assert.False(OverlayEditorLiveDiagnostics.Enabled);
            var before = OverlayEditorLiveDiagnostics.CaptureRecorded().Version;
            var first = OverlayEditorLiveDiagnostics.BeginDiagnosticCollection();
            var second = OverlayEditorLiveDiagnostics.BeginDiagnosticCollection();
            try
            {
                Assert.True(OverlayEditorLiveDiagnostics.Enabled);
                OverlayEditorLiveDiagnostics.Policy(Settings, new(), Context, true);
                Assert.True(OverlayEditorLiveDiagnostics.CaptureRecorded().Version > before);
                first.Dispose(); first.Dispose();
                Assert.True(OverlayEditorLiveDiagnostics.Enabled);
            }
            finally { first.Dispose(); second.Dispose(); }
            Assert.False(OverlayEditorLiveDiagnostics.Enabled);
            Assert.NotEmpty(OverlayEditorLiveDiagnostics.CaptureRecorded().Entries);
            Assert.Null(Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT"));
        }
        finally { Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", previous); }
    }

    private static readonly OverlayEditorLiveContext Context = new(true, true, false, OverlayTriggerArea.OpenWorld, new(false, false, false));
    private static OverlaySettings Settings => new() { Enabled = true, Widgets = [OverlayCatalog.CreateWidget("clock")] };

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("invalid", false)]
    [InlineData("1024", false)]
    [InlineData("1025", true)]
    [InlineData("9222", true)]
    [InlineData("65535", true)]
    [InlineData("65536", false)]
    public void OnlyTheExistingValidDebugPortEnablesCollection(string? port, bool expected) =>
        Assert.Equal(expected, OverlayEditorLiveDiagnostics.ValidPort(port));

    [Fact]
    public void RingBufferKeepsOnlyTheLatest128EventsInOrder()
    {
        var elapsed = 0d;
        var buffer = new OverlayLiveDiagnosticBuffer(() => ++elapsed);
        for (var i = 0; i < 140; i++) buffer.Add(null, "hide", "fixture", "post-capture", i);
        var snapshot = buffer.Capture();
        Assert.Equal(140, snapshot.Version);
        Assert.Equal(128, snapshot.Entries.Count);
        Assert.Equal(13, snapshot.Entries[0].Sequence);
        Assert.Equal(140, snapshot.Entries[^1].Sequence);
        Assert.True(snapshot.Entries.Zip(snapshot.Entries.Skip(1)).All(pair => pair.First.AtMs <= pair.Second.AtMs));
        buffer.Add(null, "clear", "fixture", "lifecycle", 141);
        Assert.Equal(140, snapshot.Entries[^1].Sequence);
    }

    [Fact]
    public void PolicyStatesDeduplicateByExactInputsAndSettingsReference()
    {
        var buffer = new OverlayLiveDiagnosticBuffer();
        var settings = Settings;
        var state = OverlayEditorLiveDiagnostics.Inputs(settings, new(), Context, true);
        buffer.Policy(settings, state);
        for (var i = 0; i < 100; i++) buffer.Policy(settings, state);
        Assert.Single(buffer.Capture().Entries);
        buffer.Policy(settings, state with { WorldMap = null });
        buffer.Policy(settings with { }, state);
        var entries = buffer.Capture().Entries;
        Assert.Equal(3, entries.Count);
        Assert.Equal(entries[0].Window, entries[1].Window);
        Assert.NotEqual(entries[0].Window, entries[2].Window);
    }

    [Fact]
    public void FrameCountersDistinguishPendingCapturesFromPresentedRankValuesWithoutLeakingData()
    {
        var now = 10d;
        var buffer = new OverlayLiveDiagnosticBuffer(() => now);
        var settings = Settings with { Widgets = [OverlayCatalog.CreateWidget("rank-net"),
            OverlayCatalog.CreateWidget("rank-net-hour"), new() { Kind = "private-widget-name" }] };
        var snapshot = new OverlaySnapshot { SessionId = "private-session", Metrics = new Dictionary<string, OverlayMetric>
        {
            ["rank-net"] = new("private-caption", "—"),
            ["rank-net-hour"] = new("private-caption", "—"),
            ["private-widget-name"] = new("private-caption", "private-value"),
        } };
        buffer.FrameStarted(settings);
        var pending = Assert.Single(buffer.Capture().Frames);
        Assert.Equal(1, pending.Started);
        Assert.Equal(0, pending.Captured);
        Assert.Equal(0, pending.Presented);
        Assert.Null(pending.LastPresentedAtMs);
        now = 20;
        buffer.FrameCaptured(settings);
        buffer.FramePresented(settings, snapshot, 1);
        var first = buffer.Capture();
        var shown = Assert.Single(first.Frames);
        Assert.Equal(20, shown.LastPresentedAtMs);
        Assert.False(shown.PresentedRankInputsKnown!["rank-net"]);
        now = 30;
        buffer.FrameStarted(settings);
        buffer.FrameCaptured(settings); // A completed obsolete capture is never a presentation.
        var obsolete = Assert.Single(buffer.Capture().Frames);
        Assert.Equal(2, obsolete.Captured);
        Assert.Equal(1, obsolete.Presented);
        Assert.Equal(20, obsolete.LastPresentedAtMs);
        var values = snapshot.Metrics.ToDictionary(pair => pair.Key, pair => pair.Value);
        values["rank-net"] = values["rank-net"] with { Value = "-1234567" };
        values["rank-net-hour"] = values["rank-net-hour"] with { Value = "0" };
        now = 40;
        buffer.FrameStarted(settings);
        buffer.FrameCaptured(settings);
        buffer.FramePresented(settings, snapshot with { Metrics = values }, 3);
        var latest = buffer.Capture();
        var frame = Assert.Single(latest.Frames);
        Assert.Equal(3, frame.Started);
        Assert.Equal(3, frame.Captured);
        Assert.Equal(2, frame.Presented);
        Assert.Equal(3, frame.LastPresentedEpoch);
        Assert.All(frame.PresentedRankInputsKnown!, pair => Assert.True(pair.Value));
        Assert.False(shown.PresentedRankInputsKnown["rank-net"]); // Earlier captures stay immutable.
        Assert.True(latest.Version > first.Version);
        Assert.Empty(latest.Entries);
        var json = OverlayEditorLiveDiagnostics.Serialize(latest);
        Assert.DoesNotContain("private-", json);
        Assert.DoesNotContain("1234567", json);
    }

    [Fact]
    public void FrameDiagnosticsAreBoundedAndDoNotEvictLifecycleEvents()
    {
        var now = 0d;
        var buffer = new OverlayLiveDiagnosticBuffer(() => ++now);
        buffer.Add(null, "hide", "fixture", "lifecycle", 1);
        for (var i = 0; i < 100; i++) buffer.FrameStarted(Settings);
        var snapshot = buffer.Capture();
        Assert.Equal(32, snapshot.Frames.Count);
        Assert.Equal(69, snapshot.Frames[0].Window);
        Assert.Equal("hide", Assert.Single(snapshot.Entries).Action);
    }

    [Fact]
    public void UnknownObservationsAndAllSimultaneouslyFailedGatesRemainDistinct()
    {
        var settings = Settings with
        {
            Enabled = false, Visibility = "session", Widgets = [],
            TriggerRules = new() { Areas = [OverlayTriggerArea.Abyss], SessionActive = OverlayTriggerRequirement.Required,
                WorldMap = OverlayTriggerRequirement.Excluded, MinimapMissing = OverlayTriggerRequirement.Excluded }
        };
        var inputs = OverlayEditorLiveDiagnostics.Inputs(settings, new() { SessionActive = true },
            Context with { GameAvailable = false, GameForeground = false, Visual = new(null, false, null) }, false);
        Assert.Null(inputs.ObservedSessionActive);
        Assert.Null(inputs.WorldMap);
        Assert.Equal(false, inputs.EscMenu);
        Assert.Null(inputs.MinimapMissing);
        Assert.Equal(OverlayLiveFailedGates.Disabled | OverlayLiveFailedGates.Empty | OverlayLiveFailedGates.GlobalForeground
            | OverlayLiveFailedGates.GameUnavailable | OverlayLiveFailedGates.SessionVisibility | OverlayLiveFailedGates.Area
            | OverlayLiveFailedGates.SessionRule | OverlayLiveFailedGates.WorldMapRule | OverlayLiveFailedGates.MinimapRule, inputs.FailedGates);
    }

    [Fact]
    public void AuthorityChangesContainOnlyChangeBitsWithoutRawIdentifiers()
    {
        var before = new OverlaySnapshot();
        var after = new OverlaySnapshot
        {
            SessionId = "private-session", SessionActive = true, TrackingAvailable = true,
            Abyss = AbyssRankOverlaySnapshot.Empty with { ProfileId = "private-profile", BindingGeneration = 2, WorldContextVisible = true }
        };
        var changes = OverlayEditorLiveDiagnostics.AuthorityChanges(before, after);
        Assert.Equal(OverlayLiveAuthorityChanges.SessionIdChanged | OverlayLiveAuthorityChanges.SessionActiveChanged
            | OverlayLiveAuthorityChanges.TrackingChanged | OverlayLiveAuthorityChanges.AbyssProfileChanged
            | OverlayLiveAuthorityChanges.BindingChanged | OverlayLiveAuthorityChanges.WorldVisibleChanged, changes);
        var buffer = new OverlayLiveDiagnosticBuffer();
        buffer.Add(Settings, "hide", "authority-changed", "post-capture", 4, changes);
        var json = OverlayEditorLiveDiagnostics.Serialize(buffer.Capture());
        Assert.DoesNotContain("private-session", json);
        Assert.DoesNotContain("private-profile", json);
        Assert.Contains("BindingChanged", json);
    }

    [Fact]
    public void ErrorsExposeOnlyTypeAndHResultAndNeverMessagePathOrStack()
    {
        var buffer = new OverlayLiveDiagnosticBuffer();
        var error = new InvalidOperationException("private-name/private-path/private-data</script>");
        buffer.Add(Settings, "error", "exception", "render", 3, error: error);
        var entry = Assert.Single(buffer.Capture().Entries);
        Assert.Equal(nameof(InvalidOperationException), entry.ExceptionType);
        Assert.Equal(error.HResult, entry.HResult);
        var json = OverlayEditorLiveDiagnostics.Serialize(buffer.Capture());
        Assert.DoesNotContain("private-", json);
        Assert.DoesNotContain("</script>", json);
    }

    [Fact]
    public void InvalidRulesAndUnrecognizedVisibilityCannotLeakArbitrarySettingsText()
    {
        var settings = Settings with { Visibility = "private-value", TriggerRules = new() { Areas = [(OverlayTriggerArea)999] } };
        var state = OverlayEditorLiveDiagnostics.Inputs(settings, new(), Context, false);
        Assert.Equal("other", state.Visibility);
        Assert.False(state.RulesValid);
        Assert.True(state.FailedGates.HasFlag(OverlayLiveFailedGates.InvalidRules));
        var buffer = new OverlayLiveDiagnosticBuffer();
        buffer.Policy(settings, state);
        Assert.DoesNotContain("private-value", OverlayEditorLiveDiagnostics.Serialize(buffer.Capture()));
    }
}
