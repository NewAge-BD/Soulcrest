using System.Reflection;
using System.Windows.Forms;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Own offscreen windows and synthetic facts only; no application, capture, network or user-data services.</summary>
[Collection("Settings file")]
public sealed class AbyssRankLiveOverlayTests
{
    [Fact]
    public Task SavedValuesAndTheFirstConfirmationAreBaselinesAndSettingsNeverRestartMotion() => OnSta(() =>
    {
        using var fixture = new Fixture(Points(8, 0) with { Confirmed = false });
        fixture.Ready();
        Assert.False(fixture.Form.MotionAnimating);
        fixture.Publish(Points(8, 1));
        fixture.Publish(Points(8, 2) with { RankPoints = AbyssRankCatalog.Default.Stages[8].Threshold + 500 });
        Assert.False(fixture.Form.MotionAnimating);
        fixture.Publish(Points(9, 3));
        Assert.True(fixture.Form.MotionAnimating);
        Assert.True(fixture.Form.MotionTimerEnabled);
        Assert.Single(fixture.SoundStarts);

        fixture.Now = 300;
        var sourceReads = fixture.Source.Reads;
        var optionReads = fixture.OptionReads;
        fixture.Form.RenderAnimationFrame();
        var travelling = Assert.IsType<AbyssRankMotionFrame>(fixture.Form.CurrentMotionFrame);
        Assert.InRange(travelling.CenterX, 33, 239);
        Assert.Equal(sourceReads, fixture.Source.Reads);
        Assert.Equal(optionReads, fixture.OptionReads);
        for (var index = 0; index < 4; index++) fixture.Form.ApplySettings();
        Assert.Equal(travelling.ElapsedMs, fixture.Form.CurrentMotionFrame!.ElapsedMs);
        Assert.Single(fixture.SoundStarts);
        fixture.Now = AbyssRankMotionTimeline.MorphStartMs;
        fixture.Form.RenderAnimationFrame();
        Assert.Equal(240, fixture.Form.CurrentMotionFrame!.CenterX);
        fixture.Now = AbyssRankMotionTimeline.UpDurationMs;
        fixture.Form.RenderAnimationFrame();
        Assert.False(fixture.Form.MotionAnimating);
        Assert.False(fixture.Form.MotionTimerEnabled);
        Assert.Null(fixture.Form.CurrentMotionFrame);
    });

    [Fact]
    public Task NewTargetsReachTheNewestConfirmedStageWithoutStackingRankUpSounds() => OnSta(() =>
    {
        using var fixture = new Fixture(Points(8, 1));
        fixture.Ready();
        fixture.Publish(Points(9, 2));
        fixture.Now = 600;
        fixture.Form.RenderAnimationFrame();
        var stopsBeforeReversal = fixture.SoundStops;
        fixture.Publish(Points(7, 3));
        Assert.True(fixture.SoundStops > stopsBeforeReversal);
        Assert.Single(fixture.SoundStarts);
        Assert.Equal(fixture.Source.Snapshot.RankPoints, fixture.Form.CurrentMotionFrame!.TotalPoints);
        fixture.Now = AbyssRankMotionTimeline.MorphStartMs + AbyssRankMotionTimeline.FirstMorphDurationMs;
        fixture.Form.RenderAnimationFrame();
        Assert.Equal(240, fixture.Form.CurrentMotionFrame!.CenterX);
        Assert.True(fixture.SoundStops > stopsBeforeReversal);
        fixture.Publish(Points(12, 4));
        Assert.Single(fixture.SoundStarts);
        fixture.Now = 20_000;
        fixture.Form.RenderAnimationFrame();
        Assert.False(fixture.Form.MotionAnimating);
        Assert.Null(fixture.Form.CurrentMotionFrame);
        fixture.Publish(Points(13, 5));
        Assert.Equal(2, fixture.SoundStarts.Count);
    });

    [Fact]
    public Task AChangedUpwardTargetStopsItsObsoleteLandingCueWithoutStartingASecondLayer() => OnSta(() =>
    {
        using var fixture = new Fixture(Points(8, 1));
        fixture.Ready();
        fixture.Publish(Points(12, 2));
        fixture.Now = 200;
        fixture.Form.RenderAnimationFrame();
        var stops = fixture.SoundStops;
        fixture.Publish(Points(13, 1)); // Old sequence must not cancel a valid cue.
        Assert.Equal(stops, fixture.SoundStops);
        fixture.Publish(Points(12, 3) with { RankPoints = Points(12, 3).RankPoints + 10 });
        Assert.Equal(stops, fixture.SoundStops); // Same destination stage keeps the same timing.
        fixture.Publish(Points(11, 4));
        fixture.Now = 540;
        fixture.Form.RenderAnimationFrame();
        Assert.True(fixture.SoundStops > stops);
        Assert.Single(fixture.SoundStarts);
        Assert.True(fixture.Form.CurrentMotionFrame!.MotionToStageIndex > fixture.Form.CurrentMotionFrame.MotionFromStageIndex);
    });

