using System.Collections.Immutable;
using System.Drawing;
using System.Reflection;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Own offscreen HWNDs only; no app host, game, capture or desktop input.</summary>
[Collection("Settings file")]
public sealed class AchievementOverlayFormTests
{
    [Fact]
    public Task NativeWindowIsNoActivateClickThroughAndRestoresLockAfterPresentationGates() => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var panel = fixture.Panel();
        _ = panel.Handle;
        panel.Show();
        Assert.True(panel.Visible);
        Assert.True(panel.Locked);
        var style = NativeMethods.GetWindowLongPtr(panel.Handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        Assert.NotEqual(0, style & NativeMethods.WS_EX_NOACTIVATE);
        Assert.NotEqual(0, style & NativeMethods.WS_EX_TRANSPARENT);
        panel.ToggleManualInteraction(); Assert.False(panel.Locked);
        panel.SetPlacementHidden(true); Assert.False(panel.Visible); Assert.True(panel.Locked);
        panel.SetPlacementHidden(false); Assert.True(panel.Visible); Assert.True(panel.Locked);
        panel.ToggleManualInteraction(); Assert.False(panel.Locked);
        panel.SetForegroundSuppressed(true); Assert.False(panel.Visible); Assert.True(panel.Locked);
        panel.SetForegroundSuppressed(false); Assert.True(panel.Visible); Assert.True(panel.Locked);
        panel.SetSuppressed(true); Assert.False(panel.Visible);
        panel.SetSuppressed(false); Assert.True(panel.Visible);
        Assert.Equal((-30000, -30000), (fixture.Settings.Current.AchievementOverlayX, fixture.Settings.Current.AchievementOverlayY));
    });

    [Fact]
    public Task AllRowsRemainScrollableAndCharacterChangesResetTheViewport() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Snapshot = fixture.Snapshot with { Rows = Enumerable.Range(0, 30).Select(index => AchievementOverlayRendererTests.Example(index + 1)).ToImmutableArray() };
        using var panel = fixture.Panel();
        _ = panel.Handle; panel.Show();
        Assert.InRange(panel.Height, 1, (int)(Screen.FromPoint(panel.Location).WorkingArea.Height * .8));
        panel.ScrollRows(10000); Assert.True(panel.ScrollOffset > 0);
        fixture.Snapshot = fixture.Snapshot with { Binding = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")) };
        panel.ApplySettings(); Assert.Equal(0, panel.ScrollOffset);
    });

    [Fact]
    public Task HiddenAnimationStillAdvancesServiceDeadlinesAndNeverChangesTheOverlayPreference() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Snapshot = fixture.Snapshot with { Rows = [AchievementOverlayRendererTests.Example() with { State = AchievementTrackerRowState.Celebrating }] };
        using var panel = fixture.Panel();
        _ = panel.Handle; panel.Show(); panel.ApplySettings();
        Assert.True(panel.AnimationRunning);
        panel.SetForegroundSuppressed(true);
        Assert.False(panel.AnimationRunning);
        var before = fixture.Ticks;
        fixture.Snapshot = fixture.Snapshot with { Rows = [] };
        typeof(AchievementOverlayForm).GetMethod("RefreshTick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(panel, [null, EventArgs.Empty]);
        Assert.True(fixture.Ticks > before);
        panel.SetForegroundSuppressed(false);
        Assert.False(panel.Visible);
        Assert.True(fixture.Settings.Current.AchievementOverlayEnabled);
    });

    [Fact]
    public Task DefaultPositionUsesPrimaryWorkingAreaAndIndependentScaleSettings() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Settings.Current.AchievementOverlayX = null;
        fixture.Settings.Current.AchievementOverlayY = null;
        fixture.Settings.Current.AchievementOverlayScale = 1.25;
        var petScale = fixture.Settings.Current.OverlayScale;
        using var panel = fixture.Panel();
        Assert.Equal(450, panel.Width);
        var working = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        Assert.Equal(new Point(working.Right - panel.Width - 24, working.Top + 24), panel.Location);
        Assert.Equal(petScale, fixture.Settings.Current.OverlayScale);
    });

    private sealed class Fixture : IDisposable
    {
        private readonly byte[]? _backup;
        private readonly string _language = UiText.Language;
        public SettingsService Settings { get; }
        public int Ticks { get; private set; }
        public AchievementTrackerSnapshot Snapshot { get; set; } = new(
            new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")), [AchievementOverlayRendererTests.Example()]) { IsLive = true };
        public Fixture()
        {
            var redirected = Environment.GetEnvironmentVariable("SOULCREST_DATA");
            Assert.False(string.IsNullOrWhiteSpace(redirected), "Native overlay tests require isolated SOULCREST_DATA.");
            Assert.Equal(Path.GetFullPath(redirected!), Path.GetFullPath(AppPaths.DataDirectory), ignoreCase: true);
            _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
            Settings = new();
            Settings.Current.AchievementOverlayEnabled = true;
            Settings.Current.AchievementOverlayX = Settings.Current.AchievementOverlayY = -30000;
            Settings.Current.AchievementOverlayScale = 1;
        }
        public AchievementOverlayForm Panel() => new(() => Snapshot, () => Ticks++, Settings);
        public void Dispose()
        {
            if (_backup is null) { if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile); }
            else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
            UiText.Language = _language;
        }
    }
    private static Task RunSta(Action action)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); complete.SetResult(); } catch (Exception error) { complete.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return complete.Task;
    }
}
