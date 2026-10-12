using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class EventTimelineOverlayTests : IDisposable
{
    private readonly byte[]? _originalSettings;
    private readonly string _originalLanguage = UiText.Language;
    private readonly string? _originalMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Soulcrest-timeline-overlay-" + Guid.NewGuid().ToString("N"));

    public EventTimelineOverlayTests()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "manifest.json"), """{"maps":[]}""");
        Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _directory);
        _originalSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            EventTimelineOverlayEnabled = true, EventTimelineOverlayScale = 1, EventTimelineOverlayWidth = 720,
            EventTimelineOverlayX = 200, EventTimelineOverlayY = 180, EventTimelineHours = 12,
            AlertsOverlayEnabled = false, UiLanguage = "en", CheckUpdatesOnStart = false, LootTrackingEnabled = false
        });
    }

    public void Dispose()
    {
        if (_originalSettings is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllBytes(AppPaths.SettingsFile, _originalSettings);
        Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _originalMapData);
        UiText.Language = _originalLanguage;
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("de", 12, .6, 360)]
    [InlineData("en", 12, 1.0, 720)]
    [InlineData("en", 24, 1.0, 480)]
    [InlineData("de", 48, 2.0, 1600)]
    public void CalendarAndCapturedBossesRenderWithinEveryConfiguredHorizonAndDensity(string language, int hours, double scale, int logicalWidth)
    {
        UiText.Language = language;
        var first = EventTimelineTests.Boss(1, TimeSpan.FromMinutes(15));
        var second = EventTimelineTests.Boss(2, TimeSpan.FromHours(2)) with { Cached = true };
        var snapshot = EventTimelineTests.Create([first, second], [first.Place.Key, second.Place.Key], hours);
        var width = (int)Math.Ceiling(logicalWidth * scale);
        var height = (int)Math.Ceiling(EventTimelineOverlayForm.LogicalHeight(snapshot) * scale);
        using var bitmap = new Bitmap(width + 20, height + 20);
        IReadOnlyList<EventTimelineOverlayForm.TimelineHit> hits;
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Fuchsia);
            graphics.ScaleTransform((float)scale, (float)scale);
            hits = EventTimelineOverlayForm.PaintPanel(graphics, snapshot, locked: true, width: logicalWidth);
        }
        Assert.Equal(Color.Fuchsia.ToArgb(), bitmap.GetPixel(width + 10, height / 2).ToArgb());
        Assert.Equal(Color.Fuchsia.ToArgb(), bitmap.GetPixel(width / 2, height + 10).ToArgb());
        Assert.Contains(hits, hit => hit.Tooltip.Contains(first.Place.Boss.En, StringComparison.Ordinal));
        Assert.Contains(hits, hit => hit.Tooltip.Contains(second.Place.Boss.En, StringComparison.Ordinal)
            && hit.Tooltip.Contains(UiText.T("Unbestätigt"), StringComparison.Ordinal));
        Assert.All(hits, hit =>
        {
            Assert.True(hit.Bounds.Left >= 0);
            Assert.True(hit.Bounds.Right <= logicalWidth);
            Assert.True(hit.Bounds.Top >= 0);
            Assert.True(hit.Bounds.Bottom <= EventTimelineOverlayForm.LogicalHeight(snapshot));
        });
        if (Environment.GetEnvironmentVariable("SOULCREST_TIMELINE_PREVIEW") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            bitmap.Save(Path.Combine(output, $"event-timeline-{language}-{hours}h-{logicalWidth}px-{scale:0.0}.png"), ImageFormat.Png);
        }
    }

    [Theory]
    [InlineData(0, 0, 1280, 720, 2000, 1000, 2)]
    [InlineData(-1920, 0, 1920, 1040, -2000, -100, 2)]
    [InlineData(0, 0, 800, 600, 40, 540, 1)]
    public void PlacementAndEffectiveScaleFitTheSelectedMonitorWithoutRewritingUserScale(
        int left, int top, int width, int height, int x, int y, double scale)
    {
        var area = new Rectangle(left, top, width, height);
        var bounds = EventTimelineOverlayForm.FitBounds(area, new Point(x, y), new Size(800, 340), scale);
        Assert.True(area.Contains(bounds));
        Assert.True(bounds.Width <= 800 * scale);
    }

    [Fact]
    public void CompactHeightDependsOnlyOnVisibleLanesAndBossIconsKeepAllCollisionTooltips()
    {
        Assert.Equal(104, EventTimelineOverlayForm.LogicalHeight(EventTimelineTests.Create()));
        var first = EventTimelineTests.Boss(1, TimeSpan.FromHours(2));
        var second = EventTimelineTests.Boss(2, TimeSpan.FromHours(2));
        var snapshot = EventTimelineTests.Create([first, second], [first.Place.Key, second.Place.Key], 48);
        Assert.Equal(128, EventTimelineOverlayForm.LogicalHeight(snapshot));
        using var bitmap = new Bitmap(720, 128);
        using var image = new Bitmap(18, 18);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Lime);
        using var canvas = Graphics.FromImage(bitmap);
        var icons = 0;
        var hits = EventTimelineOverlayForm.PaintPanel(canvas, snapshot, true, icon: _ => { icons++; return image; });
        Assert.Equal(1, icons);
        var firstHit = Assert.Single(hits, hit => hit.Tooltip.Contains(first.Place.Boss.En, StringComparison.Ordinal));
        var secondHit = Assert.Single(hits, hit => hit.Tooltip.Contains(second.Place.Boss.En, StringComparison.Ordinal));
        Assert.Equal(firstHit.Bounds, secondHit.Bounds);
        var point = new Point((int)firstHit.Bounds.Left + 10, (int)firstHit.Bounds.Top + 10);
        Assert.Equal(Color.Lime.ToArgb(), bitmap.GetPixel(point.X, point.Y).ToArgb());
        var daily = Assert.Single(hits, hit => hit.Tooltip.StartsWith(UiText.T("Täglicher Reset"), StringComparison.Ordinal)
            && hit.Tooltip.Contains("14.10", StringComparison.Ordinal));
        var weekly = Assert.Single(hits, hit => hit.Tooltip.StartsWith(UiText.T("Wöchentlicher Reset"), StringComparison.Ordinal));
        Assert.True(daily.Bounds.IntersectsWith(weekly.Bounds));
        Assert.True(daily.Bounds.Height > EventTimelineOverlayForm.LaneStride * 3);
    }

    [Fact]
    public void ProvisionalBossMarkerIsMutedWhileConfirmedMarkerUsesItsColor()
    {
        var boss = EventTimelineTests.Boss(1, TimeSpan.FromHours(2));
        using var confirmed = Paint(EventTimelineTests.Create([boss], [boss.Place.Key]));
        using var provisional = Paint(EventTimelineTests.Create([boss with { Cached = true }], [boss.Place.Key]));
        var point = new Point(EventTimelineOverlayForm.TrackLeft + EventTimelineOverlayForm.TrackWidth / 6,
            EventTimelineOverlayForm.AxisHeight + 3 * EventTimelineOverlayForm.LaneStride + 18);
        Assert.Equal(ColorTranslator.FromHtml("#f87171").ToArgb(), confirmed.GetPixel(point.X, point.Y).ToArgb());
        Assert.True(provisional.GetPixel(point.X, point.Y).R < confirmed.GetPixel(point.X, point.Y).R);
    }

    [Fact]
    public void VerticalResizingChangesDensityWhileHorizontalResizingKeepsItsIndependentWidth()
    {
        var dimensions = EventTimelineOverlayForm.ResizeDimensions(new Size(720, 104), new Point(120, 52), 104);
        Assert.Equal(1.5, dimensions.Scale);
        Assert.Equal(560, dimensions.Width);
        Assert.Equal(840, dimensions.Width * dimensions.Scale);
        var minimum = EventTimelineOverlayForm.ResizeDimensions(new Size(720, 104), new Point(-5000, -5000), 104);
        Assert.Equal((360, .6), minimum);
        var maximum = EventTimelineOverlayForm.ResizeDimensions(new Size(720, 104), new Point(5000, 5000), 104);
        Assert.Equal((1600, 2d), maximum);
    }

    [Fact]
    public Task TimelineVisibilityIsIndependentOfCountdownHudAndSuppressionCancelsInteraction() => RunOnSta(() =>
    {
        using var fixture = new NativeFixture(_directory);
        var panel = fixture.Panel;
        ShowPanel(panel);
        Assert.False(fixture.Settings.Current.AlertsOverlayEnabled);
        Assert.True(LockButton(panel).Visible);
        ClickLock(panel);
        Assert.True(panel.ManualInteractionEnabled);
        SendMouse(panel, 0x0201, new Point(30, 30));
        Assert.True(panel.Capture);
        panel.SetSuppressed(true);
        panel.SetInteractionEnabled(true);
        panel.Show();
        Assert.False(panel.Visible);
        Assert.False(LockButton(panel).Visible);
        Assert.False(panel.Capture);
        Assert.False(panel.ManualInteractionEnabled);
        AssertClickThrough(panel, true);
        panel.SetSuppressed(false);
        Assert.True(panel.Visible);
        Assert.True(LockButton(panel).Locked);
        fixture.Settings.Current.EventTimelineOverlayEnabled = false;
        RefreshPanel(panel);
        Assert.False(panel.Visible);
        Assert.False(LockButton(panel).Visible);
    });

    [Fact]
    public Task RelockingDuringAltCancelsTheSamePressUntilRelease() => RunOnSta(() =>
    {
        using var fixture = new NativeFixture(_directory);
        var panel = fixture.Panel;
        ShowPanel(panel);
        panel.SetInteractionEnabled(true);
        AssertClickThrough(panel, false);
        panel.ToggleManualInteraction();
        panel.SetInteractionEnabled(true);
        AssertClickThrough(panel, true);
        Assert.True(LockButton(panel).Locked);
        panel.SetInteractionEnabled(false);
        panel.SetInteractionEnabled(true);
        AssertClickThrough(panel, false);
        panel.SetInteractionEnabled(false);
        AssertClickThrough(panel, true);
    });

    [Fact]
    public Task DragAndResizeSaveOnlyTheirFinalStateAndLockedPanelsRejectBoth() => RunOnSta(() =>
    {
        using var fixture = new NativeFixture(_directory);
        var panel = fixture.Panel;
        ShowPanel(panel);
        var originalSize = panel.Size;
        var corner = new Point(panel.Width - 5, panel.Height - 5);
        SendMouse(panel, 0x0201, corner);
        Assert.False(panel.Capture);
        SendMouse(panel, 0x0200, new Point(corner.X + 80, corner.Y + 20));
        SendMouse(panel, 0x0202, corner);
        Assert.Equal(originalSize, panel.Size);
        ClickLock(panel);
        var changes = 0;
        fixture.Settings.Changed += () => changes++;
        var original = panel.Location;
        SendMouse(panel, 0x0201, new Point(40, 10));
        SendMouse(panel, 0x0200, new Point(80, 40));
        Assert.Equal(new Point(original.X + 40, original.Y + 30), panel.Location);
        Assert.Equal(0, changes);
        SendMouse(panel, 0x0202, new Point(80, 40));
        Assert.Equal(1, changes);
        Assert.Equal(panel.Left, fixture.Settings.Current.EventTimelineOverlayX);
        Assert.Equal(panel.Top, fixture.Settings.Current.EventTimelineOverlayY);
        corner = new Point(panel.Width - 5, panel.Height - 5);
        var moved = new Point(corner.X + 80, corner.Y + 20);
        SendMouse(panel, 0x0201, corner);
        SendMouse(panel, 0x0200, moved);
        Assert.True(panel.Height > originalSize.Height);
        Assert.True(panel.Width > originalSize.Width);
        Assert.Equal(1, changes);
        SendMouse(panel, 0x0202, moved);
        Assert.Equal(2, changes);
        Assert.True(fixture.Settings.Current.EventTimelineOverlayScale > 1);
        Assert.Equal(panel.Width, (int)Math.Round(fixture.Settings.Current.EventTimelineOverlayWidth * fixture.Settings.Current.EventTimelineOverlayScale));
        var resizedSize = panel.Size;
        for (var index = 0; index < 10; index++) RefreshPanel(panel);
        Assert.Equal(resizedSize, panel.Size);
        Assert.Equal(12, fixture.Settings.Current.EventTimelineHours);
        corner = new Point(panel.Width - 5, panel.Height - 5);
        SendMouse(panel, 0x0201, corner);
        ClickLock(panel);
        Assert.False(panel.Capture);
        var relockedSize = panel.Size;
        SendMouse(panel, 0x0200, new Point(corner.X + 50, corner.Y + 50));
        Assert.Equal(relockedSize, panel.Size);
        AssertClickThrough(panel, true);
    });

    private sealed class NativeFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AlertsService _alerts;
        private readonly BossRushService _bosses;
        public SettingsService Settings { get; }
        public EventTimelineOverlayForm Panel { get; }
        public NativeFixture(string directory)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<MapTargetsService>();
            _provider = services.BuildServiceProvider();
            Settings = _provider.GetRequiredService<SettingsService>();
            _bosses = new(_provider.GetRequiredService<ProgressService>(), Settings,
                _provider.GetRequiredService<MapTargetsService>(), _provider.GetRequiredService<NetworkLootService>(),
                new EventTimelineTests.FixedTime(), Path.Combine(directory, "bosses.json"));
            _alerts = new(Path.Combine(directory, "alerts.json"), new EventTimelineTests.FixedTime());
            Panel = new(_alerts, _bosses, Settings);
        }
        public void Dispose() { Panel.Dispose(); _alerts.Dispose(); _bosses.Dispose(); _provider.Dispose(); }
    }

    private static Bitmap Paint(EventTimelineSnapshot snapshot)
    {
        var bitmap = new Bitmap(EventTimelineOverlayForm.PanelWidth, EventTimelineOverlayForm.LogicalHeight(snapshot));
        using var graphics = Graphics.FromImage(bitmap);
        EventTimelineOverlayForm.PaintPanel(graphics, snapshot, locked: true);
        return bitmap;
    }
    private static void ShowPanel(EventTimelineOverlayForm panel) { _ = panel.Handle; RefreshPanel(panel); Assert.True(panel.Visible); }
    private static void RefreshPanel(EventTimelineOverlayForm panel) => typeof(EventTimelineOverlayForm)
        .GetMethod("RefreshPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
    private static OverlayLockButton LockButton(EventTimelineOverlayForm panel) => (OverlayLockButton)typeof(EventTimelineOverlayForm)
        .GetField("_lockButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
    private static void ClickLock(EventTimelineOverlayForm panel)
    {
        var button = LockButton(panel);
        SendMouse(button, 0x0201, new Point(12, 12));
        SendMouse(button, 0x0202, new Point(12, 12));
    }
    private static void SendMouse(Form panel, int message, Point point) =>
        SendMessage(panel.Handle, message, message == 0x0202 ? 0 : 1, (nint)((point.Y << 16) | (point.X & 0xffff)));
    private static void AssertClickThrough(Form panel, bool expected)
    {
        var style = NativeMethods.GetWindowLongPtr(panel.Handle, NativeMethods.GWL_EXSTYLE);
        Assert.Equal(expected, (style & NativeMethods.WS_EX_TRANSPARENT) != 0);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_NOACTIVATE);
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    private static async Task RunOnSta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