    [Theory]
    [InlineData("profile", false)]
    [InlineData("profile", true)]
    [InlineData("revision", true)]
    [InlineData("unknown", true)]
    [InlineData("tracking", true)]
    public Task ContextLossStopsTheSoundTailEvenAfterTheMotionHasAlreadyFinished(string change, bool renderFinish) => OnSta(() =>
    {
        using var fixture = new Fixture(Points(8, 1));
        fixture.Ready();
        fixture.Publish(Points(9, 2));
        if (renderFinish)
        {
            fixture.Now = AbyssRankMotionTimeline.UpDurationMs;
            fixture.Form.RenderAnimationFrame();
            Assert.False(fixture.Form.MotionAnimating);
        }
        var stops = fixture.SoundStops;
        fixture.Now = AbyssRankMotionTimeline.UpDurationMs + 240;
        var changed = change switch
        {
            "profile" => Points(3, 3) with { ProfileId = "synthetic-other-profile", BindingGeneration = 2 },
            "revision" => Points(11, 3) with { RankContinuityRevision = 1 },
            "unknown" => Points(9, 3) with { RankPoints = null, Confirmed = false },
            _ => Points(9, 3) with { RankTrackingActive = false }
        };
        fixture.Publish(changed);
        Assert.True(fixture.SoundStops > stops);
        Assert.Single(fixture.SoundStarts);
        Assert.False(fixture.Form.MotionAnimating);
        Assert.False(fixture.Form.MotionTimerEnabled);
    });

    [Fact]
    public Task CoalescedContinuityLossAndCharacterChangesCannotBecomeRankAnimations() => OnSta(() =>
    {
        using var fixture = new Fixture(Points(8, 1));
        fixture.Ready();
        fixture.Publish(Points(9, 2));
        Assert.True(fixture.Form.MotionAnimating);
        fixture.Source.Publish(Points(9, 3) with { Confirmed = false, RankTrackingActive = false, RankContinuityRevision = 1 });
        fixture.Source.Publish(Points(11, 4) with { RankContinuityRevision = 1 });
        Application.DoEvents();
        Assert.False(fixture.Form.MotionAnimating);
        Assert.False(fixture.Form.MotionTimerEnabled);
        fixture.Publish(Points(12, 5) with { RankContinuityRevision = 1 });
        Assert.True(fixture.Form.MotionAnimating);
        fixture.Publish(Points(3, 1) with { ProfileId = "synthetic-other-profile", BindingGeneration = 2, RankContinuityRevision = 2 });
        Assert.False(fixture.Form.MotionAnimating);
        Assert.False(fixture.Form.MotionTimerEnabled);
    });

    [Fact]
    public Task VisibilityDisableAndMotionModeChangesStopAndResumeWithSilentBaselines() => OnSta(() =>
    {
        using var fixture = new Fixture(Points(8, 1));
        fixture.Ready();
        fixture.Publish(Points(9, 2));
        fixture.Form.SetForegroundSuppressed(true);
        Assert.False(fixture.Form.Visible);
        Assert.False(fixture.Form.MotionTimerEnabled);
        fixture.Publish(Points(10, 3));
        fixture.Form.SetForegroundSuppressed(false);
        Assert.True(fixture.Form.Visible);
        Assert.False(fixture.Form.MotionAnimating);
        fixture.Publish(Points(11, 4));
        Assert.True(fixture.Form.MotionAnimating);
        fixture.Options = fixture.Options with { Enabled = false };
        fixture.Form.ApplySettings();
        Assert.False(fixture.Form.Visible);
        Assert.False(fixture.Form.MotionAnimating);
        fixture.Options = fixture.Options with { Enabled = true };
        fixture.Form.ApplySettings();
        Assert.False(fixture.Form.MotionAnimating);
        fixture.Publish(Points(12, 5));
        Assert.True(fixture.Form.MotionAnimating);
        fixture.Options = fixture.Options with { ReducedMotion = true };
        fixture.Form.ApplySettings();
        Assert.False(fixture.Form.MotionAnimating);
        fixture.Publish(Points(13, 6));
        fixture.Now = 100;
        fixture.Form.RenderAnimationFrame();
        Assert.True(fixture.Form.MotionAnimating);
        Assert.Equal(32, fixture.Form.CurrentMotionFrame!.CenterX);
        Assert.True(fixture.SoundStarts[^1].Reduced);
        fixture.Publish(Points(13, 7) with { WorldContextVisible = false, RankContinuityRevision = 1 });
        Assert.False(fixture.Form.Visible);
        Assert.False(fixture.Form.MotionAnimating);
        fixture.Publish(Points(14, 8) with { RankContinuityRevision = 2 });
        Assert.True(fixture.Form.Visible);
        Assert.False(fixture.Form.MotionAnimating);
    });

