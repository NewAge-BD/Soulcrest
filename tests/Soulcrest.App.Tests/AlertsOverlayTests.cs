using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AlertsOverlayTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 17, 55, 0, TimeSpan.Zero);
    private readonly byte[]? _originalSettings;
    private readonly string _originalLanguage = UiText.Language;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Soulcrest-alerts-overlay-" + Guid.NewGuid().ToString("N"));

    public AlertsOverlayTests()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Directory.CreateDirectory(_directory);
        _originalSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            AlertsOverlayEnabled = true, AlertsOverlayScale = 1,
            AlertsOverlayX = 240, AlertsOverlayY = 180, UiLanguage = "en",
            CheckUpdatesOnStart = false, LootTrackingEnabled = false
        });
    }

    public void Dispose()
    {
        if (_originalSettings is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllBytes(AppPaths.SettingsFile, _originalSettings);
        UiText.Language = _originalLanguage;
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 12, true)]
    public void PanelNeedsEnabledOverlayAndAtLeastOneCountdown(bool enabled, int rows, bool expected) =>
        Assert.Equal(expected, AlertsOverlayForm.PanelVisible(new AppSettings { AlertsOverlayEnabled = enabled }, rows));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(7, 6)]
    [InlineData(int.MaxValue, 6)]
    public void PanelHeightKeepsHeaderFooterAndAtMostSixRows(int count, int visible) =>
        Assert.Equal(AlertsOverlayForm.HeaderHeight + visible * AlertsOverlayForm.RowStride + AlertsOverlayForm.FooterHeight,
            AlertsOverlayForm.LogicalHeight(count));

    [Theory]
    [InlineData("en", -1, "Now")]
    [InlineData("de", 0, "Jetzt")]
    [InlineData("en", 1, "00:00:01")]
    [InlineData("en", 3661, "01:01:01")]
    [InlineData("de", 86399, "23:59:59")]
    [InlineData("en", 86400, "1d 00:00:00")]
    [InlineData("de", 183845, "2 T. 03:04:05")]
    public void CountdownKeepsDaysAndLocalizesStartedEvents(string language, int seconds, string expected)
    {
        UiText.Language = language;
        Assert.Equal(expected, AlertsOverlayForm.Countdown(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData("en", .6, 240)]
    [InlineData("de", 1.0, 340)]
    [InlineData("de", 2.0, 1000)]
    public void LongUnicodeRowsFitAtEveryOverlayDensityAndDoNotPaintASeventhRow(string language, double scale, int logicalWidth)
    {
        UiText.Language = language;
        var rows = Enumerable.Range(0, 7).Select(index => new AlertDisplayRow(
            "event:" + index, "Übermäßig langer Ereignistitel · 슈고 축제 · Abyss Siege " + new string('W', 80),
            "Lange Beschreibung · nächste Gelegenheit " + new string('M', 160),
            index == 6 ? "invalid seventh row must never be painted" : "#00ff00",
            Now.AddMinutes(index + 1), false, index == 2)).ToArray();
        var width = (int)Math.Ceiling(logicalWidth * scale);
        var height = (int)Math.Ceiling(AlertsOverlayForm.LogicalHeight(rows.Length) * scale);
        using var bitmap = new Bitmap(width + 20, height + 20);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Fuchsia);
            graphics.ScaleTransform((float)scale, (float)scale);
            AlertsOverlayForm.PaintPanel(graphics, rows, Now, locked: true, width: logicalWidth);
            if (Environment.GetEnvironmentVariable("SOULCREST_COMPACT_ALERT_PREVIEW") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, $"alerts-{language}-{logicalWidth}-{scale}.png"));
            }
        }
        Assert.Equal(Color.Fuchsia.ToArgb(), bitmap.GetPixel(width + 10, height / 2).ToArgb());
        Assert.Equal(Color.Fuchsia.ToArgb(), bitmap.GetPixel(width / 2, height + 10).ToArgb());
        for (var index = 0; index < 6; index++)
        {
            var stripe = Rectangle.FromLTRB((int)(8 * scale),
                (int)((AlertsOverlayForm.HeaderHeight + index * AlertsOverlayForm.RowStride + 8) * scale),
                (int)Math.Ceiling(10 * scale),
                (int)Math.Ceiling((AlertsOverlayForm.HeaderHeight + index * AlertsOverlayForm.RowStride + 32) * scale));
            Assert.True(HasGreenInk(bitmap, stripe), "Each visible row needs its own configured color accent.");
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void AnExpiredProvisionalSpawnIsMutedInsteadOfShowingAConfirmedColoredStart(string language)
    {
        UiText.Language = language;
        var row = new AlertDisplayRow("boss:sample", "Saved boss countdown", "", "#00ff00", Now.AddSeconds(-1), false);
        using var confirmed = Paint([row]);
        using var provisional = Paint([row with { Provisional = true }]);
        var countdown = new Rectangle(216, AlertsOverlayForm.HeaderHeight + 3, 112, 18);
        Assert.True(HasGreenInk(confirmed, countdown));
        Assert.False(HasGreenInk(provisional, countdown));
    }

    [Theory]
    [InlineData("de", "Endet in")]
    [InlineData("en", "Ends in")]
    public void RunningEventKeepsCountingDownWhileConfirmedBossRemainsStarted(string language, string label)
    {
        UiText.Language = language;
        var running = new AlertDisplayRow("catalog:shugo", "Shugo Festival", "", "#00ff00",
            Now.AddMinutes(5), true, CountdownLabel: label);
        using var first = Paint([running], Now);
        using var later = Paint([running], Now.AddSeconds(10));
        Assert.False(SameCountdownInk(first, later));

        var boss = running with { Key = "boss:sample", CountdownLabel = null };
        using var bossFirst = Paint([boss], Now);
        using var bossLater = Paint([boss], Now.AddSeconds(10));
        Assert.True(SameCountdownInk(bossFirst, bossLater));

        static bool SameCountdownInk(Bitmap left, Bitmap right)
        {
            for (var y = AlertsOverlayForm.HeaderHeight + 3; y < AlertsOverlayForm.HeaderHeight + 21; y++)
            for (var x = 170; x < 328; x++)
                if (left.GetPixel(x, y) != right.GetPixel(x, y)) return false;
            return true;
        }
    }

    [Fact]
    public Task ExplicitRelockBlocksTheSameHeldAltUntilItIsReleased() => RunOnSta(() =>
    {
        using var alerts = Service();
        var settings = new SettingsService();
        using var panel = new AlertsOverlayForm(alerts, settings);
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        var changes = 0;
        settings.Changed += () => changes++;
        AssertClickThrough(panel, true);
        panel.SetInteractionEnabled(true);
        AssertClickThrough(panel, false);
        Assert.False(LockButton(panel).Locked);
        panel.ToggleManualInteraction(); // An explicit lock also overrides a currently held Alt key.
        Assert.False(panel.ManualInteractionEnabled);
        panel.SetInteractionEnabled(true);
        panel.SetInteractionEnabled(true);
        Assert.True(LockButton(panel).Locked);
        AssertClickThrough(panel, true);
        panel.SetInteractionEnabled(false);
        panel.SetInteractionEnabled(true); // A new press can temporarily unlock again.
        AssertClickThrough(panel, false);
        panel.SetInteractionEnabled(false);
        AssertClickThrough(panel, true);
        Assert.False(panel.Visible);
        Assert.Equal(0, changes);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    });

    [Fact]
    public Task ManualUnlockAllowsDraggingAndRelockSavesOnlyTheFinalLocation() => RunOnSta(() =>
    {
        using var alerts = Service(withRow: true);
        var settings = new SettingsService();
        using var panel = new AlertsOverlayForm(alerts, settings);
        ShowPanel(panel);
        var changes = 0;
        settings.Changed += () => changes++;
        var original = panel.Location;
        var lockLocation = LockButton(panel).Location;
        ClickLock(panel);
        Assert.True(panel.ManualInteractionEnabled);
        SendMouse(panel, 0x0201, new Point(30, 30));
        Assert.True(panel.Capture);
        SendMouse(panel, 0x0200, new Point(70, 60));
        Assert.Equal(new Point(original.X + 40, original.Y + 30), panel.Location);
        Assert.Equal(new Point(lockLocation.X + 40, lockLocation.Y + 30), LockButton(panel).Location);
        panel.SetInteractionEnabled(false); // Releasing Alt preserves an explicit manual unlock.
        Assert.True(panel.Capture);
        Assert.Equal(0, changes);
        ClickLock(panel);
        Assert.False(panel.Capture);
        Assert.False(panel.ManualInteractionEnabled);
        AssertClickThrough(panel, true);
        Assert.Equal(panel.Left, settings.Current.AlertsOverlayX);
        Assert.Equal(panel.Top, settings.Current.AlertsOverlayY);
        Assert.Equal(1, changes);
        SendMouse(panel, 0x0202, new Point(70, 60));
        Assert.Equal(1, changes);
    });

    [Fact]
    public Task MenuSuppressionCancelsTheDragHidesTheLockAndPreventsForcedReshow() => RunOnSta(() =>
    {
        using var alerts = Service(withRow: true);
        var settings = new SettingsService();
        using var panel = new AlertsOverlayForm(alerts, settings);
        ShowPanel(panel);
        ClickLock(panel);
        SendMouse(panel, 0x0201, new Point(30, 30));
        Assert.True(panel.Capture);
        var changes = 0;
        settings.Changed += () => changes++;
        panel.SetSuppressed(true);
        panel.SetSuppressed(true);
        panel.SetInteractionEnabled(true);
        panel.Show();
        Assert.False(panel.Visible);
        Assert.False(LockButton(panel).Visible);
        Assert.False(panel.Capture);
        Assert.False(panel.ManualInteractionEnabled);
        AssertClickThrough(panel, true);
        Assert.Equal(1, changes);
        panel.SetSuppressed(false);
        Assert.True(panel.Visible);
        Assert.True(LockButton(panel).Visible);
        Assert.True(LockButton(panel).Locked);
        AssertClickThrough(panel, true);
    });

    [Fact]
    public Task HideResetsManualUnlockAndDisposalClosesTheIndependentLockWindow() => RunOnSta(() =>
    {
        using var alerts = Service(withRow: true);
        using var panel = new AlertsOverlayForm(alerts, new SettingsService());
        ShowPanel(panel);
        var button = LockButton(panel);
        ClickLock(panel);
        panel.Hide();
        Assert.False(button.Visible);
        Assert.False(panel.ManualInteractionEnabled);
        AssertClickThrough(panel, true);
        panel.Show();
        Assert.True(button.Visible);
        Assert.True(button.Locked);
        panel.Dispose();
        Assert.True(button.IsDisposed);
    });

    [Fact]
    public Task DisablingOverlayHidesThePanelWithoutDeletingItsScheduledCountdown() => RunOnSta(() =>
    {
        using var alerts = Service(withRow: true);
        var settings = new SettingsService();
        using var panel = new AlertsOverlayForm(alerts, settings);
        ShowPanel(panel);
        settings.Current.AlertsOverlayEnabled = false;
        RefreshPanel(panel);
        Assert.False(panel.Visible);
        Assert.False(LockButton(panel).Visible);
        Assert.Single(alerts.OverlayRows);
        settings.Current.AlertsOverlayEnabled = true;
        RefreshPanel(panel);
        Assert.True(panel.Visible);
        Assert.True(LockButton(panel).Visible);
    });

    [Theory]
    [InlineData(240, .6)]
    [InlineData(340, 1)]
    [InlineData(1000, 2)]
    public void FitBoundsContainsEveryDensityOnSmallAndOffsetMonitors(int width, double scale)
    {
        foreach (var area in new[] { new Rectangle(0, 0, 300, 180), new Rectangle(-1280, 20, 1280, 700) })
        {
            var bounds = AlertsOverlayForm.FitBounds(area, new Point(4000, -3000), width,
                AlertsOverlayForm.LogicalHeight(6), scale);
            Assert.True(area.Contains(bounds));
            Assert.True(bounds.Width > 0 && bounds.Height > 0);
        }
    }

    [Fact]
    public void SixCompactRowsUseLessThanHalfThePreviousHeightAndDetailsRemainAvailable()
    {
        Assert.True(AlertsOverlayForm.LogicalHeight(6) < (48 + 6 * 92 + 30) / 2);
        var title = new string('W', 160);
        var message = new string('M', 200);
        var details = AlertsOverlayForm.RowTooltip(new AlertDisplayRow("sample", title, message,
            "#ffffff", Now.AddMinutes(5), false, Provisional: true), Now);
        Assert.Contains(title, details);
        Assert.Contains(message, details);
        Assert.Contains("05", details);
    }

    [Fact]
    public Task CornerResizePersistsOnlyOnReleaseAndDoesNotChangeBannerDimensions() => RunOnSta(() =>
    {
        using var alerts = Service(withRow: true);
        var settings = new SettingsService();
        using var panel = new AlertsOverlayForm(alerts, settings);
        ShowPanel(panel);
        var previousBannerWidth = settings.Current.AlertNotificationsWidth;
        var previousBannerScale = settings.Current.AlertNotificationsScale;
        var previousWidth = panel.Width;
        var previousHeight = panel.Height;
        var changes = 0;
        settings.Changed += () => changes++;
        ClickLock(panel);
        var corner = new Point(panel.Width - 3, panel.Height - 3);
        SendMouse(panel, 0x0201, corner);
        SendMouse(panel, 0x0200, new Point(corner.X + 80, corner.Y + 20));
        Assert.InRange(panel.Width, previousWidth + 79, previousWidth + 81);
        Assert.InRange(panel.Height, previousHeight + 20, previousHeight + 21);
        Assert.Equal(0, changes);
        SendMouse(panel, 0x0202, new Point(panel.Width - 3, panel.Height - 3));
        Assert.False(panel.Capture);
        Assert.Equal(1, changes);
        Assert.InRange(settings.Current.AlertsOverlayScale, 1.2, 1.4);
        Assert.InRange(settings.Current.AlertsOverlayWidth, AlertsOverlayForm.MinimumWidth, AlertsOverlayForm.MaximumWidth);
        var finalSize = panel.Size;
        RefreshPanel(panel);
        Assert.Equal(finalSize, panel.Size);
        Assert.Equal(previousBannerWidth, settings.Current.AlertNotificationsWidth);
        Assert.Equal(previousBannerScale, settings.Current.AlertNotificationsScale);
        ClickLock(panel);
        Assert.False(panel.ManualInteractionEnabled);
        Assert.True(LockButton(panel).Locked);
        SendMouse(panel, 0x0201, new Point(panel.Width - 3, panel.Height - 3));
        Assert.False(panel.Capture);
        Assert.Equal(1, changes);
    });

    private AlertsService Service(bool withRow = false)
    {
        var service = new AlertsService(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json"), new FixedTime(), runTimer: false);
        if (withRow) Assert.True(service.Save(new EventAlert { Id = "sample-event", Title = "Sample event", AlwaysShowOverlay = true }));
        return service;
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Bitmap Paint(IReadOnlyList<AlertDisplayRow> rows, DateTimeOffset? now = null)
    {
        var bitmap = new Bitmap(AlertsOverlayForm.PanelWidth, AlertsOverlayForm.LogicalHeight(rows.Count));
        using var graphics = Graphics.FromImage(bitmap);
        AlertsOverlayForm.PaintPanel(graphics, rows, now ?? Now, locked: true);
        return bitmap;
    }

    private static bool HasGreenInk(Bitmap bitmap, Rectangle area)
    {
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.G > pixel.R + 80 && pixel.G > pixel.B + 80) return true;
        }
        return false;
    }

    private static void ShowPanel(AlertsOverlayForm panel)
    {
        _ = panel.Handle;
        RefreshPanel(panel);
        Assert.True(panel.Visible);
        Assert.True(LockButton(panel).Visible);
    }

    private static void RefreshPanel(AlertsOverlayForm panel) => typeof(AlertsOverlayForm)
        .GetMethod("RefreshPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);

    private static OverlayLockButton LockButton(AlertsOverlayForm panel) => (OverlayLockButton)typeof(AlertsOverlayForm)
        .GetField("_lockButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;

    private static void ClickLock(AlertsOverlayForm panel)
    {
        var button = LockButton(panel);
        Assert.True(button.Visible);
        var center = new Point(button.ClientSize.Width / 2, button.ClientSize.Height / 2);
        SendMouse(button, 0x0201, center);
        SendMouse(button, 0x0202, center);
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
