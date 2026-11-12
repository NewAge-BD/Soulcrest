using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorLiveHostTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public Task RecordingVisibilityRequiresBothSafeCaptureAndTheWindowPermission(bool safeCapture, bool excluded, bool expected) => RunSta(() =>
    {
        using var fixture = new Fixture { CaptureWindowOnly = safeCapture };
        fixture.Editor.SaveAsync(fixture.Editor.Settings with { CaptureExcluded = excluded }).GetAwaiter().GetResult();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        Assert.True(surface.Visible);
        Assert.Equal(expected, surface.VisibleInRecordings);
        Assert.Equal(excluded, fixture.Editor.Settings.CaptureExcluded);
        Assert.Equal(1, fixture.Source.ActiveLeases);
    });

    [Fact]
    public Task RecordingPolicyChangesApplyIndependentlyToTwoWindowsWithoutReplacingTheirHosts() => RunSta(() =>
    {
        using var fixture = new Fixture(marketWindow: true) { CaptureWindowOnly = true };
        var first = fixture.Editor.Overlays[0];
        var second = fixture.Editor.Overlays[1];
        fixture.Editor.SaveAsync(first.Id, first.Settings with { CaptureExcluded = false }).GetAwaiter().GetResult();
        fixture.Host.Start();
        var firstSurface = fixture.Surfaces.Single(surface => surface.Configured!.Id == first.Id);
        var secondSurface = fixture.Surfaces.Single(surface => surface.Configured!.Id == second.Id);
        Assert.True(firstSurface.VisibleInRecordings);
        Assert.False(secondSurface.VisibleInRecordings);
        var before = File.ReadAllBytes(fixture.Path);
        fixture.CaptureWindowOnly = false;
        fixture.Host.Invalidate();
        Assert.False(firstSurface.VisibleInRecordings);
        Assert.False(secondSurface.VisibleInRecordings);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        fixture.CaptureWindowOnly = true;
        fixture.Host.Invalidate();
        Assert.True(firstSurface.VisibleInRecordings);
        Assert.False(secondSurface.VisibleInRecordings);
        first = fixture.Editor.Overlays.Single(window => window.Id == first.Id);
        fixture.Editor.SaveAsync(first.Id, first.Settings with { CaptureExcluded = true }).GetAwaiter().GetResult();
        fixture.Host.Invalidate();
        Assert.False(firstSurface.VisibleInRecordings);
        Assert.False(secondSurface.VisibleInRecordings);
        second = fixture.Editor.Overlays.Single(window => window.Id == second.Id);
        fixture.Editor.SaveAsync(second.Id, second.Settings with { CaptureExcluded = false }).GetAwaiter().GetResult();
        fixture.Host.Invalidate();
        Assert.False(firstSurface.VisibleInRecordings);
        Assert.True(secondSurface.VisibleInRecordings);
        Assert.Equal(2, fixture.Surfaces.Count);
        Assert.All(fixture.Surfaces, surface => { Assert.True(surface.Visible); Assert.False(surface.Disposed); });
        Assert.Equal(1, fixture.Source.ActiveLeases);
    });

    [Fact]
    public Task EnabledEmptyWindowKeepsItsMandatoryControlWithoutAcquiringMarketData() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Editor.SaveAsync(fixture.Editor.Settings with { Widgets = [], Visibility = "always" }).GetAwaiter().GetResult();
        fixture.Host.Start();
        Assert.True(Assert.Single(fixture.Surfaces).Visible);
        Assert.Empty(Assert.Single(fixture.Surfaces).Configured!.Settings.Widgets);
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.False(fixture.Source.Markets.Last());
    });

    [Fact]
    public Task AllWindowsShareOneSourceFrameAndOnlyVisibleMarketWindowsAcquirePrices() => RunSta(() =>
    {
        using var fixture = new Fixture(marketWindow: true);
        var before = File.ReadAllBytes(fixture.Path);
        fixture.Host.Start();
        Assert.Equal(1, fixture.Source.Captures);
        Assert.Equal(2, fixture.Renderer.Sessions.Count);
        var snapshots = fixture.Renderer.Sessions.SelectMany(session => session.Snapshots).ToArray();
        Assert.Equal(2, snapshots.Length);
        Assert.Same(snapshots[0], snapshots[1]);
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.True(fixture.Source.Markets.Last());
        Assert.All(fixture.Surfaces, surface => Assert.True(surface.Visible));
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    });

    [Fact]
    public Task PlacementHidesAndDisposesLiveSurfacesWithoutDisablingTheirSavedWindows() => RunSta(() =>
    {
        using var fixture = new Fixture(marketWindow: true);
        fixture.Host.Start();
        var before = File.ReadAllBytes(fixture.Path);
        fixture.Host.SetPlacementActive(true);
        Assert.All(fixture.Surfaces, surface => { Assert.False(surface.Visible); Assert.True(surface.Disposed); });
        Assert.Equal(0, fixture.Source.ActiveLeases);
        Assert.All(fixture.Editor.Overlays, window => Assert.True(window.Settings.Enabled));
        fixture.Host.SetPlacementActive(false);
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.Equal(4, fixture.Renderer.Sessions.Count);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    });

    [Fact]
    public Task DisposingSessionsCannotReenterThroughAQueuedOwnerInvalidationOrLeaveRuntimeVisible() => RunSta(() =>
    {
        using var fixture = new Fixture(marketWindow: true);
        fixture.Host.Start();
        Assert.All(fixture.Surfaces, surface => Assert.True(surface.Visible));
        var captures = fixture.Source.Captures;
        var sessionCount = fixture.Renderer.Sessions.Count;
        // MainForm can already have queued a context callback when WebView disposal pumps messages.
        fixture.QueueOwnerAction(fixture.Host.Invalidate);
        foreach (var session in fixture.Renderer.Sessions) session.OnDispose = Application.DoEvents;
        fixture.Host.Dispose();
        fixture.Host.Dispose();
        Assert.Equal(captures, fixture.Source.Captures);
        Assert.Equal(sessionCount, fixture.Renderer.Sessions.Count);
        Assert.All(fixture.Renderer.Sessions, session => Assert.True(session.Disposed));
        Assert.All(fixture.Surfaces, surface => { Assert.False(surface.Visible); Assert.True(surface.Disposed); });
        Assert.All(fixture.Editor.Overlays, window => Assert.False(fixture.Editor.GetState(window.Id).IsVisible));
        Assert.Equal(0, fixture.Source.ActiveLeases);
    });

    [Fact]
    public Task ContextLossRevokesEverySurfaceAndRuntimeStateWithoutStartingAnotherCapture() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var session = Assert.Single(fixture.Renderer.Sessions);
        var captures = session.Snapshots.Count;
        fixture.Allowed = false;
        fixture.Host.Invalidate();
        Assert.False(Assert.Single(fixture.Surfaces).Visible);
        Assert.True(session.Disposed);
        Assert.Equal(captures, session.Snapshots.Count);
        Assert.False(fixture.Editor.GetState("default").IsVisible);
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.False(fixture.Source.Markets.Last());
    });

    [Fact]
    public Task ASessionGatedWindowContinuesObservingUntilARealSessionCanRevealIt() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Source.Frame = fixture.Source.Frame with { SessionActive = false };
        fixture.Host.Start();
        Assert.Empty(fixture.Surfaces);
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.False(fixture.Source.Markets.Last());
        fixture.Source.Frame = fixture.Source.Frame with { SessionActive = true };
        fixture.Source.Publish();
        Assert.True(Assert.Single(fixture.Surfaces).Visible);
        Assert.Equal(1, fixture.Source.ActiveLeases);
    });

    [Fact]
    public Task RuntimeFeedbackAndPreviewEventsDoNotReenterTheLayoutRenderingLoop() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var captures = fixture.Source.Captures;
        var session = Assert.Single(fixture.Renderer.Sessions);
        fixture.Editor.SetRuntimeState("default", false, "synthetic status");
        fixture.Editor.SetRuntimeState("default", true);
        fixture.Editor.SetPreviewAsync(true).GetAwaiter().GetResult();
        fixture.Editor.SetPreviewAsync(false).GetAwaiter().GetResult();
        Assert.Equal(captures, fixture.Source.Captures);
        Assert.Single(session.Snapshots);
        Assert.Single(fixture.Renderer.Sessions);
    });

    [Theory]
    [InlineData("", false)]
    [InlineData("abyss-killfeed", true)]
    [InlineData("abyss-rank-progress", true)]
    public Task AbyssWorldVisibilityOnlyRevokesWindowsContainingAnAbyssModule(string abyssKind, bool shouldHide) => RunSta(() =>
    {
        using var fixture = new Fixture();
        var widgets = new[] { "duration", "market-kina", "clock", "dropped-kina", "daily-kina", "drop-list" }
            .Select(kind => OverlayCatalog.CreateWidget(kind)).ToList();
        if (abyssKind.Length != 0) widgets.Add(OverlayCatalog.CreateWidget(abyssKind));
        fixture.Editor.SaveAsync(fixture.Editor.Settings with { Widgets = widgets.ToArray() }).GetAwaiter().GetResult();
        fixture.Source.Frame = fixture.Source.Frame with { Abyss = fixture.Source.Frame.Abyss with { WorldContextVisible = true } };
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        var session = Assert.Single(fixture.Renderer.Sessions);
        var hides = surface.Hides;
        var pending = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Renderer.Pending = pending;
        fixture.Source.Frame = fixture.Source.Frame with { Abyss = fixture.Source.Frame.Abyss with { WorldContextVisible = false } };
        fixture.Source.Publish();
        var hiddenBeforeReplacement = surface.Hides > hides;
        var visibleBeforeReplacement = surface.Visible;
        pending.SetResult(new Bitmap(360, 260));
        DrainRender(fixture);
        Assert.Equal(shouldHide, hiddenBeforeReplacement);
        Assert.Equal(!shouldHide, visibleBeforeReplacement);
        Assert.False(surface.Disposed);
        Assert.False(session.Disposed);
        Assert.Single(fixture.Renderer.Sessions);
    });

    [Theory]
    [InlineData("session-id")]
    [InlineData("session-active")]
    [InlineData("tracking")]
    [InlineData("profile")]
    [InlineData("binding")]
    public Task GlobalSessionTrackingAndCharacterBoundariesStillHideAWindowWithoutAbyssModules(string boundary) => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        var session = Assert.Single(fixture.Renderer.Sessions);
        var hides = surface.Hides;
        var pending = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Renderer.Pending = pending;
        var before = fixture.Source.Frame;
        fixture.Source.Frame = boundary switch
        {
            "session-id" => before with { SessionId = "synthetic-other-session" },
            "session-active" => before with { SessionActive = false },
            "tracking" => before with { TrackingAvailable = false },
            "profile" => before with { Abyss = before.Abyss with { ProfileId = "synthetic-other-profile" } },
            _ => before with { Abyss = before.Abyss with { BindingGeneration = (before.Abyss.BindingGeneration ?? 0) + 1 } },
        };
        fixture.Source.Publish();
        var hiddenBeforeReplacement = surface.Hides > hides;
        var visibleBeforeReplacement = surface.Visible;
        if (session.Snapshots.Count > 1) pending.SetResult(new Bitmap(360, 260));
        else pending.SetCanceled(); // Session-active=false is ineligible before another render.
        DrainRender(fixture);
        Assert.True(hiddenBeforeReplacement);
        Assert.False(visibleBeforeReplacement);
    });

    [Theory]
    [InlineData("menu", true)] [InlineData("category", true)] [InlineData("preset", true)]
    [InlineData("binding", true)] [InlineData("freshness", true)] [InlineData("source", true)]
    [InlineData("menu", false)] [InlineData("sequence", true)] [InlineData("board", true)]
    public Task GenusAuthorityRevokesItsOwnPendingSurfaceWithoutBlankingUnrelatedWindows(string boundary, bool containsGenus) => RunSta(() =>
    {
        using var fixture = new Fixture();
        if (containsGenus) fixture.Editor.SaveAsync(fixture.Editor.Settings with { Widgets = [OverlayCatalog.CreateWidget("genus-insight")] }).GetAwaiter().GetResult();
        fixture.Source.Frame = fixture.Source.Frame with { GenusInsight = GenusInsightOverlayExamples.Create(DateTimeOffset.UtcNow, "anonymous-first") };
        fixture.Host.Start(); var surface = Assert.Single(fixture.Surfaces); var hides = surface.Hides;
        var pending = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously); fixture.Renderer.Pending = pending;
        var before = fixture.Source.Frame.GenusInsight;
        fixture.Source.Frame = fixture.Source.Frame with { GenusInsight = boundary switch
        {
            "menu" => before with { MenuVisible = false }, "category" => before with { SelectedGenus = "fera" },
            "preset" => before with { ViewedPreset = 2 }, "binding" => before with { Generation = before.Generation + 1 },
            "freshness" => before with { IsCurrent = false }, "source" => before with { MenuSourceGeneration = before.MenuSourceGeneration + 1 },
            "board" => before with { ViewedBoard = null },
            _ => before with { Sequence = before.Sequence + 1 },
        } };
        fixture.Source.Publish();
        Assert.Equal(containsGenus && boundary != "sequence", surface.Hides > hides);
        Assert.Equal(!containsGenus || boundary == "sequence", surface.Visible);
        pending.SetResult(new Bitmap(464, 410)); DrainRender(fixture);
    });

    [Fact]
    public Task AWindowRenderErrorIsVisibleInRuntimeStateAndDoesNotAcquireLiveDataOrChangeTheStore() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Renderer.Throw = true;
        var before = File.ReadAllBytes(fixture.Path);
        fixture.Host.Start();
        Assert.True(fixture.Editor.GetState("default").IsError);
        Assert.Contains("synthetic renderer failure", fixture.Editor.GetState("default").Status);
        Assert.False(fixture.Editor.GetState("default").IsVisible);
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.False(fixture.Source.Markets.Last());
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    });

    [Fact]
    public Task AStaleCompletedCaptureCannotReshowARevokedWindow() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Renderer.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        Assert.Equal(0, surface.Shows);
        fixture.Allowed = false;
        fixture.Host.Invalidate();
        fixture.Renderer.Pending.SetResult(new Bitmap(360, 260));
        var deadline = Environment.TickCount64 + 3000;
        var rendering = typeof(OverlayEditorLiveHostCoordinator).GetField("_rendering", BindingFlags.Instance | BindingFlags.NonPublic)!;
        while ((bool)rendering.GetValue(fixture.Host)! && Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Assert.False((bool)rendering.GetValue(fixture.Host)!);
        Assert.Equal(0, surface.Shows);
        Assert.False(surface.Visible);
        Assert.False(fixture.Editor.GetState("default").IsVisible);
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.False(fixture.Source.Markets.Last());
    });

    [Fact]
    public Task WorkerInvalidationRevokesAnOldCaptureBeforeItsUiCallbackCanRun() => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var scheduler = new BufferedContext();
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(scheduler);
            fixture.Renderer.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Host.Start();
            var surface = Assert.Single(fixture.Surfaces);
            var epochField = typeof(OverlayEditorLiveHostCoordinator).GetField("_epoch", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var requestedField = typeof(OverlayEditorLiveHostCoordinator).GetField("_requested", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var before = (long)epochField.GetValue(fixture.Host)!;
            // Also queue an animation follow-up. It must not adopt a new authority
            // generation while the matching UI snapshot has not been applied yet.
            requestedField.SetValue(fixture.Host, true);
            fixture.Source.Frame = fixture.Source.Frame with { SessionActive = false };
            Task.Run(fixture.Source.Publish).GetAwaiter().GetResult();
            Assert.True((long)epochField.GetValue(fixture.Host)! > before);
            Assert.Equal(1, fixture.Source.Captures); // Owner.BeginInvoke has not been pumped.
            fixture.Renderer.Pending.SetResult(new Bitmap(360, 260));
            Assert.True(scheduler.WaitForPost(TimeSpan.FromSeconds(5)));
            scheduler.RunOne(); // Finish the old render before Owner.BeginInvoke.
            Assert.Equal(1, fixture.Source.Captures);
            Assert.Equal(0, surface.Shows);
            Assert.False(surface.Visible);
            Assert.False((bool)typeof(OverlayEditorLiveHostCoordinator).GetField("_rendering", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Host)!);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        Application.DoEvents();
        Assert.Equal(2, fixture.Source.Captures);
        Assert.All(fixture.Surfaces, surface => Assert.Equal(0, surface.Shows));
        Assert.False(fixture.Editor.GetState("default").IsVisible);
    });

    [Fact]
    public Task ARemovedPreferredMonitorCannotAuthorizeAnOffscreenWindow() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.HasMonitor = false;
        fixture.Host.Start();
        Assert.Empty(fixture.Surfaces);
        Assert.Empty(fixture.Renderer.Sessions);
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.False(fixture.Source.Markets.Last());
        Assert.True(fixture.Editor.GetState("default").IsError);
    });

    [Theory]
    [InlineData("com")]
    [InlineData("invalid-operation")]
    public Task ASupersededCaptureFailureCannotHideOrPoisonAnEligibleLiveWindow(string failure) => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        var session = Assert.Single(fixture.Renderer.Sessions);
        var hides = surface.Hides;
        var pending = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Renderer.Pending = pending;
        fixture.Source.Frame = fixture.Source.Frame with { ClockUtcNow = fixture.Source.Frame.ClockUtcNow.AddSeconds(1) };
        fixture.Source.Publish();
        var oldToken = session.Tokens.Last();
        var latest = fixture.Source.Frame with { ClockUtcNow = fixture.Source.Frame.ClockUtcNow.AddSeconds(1) };
        fixture.Source.Frame = latest;
        fixture.Source.Publish();
        Assert.True(oldToken.IsCancellationRequested);
        // Native captures need not honor cancellation. Their late error belongs
        // to the revoked epoch, while the window and its source remain eligible.
        fixture.Renderer.Pending = null;
        pending.SetException(failure == "com" ? new COMException("Synthetic stale capture failure")
            : new InvalidOperationException("Synthetic stale capture failure"));
        DrainRender(fixture);
        Assert.True(surface.Visible);
        Assert.Equal(hides, surface.Hides);
        Assert.False(surface.Disposed);
        Assert.False(session.Disposed);
        Assert.Single(fixture.Renderer.Sessions);
        Assert.Same(latest, session.Snapshots.Last());
        Assert.Equal(2, surface.Shows);
        Assert.False(fixture.Editor.GetState("default").IsError);
    });

    [Theory]
    [InlineData("com")]
    [InlineData("invalid-operation")]
    public Task AStaleCancelledCaptureFailureCannotDisposeTheSessionOnTheNextSourceRefresh(string failure) => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        var session = Assert.Single(fixture.Renderer.Sessions);
        var pending = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Renderer.Pending = pending;
        fixture.Source.Publish();
        var oldToken = session.Tokens.Last();
        fixture.Source.Publish();
        Assert.True(oldToken.IsCancellationRequested);
        fixture.Renderer.Pending = null;
        pending.SetException(failure == "com" ? new COMException("Synthetic stale capture failure")
            : new InvalidOperationException("Synthetic stale capture failure"));
        DrainRender(fixture);
        // A failed marker left by the obsolete operation used to be collected
        // here, disposing both the native surface and its persistent WebView.
        fixture.Source.Publish();
        DrainRender(fixture);
        Assert.Single(fixture.Renderer.Sessions);
        Assert.Single(fixture.Surfaces);
        Assert.False(session.Disposed);
        Assert.False(surface.Disposed);
        Assert.True(surface.Visible);
        Assert.False(fixture.Editor.GetState("default").IsError);
    });

    [Fact]
    public Task AnimationCapturesReuseTheSameFrameAndSessionWithoutAnotherSourceRead() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Source.Frame = fixture.Source.Frame with { ItemAnimationsEnabled = true };
        fixture.Editor.SaveAsync(fixture.Editor.Settings with { Widgets = [OverlayCatalog.CreateWidget("drop-grid")] }).GetAwaiter().GetResult();
        fixture.Host.Start();
        typeof(OverlayEditorLiveHostCoordinator).GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Host, [null, EventArgs.Empty]);
        Assert.Equal(1, fixture.Source.Captures);
        var session = Assert.Single(fixture.Renderer.Sessions);
        Assert.Equal(2, session.Snapshots.Count);
        Assert.Same(session.Snapshots[0], session.Snapshots[1]);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AReentrantNormalRefreshDuringPresentationDoesNotBlinkTheAuthorizedWindow(bool worker) => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        var session = Assert.Single(fixture.Renderer.Sessions);
        var hides = surface.Hides;
        var latest = fixture.Source.Frame with { ClockUtcNow = fixture.Source.Frame.ClockUtcNow.AddSeconds(1) };
        surface.OnShow = () =>
        {
            surface.OnShow = null;
            fixture.Source.Frame = latest;
            if (worker) Task.Run(fixture.Host.Invalidate).GetAwaiter().GetResult();
            else fixture.Host.Invalidate();
        };
        fixture.Source.Publish();
        Application.DoEvents();
        DrainRender(fixture);
        Assert.Equal(hides, surface.Hides);
        Assert.True(surface.Visible);
        Assert.False(surface.Disposed);
        Assert.False(session.Disposed);
        Assert.Single(fixture.Renderer.Sessions);
        Assert.Same(latest, session.Snapshots.Last());
        Assert.True(fixture.Editor.GetState("default").IsVisible);
    });

    [Theory]
    [InlineData("session", false)]
    [InlineData("session", true)]
    [InlineData("context", false)]
    [InlineData("context", true)]
    public Task AReentrantAuthorityRevocationDuringPresentationStillHidesBeforeAQueuedWorkerCallback(string boundary, bool worker) => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        var hides = surface.Hides;
        var pending = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        surface.OnShow = () =>
        {
            surface.OnShow = null;
            fixture.Renderer.Pending = pending;
            if (boundary == "session") fixture.Source.Frame = fixture.Source.Frame with { SessionId = "synthetic-reentrant-session" };
            else fixture.Allowed = false;
            if (worker) Task.Run(fixture.Host.Invalidate).GetAwaiter().GetResult();
            else fixture.Host.Invalidate();
        };
        fixture.Source.Publish();
        // Do not pump BeginInvoke yet. A worker already revoked the old epoch;
        // the post-present check must inspect current authority, not its old frame.
        var visibleBeforeWorkerCallback = surface.Visible;
        var hiddenBeforeWorkerCallback = surface.Hides > hides;
        Application.DoEvents();
        fixture.Renderer.Pending = null;
        using var replacement = new Bitmap(360, 260);
        pending.SetResult(replacement);
        DrainRender(fixture);
        Assert.False(visibleBeforeWorkerCallback);
        Assert.True(hiddenBeforeWorkerCallback);
        if (boundary == "context") Assert.False(surface.Visible);
    });

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public Task PostPresentReconciliationHasABoundedRetryAndFailsClosedIfStillUnstable(int changesDuringCapture, bool shouldHide) => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        var session = Assert.Single(fixture.Renderer.Sessions);
        var hides = surface.Hides;
        var reconciles = 0;
        surface.OnShow = () =>
        {
            surface.OnShow = null;
            fixture.Source.OnCapture = () =>
            {
                if (++reconciles > changesDuringCapture) return;
                fixture.Source.Frame = fixture.Source.Frame with { ClockUtcNow = fixture.Source.Frame.ClockUtcNow.AddSeconds(1) };
                Task.Run(fixture.Host.Invalidate).GetAwaiter().GetResult();
            };
            Task.Run(fixture.Host.Invalidate).GetAwaiter().GetResult();
        };
        fixture.Source.Publish();
        var reconcilesBeforePump = reconciles;
        var hiddenBeforePump = surface.Hides > hides;
        fixture.Source.OnCapture = null;
        Application.DoEvents();
        DrainRender(fixture);
        Assert.Equal(2, reconcilesBeforePump);
        Assert.Equal(shouldHide, hiddenBeforePump);
        Assert.False(session.Disposed);
        Assert.False(surface.Disposed);
    });

    [Fact]
    public Task ADiscardedReconcileSnapshotCannotConsumeARealSessionBoundary() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        var pending = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        surface.OnShow = () =>
        {
            surface.OnShow = null;
            fixture.Renderer.Pending = pending;
            fixture.Source.Frame = fixture.Source.Frame with { SessionId = "synthetic-reconciled-session" };
            fixture.Source.OnCapture = () =>
            {
                fixture.Source.OnCapture = null;
                fixture.Source.Frame = fixture.Source.Frame with { ClockUtcNow = fixture.Source.Frame.ClockUtcNow.AddSeconds(1) };
                Task.Run(fixture.Host.Invalidate).GetAwaiter().GetResult();
            };
            Task.Run(fixture.Host.Invalidate).GetAwaiter().GetResult();
        };
        fixture.Source.Publish();
        var visibleBeforeWorkerCallback = surface.Visible;
        Application.DoEvents();
        fixture.Renderer.Pending = null;
        using var replacement = new Bitmap(360, 260);
        pending.SetResult(replacement);
        DrainRender(fixture);
        Assert.False(visibleBeforeWorkerCallback);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PostPresentNeverAccessesASurfaceRemovedOrDisposedDuringShow(bool disposeHost) => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        surface.RejectAccessAfterDispose = true;
        surface.OnShow = () =>
        {
            surface.OnShow = null;
            if (disposeHost) fixture.Host.Dispose();
            else { fixture.Allowed = false; fixture.Host.Invalidate(); }
        };
        fixture.Source.Publish();
        Application.DoEvents();
        DrainRender(fixture);
        Assert.True(surface.Disposed);
        Assert.Equal(0, surface.AccessesAfterDispose);
        Assert.False(fixture.Editor.GetState("default").IsVisible);
    });

    [Fact]
    public Task ADpiHideDuringPresentationIsNotReportedAsRuntimeVisibleBeforeTheReplacement() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        var pending = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        surface.OnShow = () =>
        {
            surface.OnShow = null;
            fixture.Renderer.Pending = pending;
            surface.HideFrame();
            surface.ChangeDpi();
        };
        fixture.Source.Publish();
        var actualVisible = surface.Visible;
        var runtimeVisible = fixture.Editor.GetState("default").IsVisible;
        fixture.Renderer.Pending = null;
        using var replacement = new Bitmap(360, 260);
        pending.SetResult(replacement);
        DrainRender(fixture);
        Assert.False(actualVisible);
        Assert.False(runtimeVisible);
    });

    [Fact]
    public Task NativePlacementCommitsOnlyPortableGeometryAndKeepsItsSharedDataLease() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        Assert.NotNull(surface.Configured);
        var settings = fixture.Editor.Settings;
        var widgets = settings.Widgets;
        surface.Place(new(-1700, 80, 360, 260), 1, settings);
        WaitFor(() => surface.PlacementCompletions.Count == 1);
        Assert.Null(surface.PlacementCompletions[0]);
        Assert.Equal("fixture", fixture.Editor.Settings.PositionMonitorId);
        Assert.Equal(220d / (1920 - 360), fixture.Editor.Settings.PositionX, 8);
        Assert.Equal(80d / (1040 - 260), fixture.Editor.Settings.PositionY, 8);
        Assert.Equal(settings.Widgets.Select(widget => widget.Id), fixture.Editor.Settings.Widgets.Select(widget => widget.Id));
        Assert.Equal(widgets.Select(widget => widget.Kind), fixture.Editor.Settings.Widgets.Select(widget => widget.Kind));
        Assert.Equal(settings.Enabled, fixture.Editor.Settings.Enabled);
        Assert.Equal(settings.TriggerRules, fixture.Editor.Settings.TriggerRules);
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.False(fixture.Source.Markets.Last());
    });

    [Fact]
    public Task NativePlacementWriteFailureKeepsTheFileAndRevokesSurfacesUntilExplicitReload() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        surface.RejectAccessAfterDispose = true;
        var before = File.ReadAllBytes(fixture.Path);
        using (var blocker = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            surface.Place(new(-1700, 80, 360, 260), 1, fixture.Editor.Settings);
            WaitFor(() => fixture.Editor.LoadError is not null && surface.Disposed);
            Assert.Equal(before, File.ReadAllBytes(fixture.Path));
            Assert.Equal(0, surface.AccessesAfterDispose);
            fixture.Source.Publish();
            Assert.All(fixture.Surfaces, item => Assert.True(item.Disposed));
            Assert.Equal(0, fixture.Source.ActiveLeases);
        }
        Assert.True(fixture.Editor.ReloadAsync().GetAwaiter().GetResult().Succeeded);
        Application.DoEvents();
        Assert.Null(fixture.Editor.LoadError);
    });

    [Theory]
    [InlineData("session-active")]
    [InlineData("tracking")]
    [InlineData("session-id")]
    public Task WorkerAuthorityRevocationBeforeItsOwnerCallbackCannotCommitANativePlacement(string boundary) => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        surface.RejectAccessAfterDispose = true;
        var settings = fixture.Editor.Settings;
        var before = File.ReadAllBytes(fixture.Path);
        fixture.Source.Frame = boundary switch
        {
            "session-active" => fixture.Source.Frame with { SessionActive = false },
            "tracking" => fixture.Source.Frame with { TrackingAvailable = false },
            _ => fixture.Source.Frame with { SessionId = "synthetic-next-session" },
        };
        Task.Run(fixture.Source.Publish).GetAwaiter().GetResult();
        Assert.Equal(1, fixture.Source.Captures); // BeginInvoke is still queued on the owner.
        surface.Place(new(-1700, 80, 360, 260), 1, settings);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Same(settings, fixture.Editor.Settings);
        Assert.Equal(0, surface.AccessesAfterDispose);
        Application.DoEvents();
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Same(settings, fixture.Editor.Settings);
    });

    [Fact]
    public Task PendingSameAuthorityRefreshDoesNotCancelOrHideANativePlacement() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        var hides = surface.Hides;
        fixture.Source.Frame = fixture.Source.Frame with { ClockUtcNow = fixture.Source.Frame.ClockUtcNow.AddSeconds(1) };
        Task.Run(fixture.Source.Publish).GetAwaiter().GetResult();
        Assert.Equal(1, fixture.Source.Captures);
        surface.Place(new(-1700, 80, 360, 260), 1, fixture.Editor.Settings);
        WaitFor(() => surface.PlacementCompletions.Count == 1);
        Assert.Null(surface.PlacementCompletions[0]);
        Assert.Equal(220d / (1920 - 360), fixture.Editor.Settings.PositionX, 8);
        Assert.Equal(hides, surface.Hides);
        Assert.True(surface.Visible);
    });

    [Fact]
    public Task TwoImmediatePlacementsUseAcknowledgedSettingsWhileTheReplacementRendererIsStillPending() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Host.Start();
        var surface = Assert.Single(fixture.Surfaces);
        var pending = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Renderer.Pending = pending;
        surface.Place(new(-1700, 80, 360, 260), 1, surface.Configured!.Settings);
        WaitFor(() => surface.PlacementCompletions.Count == 1);
        Assert.False(pending.Task.IsCompleted);
        Assert.Null(surface.PlacementCompletions[0]);
        Assert.Same(fixture.Editor.Settings, surface.Configured!.Settings);
        surface.Place(new(-1660, 120, 360, 260), 1, surface.Configured.Settings);
        WaitFor(() => surface.PlacementCompletions.Count == 2);
        Assert.False(pending.Task.IsCompleted);
        Assert.All(surface.PlacementCompletions, Assert.Null);
        Assert.Same(fixture.Editor.Settings, surface.Configured!.Settings);
        Assert.Equal(260d / (1920 - 360), fixture.Editor.Settings.PositionX, 8);
        Assert.Equal(120d / (1040 - 260), fixture.Editor.Settings.PositionY, 8);
        Assert.False(fixture.Editor.GetState("default").IsError);
        fixture.Renderer.Pending = null;
        pending.SetResult(new Bitmap(360, 260));
        DrainRender(fixture);
    });

    [Fact]
    public Task NativeRankPresentationIsRecordedOnlyAfterAFrameReachesTheSurface() => RunSta(() =>
    {
        using var diagnosis = OverlayEditorLiveDiagnostics.BeginDiagnosticCollection();
        using var fixture = new Fixture();
        fixture.Editor.SaveAsync(fixture.Editor.Settings with { Widgets = [OverlayCatalog.CreateWidget("rank-net")] })
            .GetAwaiter().GetResult();
        var values = fixture.Source.Frame.Metrics.ToDictionary(pair => pair.Key, pair => pair.Value);
        values["rank-net"] = values["rank-net"] with { Value = "—" };
        fixture.Source.Frame = fixture.Source.Frame with { Metrics = values, Drops = [] };
        fixture.Host.Start();
        var initial = OverlayEditorLiveDiagnostics.CaptureRecorded().Frames.Last(frame =>
            frame.PresentedRankInputsKnown?.ContainsKey("rank-net") == true);
        Assert.Equal(1, initial.Presented);
        Assert.False(initial.PresentedRankInputsKnown!["rank-net"]);
        var pending = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Renderer.Pending = pending;
        values = values.ToDictionary(pair => pair.Key, pair => pair.Value);
        values["rank-net"] = values["rank-net"] with { Value = "-321" };
        fixture.Source.Frame = fixture.Source.Frame with { Metrics = values };
        fixture.Source.Publish();
        var waiting = OverlayEditorLiveDiagnostics.CaptureRecorded().Frames.Single(frame => frame.Window == initial.Window);
        Assert.Equal(2, waiting.Started);
        Assert.Equal(1, waiting.Captured);
        Assert.Equal(1, waiting.Presented);
        fixture.Renderer.Pending = null;
        pending.SetResult(new Bitmap(360, 260));
        DrainRender(fixture);
        var presented = OverlayEditorLiveDiagnostics.CaptureRecorded().Frames.Single(frame => frame.Window == initial.Window);
        Assert.Equal(2, presented.Presented);
        Assert.True(presented.PresentedRankInputsKnown!["rank-net"]);
        Assert.Equal(2, Assert.Single(fixture.Surfaces).Shows);
    });

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        Assert.True(condition());
    }

    private static void DrainRender(Fixture fixture)
    {
        var rendering = typeof(OverlayEditorLiveHostCoordinator).GetField("_rendering", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = Environment.TickCount64 + 3000;
        while ((bool)rendering.GetValue(fixture.Host)! && Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Assert.False((bool)rendering.GetValue(fixture.Host)!);
    }

    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private sealed class BufferedContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly ManualResetEventSlim _posted = new();
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _queue.Enqueue((callback, state));
            _posted.Set();
        }
        internal bool WaitForPost(TimeSpan timeout) => _posted.Wait(timeout);
        internal void RunOne()
        {
            if (!_queue.TryDequeue(out var work)) throw new InvalidOperationException("No render continuation was queued.");
            work.Callback(work.State);
        }
        public void Dispose() => _posted.Dispose();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Soulcrest-live-host-" + Guid.NewGuid().ToString("N"));
        private readonly Control _owner = new();
        internal string Path => System.IO.Path.Combine(_root, "overlay.json");
        internal OverlayService Editor { get; }
        internal Source Source { get; } = new();
        internal Renderer Renderer { get; } = new();
        internal List<Surface> Surfaces { get; } = [];
        internal OverlayEditorLiveHostCoordinator Host { get; }
        internal bool Allowed { get; set; } = true;
        internal bool HasMonitor { get; set; } = true;
        internal bool CaptureWindowOnly { get; set; }
        internal Fixture(bool marketWindow = false)
        {
            Editor = new(_root, new OverlayCollectionSettings());
            Editor.SaveAsync(Editor.Settings with { Enabled = true, Widgets = [OverlayCatalog.CreateWidget("duration")] }).GetAwaiter().GetResult();
            if (marketWindow)
            {
                Editor.CreateOverlayAsync("Market").GetAwaiter().GetResult();
                Editor.SaveAsync(Editor.Settings with { Enabled = true, Widgets = [OverlayCatalog.CreateWidget("market-kina")] }).GetAwaiter().GetResult();
            }
            _ = _owner.Handle;
            OverlayMonitor monitor = new("fixture", "Fixture", new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1040));
            Host = new(_owner, Editor, Source, Renderer, (_, snapshot) => Allowed && snapshot.SessionActive,
                () => HasMonitor ? [monitor] : [], () => monitor, () => CaptureWindowOnly, () => { var surface = new Surface(); Surfaces.Add(surface); return surface; });
        }
        internal void QueueOwnerAction(Action action) => _owner.BeginInvoke(action);
        public void Dispose() { Host.Dispose(); _owner.Dispose(); Editor.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    private sealed class Source : IOverlayEditorPresentationSource
    {
        internal OverlaySnapshot Frame { get; set; } = OverlaySnapshot.DemoFor("en") with { ItemAnimationsEnabled = false };
        internal Action? OnCapture { get; set; }
        internal int Captures { get; private set; }
        internal int ActiveLeases { get; private set; }
        internal List<bool> Markets { get; } = [];
        public event Action? Changed;
        internal void Publish() => Changed?.Invoke();
        public OverlaySnapshot Capture(bool examples = false, string? language = null)
        { Assert.False(examples); Captures++; var frame = Frame; OnCapture?.Invoke(); return frame; }
        public IDisposable Acquire(bool examples = false, bool market = false) { Assert.False(examples); ActiveLeases++; Markets.Add(market); return new Lease(this); }
        public void Dispose() { }
        private sealed class Lease(Source source) : IDisposable { public void Dispose() => source.ActiveLeases--; }
    }
    private sealed class Renderer : IOverlayEditorLiveRenderer
    {
        internal bool Throw { get; set; }
        internal TaskCompletionSource<Bitmap>? Pending { get; set; }
        internal List<Session> Sessions { get; } = [];
        public IOverlayEditorLiveRenderSession CreateSession() { var session = new Session(this); Sessions.Add(session); return session; }
        internal sealed class Session(Renderer owner) : IOverlayEditorLiveRenderSession
        {
            internal List<OverlaySnapshot> Snapshots { get; } = [];
            internal List<CancellationToken> Tokens { get; } = [];
            internal bool Disposed { get; private set; }
            internal Action? OnDispose { get; set; }
            public Task<Bitmap> UpdateAndCaptureAsync(OverlayInstance window, OverlaySnapshot snapshot, CancellationToken cancellationToken = default)
            {
                if (owner.Throw) throw new InvalidOperationException("synthetic renderer failure");
                Snapshots.Add(snapshot);
                Tokens.Add(cancellationToken);
                return owner.Pending?.Task ?? Task.FromResult(new Bitmap((int)window.Settings.Width, (int)window.Settings.Height));
            }
            public void Dispose() { Disposed = true; OnDispose?.Invoke(); }
        }
    }
    private sealed class Surface : IOverlayEditorLiveSurface
    {
        private bool _visible;
        public bool Visible { get { CheckAccess(); return _visible; } private set => _visible = value; }
        internal bool Disposed { get; private set; }
        internal int Shows { get; private set; }
        internal int Hides { get; private set; }
        internal bool VisibleInRecordings { get; private set; }
        internal Action? OnShow { get; set; }
        internal bool RejectAccessAfterDispose { get; set; }
        internal int AccessesAfterDispose { get; private set; }
        internal OverlayInstance? Configured { get; private set; }
        internal List<string?> PlacementCompletions { get; } = [];
        public event Action<OverlayLivePlacement>? PlacementCompleted;
        public void ConfigureInteraction(OverlayInstance window, Size logicalSize, OverlayMonitor monitor, string themeId, double? displayedScale = null) => Configured = window;
        public void CompletePlacement(string? error) { CheckAccess(); PlacementCompletions.Add(error); }
        internal void Place(Rectangle bounds, double scale, OverlaySettings settings) => PlacementCompleted?.Invoke(new(bounds, scale, "fixture", settings));
        public event Action? GeometryInvalidated;
        internal void ChangeDpi() => GeometryInvalidated?.Invoke();
        public void HideFrame() { CheckAccess(); Hides++; Visible = false; }
        public void ShowFrame(Bitmap image, Rectangle bounds, bool visibleInRecordings)
        { CheckAccess(); VisibleInRecordings = visibleInRecordings; Shows++; Visible = true; OnShow?.Invoke(); }
        public void Dispose() { Visible = false; Disposed = true; }
        private void CheckAccess()
        {
            if (!Disposed || !RejectAccessAfterDispose) return;
            AccessesAfterDispose++;
            throw new InvalidOperationException("Synthetic disposed surface access");
        }
    }
}