    [Fact]
    public Task HandleRecreationAndDisposalCancelPendingCallbacksAndDisconnectTheSource() => OnSta(() =>
    {
        using var fixture = new Fixture(Points(8, 1));
        fixture.Ready();
        fixture.Publish(Points(9, 2));
        fixture.Source.Publish(Points(10, 3)); // Leave one refresh queued on the old HWND.
        typeof(Control).GetMethod("DestroyHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Form, null);
        Assert.False(fixture.Form.IsHandleCreated);
        Assert.False(fixture.Form.MotionTimerEnabled);
        Assert.False(fixture.Form.MotionAnimating);
        fixture.Source.Publish(Points(11, 4));
        _ = fixture.Form.Handle;
        Application.DoEvents();
        Assert.False(fixture.Form.MotionAnimating);
        fixture.Publish(Points(12, 5));
        Assert.True(fixture.Form.MotionAnimating);
        fixture.Form.Dispose();
        Assert.Equal(0, fixture.Source.Subscribers);
        Assert.False(fixture.Form.MotionAnimating);
        fixture.Source.Publish(Points(13, 6));
        Application.DoEvents();
        Assert.False(fixture.Form.IsHandleCreated);
    });

    private static AbyssRankOverlaySnapshot Points(int stage, long sequence) =>
        new(AbyssRankCatalog.Default.Stages[stage].Threshold + 100, true, true)
        {
            ProfileId = "synthetic-profile", BindingGeneration = 1, RankObservationSequence = sequence,
            RankPointsObservedAt = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(sequence),
            RankTrackingActive = true, WorldContextVisible = true
        };

    private sealed class Fixture : IDisposable
    {
        internal Source Source { get; }
        internal AbyssRankOverlayOptions Options { get; set; } = new(true, -30_000, -30_000);
        internal AbyssRankOverlayForm Form { get; }
        internal double Now { get; set; }
        internal int OptionReads { get; private set; }
        internal int SoundStops { get; private set; }
        internal List<(long From, long To, bool Reduced)> SoundStarts { get; } = [];

        internal Fixture(AbyssRankOverlaySnapshot baseline)
        {
            Source = new(baseline);
            Form = new(Source, () => { OptionReads++; return Options; }, null,
                (from, to, reduced) => SoundStarts.Add((from, to, reduced)), () => SoundStops++, () => Now);
            _ = Form.Handle;
        }

        internal void Ready()
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!Form.MotionAssetsReady && wait.Elapsed < TimeSpan.FromSeconds(15))
            { Application.DoEvents(); Thread.Sleep(1); }
            Assert.True(Form.MotionAssetsReady, "Bundled morph atlases must finish warming before motion is enabled.");
            Application.DoEvents();
            Form.ApplySettings();
            SoundStarts.Clear(); SoundStops = 0;
        }

        internal void Publish(AbyssRankOverlaySnapshot snapshot)
        { Source.Publish(snapshot); Application.DoEvents(); }
        public void Dispose() => Form.Dispose();
    }

    private sealed class Source(AbyssRankOverlaySnapshot initial) : IAbyssRankOverlaySource
    {
        private AbyssRankOverlaySnapshot _snapshot = initial;
        private Action? _changed;
        internal int Subscribers { get; private set; }
        internal int Reads { get; private set; }
        public AbyssRankOverlaySnapshot Snapshot { get { Reads++; return Volatile.Read(ref _snapshot); } }
        public event Action? Changed
        {
            add { Subscribers++; _changed += value; }
            remove { Subscribers--; _changed -= value; }
        }
        internal void Publish(AbyssRankOverlaySnapshot snapshot)
        { Volatile.Write(ref _snapshot, snapshot); _changed?.Invoke(); }
    }

    private static Task OnSta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
