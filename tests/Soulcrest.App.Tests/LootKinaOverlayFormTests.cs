using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Only Soulcrest's isolated own offscreen window; no app host, game, capture or user data.</summary>
[Collection("Settings file")]
public sealed class LootKinaOverlayFormTests
{
    [Fact]
    public Task SessionStartCanAcceptItsFirstRareReceiptBeforeTheUiCallbackWithoutShowingOrInvokingTheForm() => RunSta(() =>
    {
        var source = new Source();
        var options = new LootOverlayOptions(false, -30_000, -30_000) { ShowOnSessionStart = true };
        using var alerts = new RareDropAlertService(() => new(SoundEnabled: false),
            () => throw new InvalidOperationException("No audio in a form regression."), TimeProvider.System);
        using var panel = new LootOverlayForm(source, () => options, alerts: alerts);
        _ = panel.Handle;
        Assert.False(panel.Visible);
        options = options with { Enabled = true }; // Host persisted the explicit auto-show decision; UI refresh is still queued.
        Task.Run(() =>
        {
            panel.PrepareAlertsForSessionStart();
            alerts.Accept(new("example-session", "example-profile", DateTimeOffset.UtcNow,
                "Item:210230006", "Example rare item", 1));
        }).GetAwaiter().GetResult();
        Assert.False(panel.Visible); // Preparing a receipt never touches the window from the worker.
        Assert.Single(alerts.CurrentNotices);
        Application.DoEvents();
        Assert.True(panel.Visible);
        Assert.Single(Field<LootOverlaySnapshot>(panel, "_snapshot").RareDrops);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SessionOnlyKeepsTheFirstRareReceiptWithAutoShowOrAnAlreadyEnabledOverlay(bool alreadyEnabled) => RunSta(() =>
    {
        var source = new Source();
        var options = new LootOverlayOptions(alreadyEnabled, -30_000, -30_000)
            { OnlyDuringSession = true, ShowOnSessionStart = !alreadyEnabled };
        using var alerts = new RareDropAlertService(() => new(SoundEnabled: false),
            () => throw new InvalidOperationException("No audio in a form regression."), TimeProvider.System);
        using var panel = new LootOverlayForm(source, () => options, alerts: alerts);
        _ = panel.Handle; AssertHidden(panel);
        Assert.False(Field<LootOverlaySnapshot>(panel, "_snapshot").SessionActive);
        source.Publish(new("new-session", true, 0, null, [])); // Published before the session-start callback.
        options = options with { Enabled = true }; // Auto-show may enable it; otherwise this is unchanged.
        Task.Run(() =>
        {
            panel.PrepareAlertsForSessionStart();
            alerts.Accept(new("new-session", "example-profile", DateTimeOffset.UtcNow,
                "Item:210230006", "Example rare item", 1));
        }).GetAwaiter().GetResult();
        AssertHidden(panel); Assert.Single(alerts.CurrentNotices);
        panel.ApplySettings(); // A queued UI refresh must use the committed active snapshot first.
        Application.DoEvents();
        Assert.True(panel.Visible); Assert.Single(alerts.CurrentNotices);
        Assert.Single(Field<LootOverlaySnapshot>(panel, "_snapshot").RareDrops);
        source.Publish(LootOverlaySnapshot.Empty); Application.DoEvents();
        AssertHidden(panel); Assert.Empty(alerts.CurrentNotices); Assert.True(options.Enabled);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AQueuedUiRefreshWithOldOptionsCannotEraseTheFirstPreparedSessionReceipt(bool alreadyEnabled) => RunSta(() =>
    {
        var source = new Source();
        var options = new LootOverlayOptions(alreadyEnabled, -30_000, -30_000) { OnlyDuringSession = true };
        var pauseNextRead = false;
        using var readStarted = new ManualResetEventSlim(); using var prepared = new ManualResetEventSlim();
        LootOverlayOptions ReadOptions()
        {
            var captured = options;
            if (pauseNextRead)
            {
                pauseNextRead = false;
                readStarted.Set();
                if (!prepared.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic prepare wait expired");
            }
            return captured;
        }
        using var alerts = new RareDropAlertService(() => new(SoundEnabled: false),
            () => throw new InvalidOperationException("No audio in a form regression."), TimeProvider.System);
        using var panel = new LootOverlayForm(source, ReadOptions, alerts: alerts);
        _ = panel.Handle; AssertHidden(panel);
        var worker = Task.Run(() =>
        {
            try
            {
                if (!readStarted.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic UI wait expired");
                source.Snapshot = new("new-session", true, 0, null, []);
                options = options with { Enabled = true };
                panel.PrepareAlertsForSessionStart();
                alerts.Accept(new("new-session", "example-profile", DateTimeOffset.UtcNow,
                    "Item:210230006", "Example rare item", 1));
            }
            finally { prepared.Set(); }
        });
        pauseNextRead = true;
        panel.ApplySettings(); // This refresh captured the old master toggle before the worker prepared its receipt.
        worker.GetAwaiter().GetResult();
        Assert.Single(alerts.CurrentNotices);
        source.Publish(source.Snapshot); Application.DoEvents();
        Assert.True(panel.Visible); Assert.Single(alerts.CurrentNotices);
        Assert.Single(Field<LootOverlaySnapshot>(panel, "_snapshot").RareDrops);
    });

    [Theory]
    [InlineData(LootTrackerSessionKind.Mixed)]
    [InlineData(LootTrackerSessionKind.Lifeskill)]
    [InlineData(LootTrackerSessionKind.Abyss)]
    public Task SessionOnlyFollowsTheLiveSessionWithoutChangingTheSavedOverlayPreference(LootTrackerSessionKind kind) => RunSta(() =>
    {
        var source = new Source { Snapshot = LootOverlaySnapshot.Empty with { DailyMonsterKina = 500 } };
        var options = new LootOverlayOptions(true, -30_000, -30_000) { OnlyDuringSession = true };
        using var panel = new LootOverlayForm(source, () => options);
        var handle = panel.Handle; AssertHidden(panel);
        options = options with { OnlyDuringSession = false }; panel.ApplySettings(); Assert.True(panel.Visible);
        options = options with { OnlyDuringSession = true }; panel.ApplySettings(); AssertHidden(panel);
        var active = LootKinaOverlayRendererTests.Example() with { SessionKind = kind };
        source.Publish(active); Application.DoEvents(); Assert.True(panel.Visible); Assert.True(Timer(panel).Enabled);
        panel.SetForegroundSuppressed(true); AssertHidden(panel);
        source.Publish(LootOverlaySnapshot.Empty); Application.DoEvents();
        panel.SetForegroundSuppressed(false); AssertHidden(panel);
        source.Publish(active with { SessionId = "next-session" }); Application.DoEvents();
        Assert.True(panel.Visible); Assert.Equal(handle, panel.Handle); Assert.True(options.Enabled);
        Assert.False(options.ShowOnSessionStart); Assert.True(options.OnlyDuringSession);
        Assert.Equal((-30_000, -30_000), (options.X, options.Y));
        options = options with { Enabled = false }; panel.ApplySettings(); AssertHidden(panel);
    });

    [Fact]
    public Task ASessionEndingBetweenTimerTicksHidesAndStopsWithoutAQueuedTickReopeningIt() => RunSta(() =>
    {
        var source = new Source { Snapshot = LootKinaOverlayRendererTests.Example() };
        var options = new LootOverlayOptions(true, -30_000, -30_000) { OnlyDuringSession = true };
        using var panel = new LootOverlayForm(source, () => options);
        _ = panel.Handle; Assert.True(panel.Visible);
        source.Snapshot = LootOverlaySnapshot.Empty; // Timer discovers this before a coalesced Changed callback.
        typeof(LootOverlayForm).GetMethod("TimerTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(panel, [null, EventArgs.Empty]);
        AssertHidden(panel); Assert.True(options.Enabled);
        source.Snapshot = LootKinaOverlayRendererTests.Example();
        typeof(LootOverlayForm).GetMethod("TimerTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(panel, [null, EventArgs.Empty]);
        AssertHidden(panel);
        source.Publish(source.Snapshot); Application.DoEvents(); Assert.True(panel.Visible);
    });

    [Theory]
    [InlineData("foreground")]
    [InlineData("suppressed")]
    [InlineData("placement")]
    [InlineData("disposed")]
    public Task SessionStartPreparationCannotRetainHiddenReceiptsOrReplayThemLater(string hidden) => RunSta(() =>
    {
        var source = new Source();
        var options = new LootOverlayOptions(false, -30_000, -30_000) { OnlyDuringSession = true };
        using var alerts = new RareDropAlertService(() => new(SoundEnabled: false),
            () => throw new InvalidOperationException("No audio in a form regression."), TimeProvider.System);
        using var panel = new LootOverlayForm(source, () => options, alerts: alerts);
        _ = panel.Handle;
        switch (hidden)
        {
            case "foreground": panel.SetForegroundSuppressed(true); break;
            case "suppressed": panel.SetSuppressed(true); break;
            case "placement": panel.SetPlacementHidden(true); break;
            case "disposed": panel.Dispose(); break;
        }
        source.Snapshot = new("example-session", true, 0, null, []);
        options = options with { Enabled = true };
        Task.Run(() =>
        {
            panel.PrepareAlertsForSessionStart();
            alerts.Accept(new("example-session", "example-profile", DateTimeOffset.UtcNow,
                "Item:210230006", "Example rare item", 1));
        }).GetAwaiter().GetResult();
        Assert.Empty(alerts.CurrentNotices);
        if (hidden != "disposed")
        {
            panel.SetForegroundSuppressed(false); panel.SetSuppressed(false); panel.SetPlacementHidden(false);
            Application.DoEvents();
            Assert.True(panel.Visible);
            Assert.Empty(alerts.CurrentNotices);
        }
    });

    [Fact]
    public Task DisabledHandleDoesNotShowOrStartTheTimerAndDisposalUnsubscribesTheSource() => RunSta(() =>
    {
        var source = new Source();
        using var panel = new LootOverlayForm(source, () => new());
        Assert.False(panel.IsHandleCreated);
        Assert.Equal(1, source.Subscribers);
        _ = panel.Handle;
        Assert.False(panel.Visible);
        Assert.False(Timer(panel).Enabled);
        AssertClickThrough(panel, true);
        panel.Dispose();
        Assert.Equal(0, source.Subscribers);
    });

    [Fact]
    public Task VisibilityModesStopTheTimerAndRestoreTheLatestSnapshotWithoutChangingThePreference() => RunSta(() =>
    {
        var source = new Source { Snapshot = LootKinaOverlayRendererTests.Example() };
        var options = new LootOverlayOptions(true, -30_000, -30_000);
        using var panel = new LootOverlayForm(source, () => options, null, null, () => true);
        _ = panel.Handle;
        Assert.True(panel.Visible);
        Assert.True(Lock(panel).Visible);
        Assert.True(Timer(panel).Enabled);
        Assert.Equal(33, Timer(panel).Interval);
        var handle = panel.Handle;

        panel.SetForegroundSuppressed(true);
        AssertHidden(panel);
        source.Publish(source.Snapshot with { SessionActive = false, Drops = [] });
        Application.DoEvents();
        AssertHidden(panel);
        panel.SetForegroundSuppressed(false);
        Assert.True(panel.Visible);
        Assert.Equal(1000, Timer(panel).Interval);
        Assert.Equal(LootOverlayRenderer.Measure(source.Snapshot), panel.Size);
        Assert.Equal(handle, panel.Handle);

        panel.SetPlacementHidden(true);
        AssertHidden(panel);
        panel.SetPlacementHidden(false);
        Assert.True(panel.Visible);
        panel.SetSuppressed(true);
        AssertHidden(panel);
        panel.SetSuppressed(false);
        Assert.True(panel.Visible);
        Assert.True(options.Enabled);
        Assert.Equal((-30_000, -30_000), (options.X, options.Y));
        options = options with { Enabled = false };
        panel.ApplySettings();
        AssertHidden(panel);
    });

    [Fact]
    public Task WindowsMotionChangesStopEffectsWithoutStoppingStaticRefreshOrReopeningAHiddenOverlay() => RunSta(() =>
    {
        var source = new Source { Snapshot = LootKinaOverlayRendererTests.Example() };
        var options = new LootOverlayOptions(true, -30_000, -30_000);
        bool? windowsAnimations = true;
        using var panel = new LootOverlayForm(source, () => options, null, null, () => windowsAnimations);
        _ = panel.Handle;
        Assert.Equal(33, Timer(panel).Interval);

        windowsAnimations = false;
        MotionPreferenceChanged(panel);
        Assert.False(Field<LootOverlaySnapshot>(panel, "_snapshot").AnimationsEnabled);
        Assert.True(Timer(panel).Enabled);
        Assert.Equal(1000, Timer(panel).Interval); // Duration and rates still update in static mode.
        Assert.Equal(source.Snapshot.SessionId, Field<LootOverlaySnapshot>(panel, "_snapshot").SessionId);

        windowsAnimations = null;
        MotionPreferenceChanged(panel);
        Assert.False(Field<LootOverlaySnapshot>(panel, "_snapshot").AnimationsEnabled);
        windowsAnimations = true;
        options = options with { AnimationsEnabled = false };
        MotionPreferenceChanged(panel);
        Assert.Equal(1000, Timer(panel).Interval); // Windows cannot override the explicit app opt-out.

        options = options with { AnimationsEnabled = true };
        source.Snapshot = source.Snapshot with { AnimationsEnabled = false };
        MotionPreferenceChanged(panel);
        Assert.False(Field<LootOverlaySnapshot>(panel, "_snapshot").AnimationsEnabled);
        source.Snapshot = source.Snapshot with { AnimationsEnabled = true };
        MotionPreferenceChanged(panel);
        Assert.Equal(33, Timer(panel).Interval);

        panel.SetPlacementHidden(true);
        windowsAnimations = false;
        MotionPreferenceChanged(panel);
        windowsAnimations = true;
        MotionPreferenceChanged(panel);
        AssertHidden(panel);
        Assert.True(options.Enabled);
        panel.SetPlacementHidden(false);
        Assert.True(panel.Visible);
        Assert.Equal(33, Timer(panel).Interval);
    });

    [Fact]
    public Task ManualLockEndsAnOwnWindowDragSavesOnlyThePositionAndWaitsForAltRelease() => RunSta(() =>
    {
        var source = new Source { Snapshot = LootKinaOverlayRendererTests.Example() };
        var options = new LootOverlayOptions(true, -30_000, -30_000);
        var saves = new List<Point>();
        using var panel = new LootOverlayForm(source, () => options, point =>
        {
            saves.Add(point);
            options = options with { X = point.X, Y = point.Y };
        });
        var foreground = GetForegroundWindow();
        _ = panel.Handle;
        panel.ToggleManualInteraction();
        Assert.True(panel.ManualInteractionEnabled);
        AssertClickThrough(panel, false);
        panel.SetInteractionEnabled(false);
        AssertClickThrough(panel, false);
        Mouse(panel, "OnMouseDown", new(MouseButtons.Left, 1, 30, 20, 0));
        Assert.True(panel.Capture);
        Mouse(panel, "OnMouseMove", new(MouseButtons.Left, 0, 50, 40, 0));
        panel.SetInteractionEnabled(true);
        panel.ToggleManualInteraction();
        Assert.False(panel.Capture);
        Assert.False(panel.ManualInteractionEnabled);
        AssertClickThrough(panel, true);
        Assert.Equal(new Point(-29_980, -29_980), Assert.Single(saves));
        panel.SetInteractionEnabled(true);
        AssertClickThrough(panel, true);
        panel.SetInteractionEnabled(false);
        panel.SetInteractionEnabled(true);
        AssertClickThrough(panel, false);
        panel.SetInteractionEnabled(false);
        AssertClickThrough(panel, true);
        Assert.Equal(foreground, GetForegroundWindow());
    });

    [Fact]
    public Task ThemeAndScaleChangesKeepTheSameOwnWindowAndCaptureVisibility() => RunSta(() =>
    {
        var source = new Source { Snapshot = LootKinaOverlayRendererTests.Example() };
        var options = new LootOverlayOptions(true, -30_000, -30_000);
        using var panel = new LootOverlayForm(source, () => options);
        var handle = panel.Handle;
        foreach (var theme in OverlayThemeDefinition.All)
        {
            options = options with { Theme = theme.Id, Scale = .8 };
            panel.ApplySettings();
            Assert.Equal(handle, panel.Handle);
            Assert.True(panel.Visible);
            AssertClickThrough(panel, true);
            Assert.Equal(384, panel.Width); // 480-pixel HUD at the configured 80% scale.
            if (GetWindowDisplayAffinity(handle, out var affinity))
                Assert.Equal((uint)NativeMethods.WDA_EXCLUDEFROMCAPTURE, affinity);
        }
    });

    private static void AssertHidden(LootOverlayForm panel)
    {
        Assert.False(panel.Visible);
        Assert.False(Lock(panel).Visible);
        Assert.False(panel.Capture);
        Assert.False(panel.ManualInteractionEnabled);
        Assert.False(Timer(panel).Enabled);
        AssertClickThrough(panel, true);
    }

    private static OverlayLockButton Lock(LootOverlayForm panel) => Field<OverlayLockButton>(panel, "_lockButton");
    private static System.Windows.Forms.Timer Timer(LootOverlayForm panel) => Field<System.Windows.Forms.Timer>(panel, "_timer");
    private static T Field<T>(LootOverlayForm panel, string name) => (T)typeof(LootOverlayForm)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
    private static void Mouse(LootOverlayForm panel, string name, MouseEventArgs e) => typeof(LootOverlayForm)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [e]);
    private static void MotionPreferenceChanged(LootOverlayForm panel) => typeof(LootOverlayForm)
        .GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(panel, [Message.Create(panel.Handle, 0x001A, 0, 0)]);
    private static void AssertClickThrough(LootOverlayForm panel, bool expected) =>
        Assert.Equal(expected, (NativeMethods.GetWindowLongPtr(panel.Handle, NativeMethods.GWL_EXSTYLE)
            & NativeMethods.WS_EX_TRANSPARENT) != 0);

    private sealed class Source : ILootOverlaySource
    {
        private Action? _changed;
        public LootOverlaySnapshot Snapshot { get; set; } = LootOverlaySnapshot.Empty;
        internal int Subscribers { get; private set; }
        public event Action? Changed
        {
            add { _changed += value; Subscribers++; }
            remove { _changed -= value; Subscribers--; }
        }
        internal void Publish(LootOverlaySnapshot snapshot) { Snapshot = snapshot; _changed?.Invoke(); }
    }

    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
}
