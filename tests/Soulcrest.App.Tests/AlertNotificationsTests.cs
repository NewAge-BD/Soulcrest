using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Alerts;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AlertNotificationsTests
{
    [Fact]
    public async Task ShortNotificationSurvivesHudSuppressionAndDisabledCountdownOverlayUntilItsDurationEnds()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            var folder = Path.Combine(Path.GetTempPath(), "Soulcrest-notification-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
            var language = UiText.Language;
            try
            {
                var clock = new ManualTime();
                JsonFile.Save(AppPaths.SettingsFile, new AppSettings { AlertsOverlayEnabled = false, AlertsOverlayX = 240, AlertsOverlayY = 180 });
                var settings = new SettingsService();
                using var alerts = new AlertsService(Path.Combine(folder, "alerts.json"), clock);
                Assert.True(alerts.Save(new EventAlert
                {
                    Id = "sample", Title = "Sample reminder", ShowOverlay = true, PlaySound = false, DurationSeconds = 3,
                    Schedule = new() { Kind = AlertScheduleKind.Once, TimeZoneId = "UTC", At = clock.GetUtcNow().AddHours(1) }
                }));
                using var hud = new AlertsOverlayForm(alerts, settings);
                using var notification = new AlertNotificationsForm(alerts, settings);
                _ = hud.Handle;
                _ = notification.Handle;
                hud.SetSuppressed(true);
                alerts.Preview("sample");
                Assert.Single(alerts.NotificationRows);
                Assert.Empty(alerts.CountdownRows);
                notification.RefreshPanel();
                hud.Show();
                Assert.False(hud.Visible);
                Assert.True(notification.Visible);
                var style = NativeMethods.GetWindowLongPtr(notification.Handle, NativeMethods.GWL_EXSTYLE);
                Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_TRANSPARENT);
                Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_NOACTIVATE);

                clock.Now = clock.Now.AddSeconds(3);
                notification.RefreshPanel();
                Assert.Empty(alerts.NotificationRows);
                Assert.False(notification.Visible);
                Assert.False(hud.Visible);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, settingsBackup);
                UiText.Language = language;
                Directory.Delete(folder, true);
            }
            if (failure is not null) completed.SetException(failure); else completed.SetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public Task BannerResizeUsesIndependentDimensionsAndKeepsItsOwnClickThroughLock() => WithNotification((alerts, settings, panel, clock) =>
    {
        var previousHudWidth = settings.Current.AlertsOverlayWidth;
        var previousHudScale = settings.Current.AlertsOverlayScale;
        Assert.Equal(224, panel.Width);
        Assert.InRange(panel.Height, 57, 58);
        var changes = 0;
        settings.Changed += () => changes++;
        panel.ToggleManualInteraction();
        Assert.False(LockButton(panel).Locked);
        var corner = new Point(panel.Width - 3, panel.Height - 3);
        SendMouse(panel, 0x0201, corner);
        SendMouse(panel, 0x0200, new Point(corner.X + 70, corner.Y + 18));
        Assert.True(panel.Capture);
        Assert.Equal(0, changes);
        panel.ToggleManualInteraction(); // Explicit relock ends and stores the resize once.
        Assert.False(panel.Capture);
        Assert.True(LockButton(panel).Locked);
        Assert.Equal(1, changes);
        Assert.NotEqual(280, settings.Current.AlertNotificationsWidth);
        Assert.NotEqual(.8, settings.Current.AlertNotificationsScale);
        Assert.Equal(previousHudWidth, settings.Current.AlertsOverlayWidth);
        Assert.Equal(previousHudScale, settings.Current.AlertsOverlayScale);
        var finalSize = panel.Size;
        panel.RefreshPanel();
        Assert.Equal(finalSize, panel.Size);
        var style = NativeMethods.GetWindowLongPtr(panel.Handle, NativeMethods.GWL_EXSTYLE);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_TRANSPARENT);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_NOACTIVATE);
        panel.SetSuppressed(true); // Timed notifications ignore normal HUD suppression.
        Assert.True(panel.Visible);
    });

    [Fact]
    public Task BannerRelockBlocksHeldAltAndExpiryClosesItsIndependentLockSurface() => WithNotification((alerts, settings, panel, clock) =>
    {
        var button = LockButton(panel);
        panel.SetInteractionEnabled(true);
        Assert.False(button.Locked);
        panel.ToggleManualInteraction();
        panel.SetInteractionEnabled(true);
        Assert.True(button.Locked);
        panel.SetInteractionEnabled(false);
        panel.SetInteractionEnabled(true);
        Assert.False(button.Locked);
        panel.SetInteractionEnabled(false);
        panel.ToggleManualInteraction();
        Assert.True(panel.ManualInteractionEnabled);
        SendMouse(panel, 0x0201, new Point(30, 30));
        Assert.True(panel.Capture);
        clock.Now = clock.Now.AddSeconds(3);
        panel.RefreshPanel();
        Assert.False(panel.Visible);
        Assert.False(button.Visible);
        Assert.False(panel.Capture);
        Assert.False(panel.ManualInteractionEnabled);
        panel.Dispose();
        Assert.True(button.IsDisposed);
    });

    [Theory]
    [InlineData("en", 240)]
    [InlineData("de", 340)]
    [InlineData("de", 1000)]
    public void CompactNotificationRowsPaintWithinTheirIndependentWidth(string language, int width)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var now = new ManualTime().Now;
            var rows = Enumerable.Range(0, 7).Select(index => new AlertDisplayRow("notice:" + index,
                "Langer Hinweis · 슈고 축제 · " + new string('W', 100),
                "Ausführliche Nachricht · " + new string('M', 160),
                index == 6 ? "invalid overflow color" : "#a78bfa", now.AddMinutes(index), false)).ToArray();
            var height = AlertsOverlayForm.LogicalHeight(rows.Length);
            using var bitmap = new Bitmap(width + 20, height + 20);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Fuchsia);
                AlertsOverlayForm.PaintNotifications(graphics, rows, now, false, width: width);
            }
            Assert.Equal(Color.Fuchsia.ToArgb(), bitmap.GetPixel(width + 10, height / 2).ToArgb());
            Assert.Equal(Color.Fuchsia.ToArgb(), bitmap.GetPixel(width / 2, height + 10).ToArgb());
            if (Environment.GetEnvironmentVariable("SOULCREST_COMPACT_ALERT_PREVIEW") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, $"notification-{language}-{width}.png"));
            }
        }
        finally { UiText.Language = previous; }
    }

    private static async Task WithNotification(Action<AlertsService, SettingsService, AlertNotificationsForm, ManualTime> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "Soulcrest-compact-notification-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
            var language = UiText.Language;
            Exception? failure = null;
            try
            {
                JsonFile.Save(AppPaths.SettingsFile, new AppSettings
                {
                    SettingsRevision = 4, AlertsOverlayWidth = 700, AlertsOverlayScale = 1.6,
                    AlertNotificationsWidth = 280, AlertNotificationsScale = .8,
                    AlertNotificationsX = 240, AlertNotificationsY = 180, UiLanguage = "en"
                });
                var settings = new SettingsService();
                var clock = new ManualTime();
                using var alerts = new AlertsService(Path.Combine(folder, "alerts.json"), clock, runTimer: false);
                Assert.True(alerts.Save(new EventAlert
                {
                    Id = "sample", Title = "Sample reminder", ShowOverlay = true, PlaySound = false, DurationSeconds = 3,
                    Schedule = new() { Kind = AlertScheduleKind.Once, TimeZoneId = "UTC", At = clock.Now.AddHours(1) }
                }));
                alerts.Preview("sample");
                using var panel = new AlertNotificationsForm(alerts, settings);
                _ = panel.Handle;
                panel.RefreshPanel();
                Assert.True(panel.Visible);
                Assert.True(LockButton(panel).Visible);
                action(alerts, settings, panel, clock);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, backup);
                UiText.Language = language;
                Directory.Delete(folder, true);
            }
            if (failure is not null) completed.SetException(failure); else completed.SetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static OverlayLockButton LockButton(AlertNotificationsForm panel) => (OverlayLockButton)typeof(AlertsOverlayForm)
        .GetField("_lockButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;

    private static void SendMouse(Form panel, int message, Point point) =>
        SendMessage(panel.Handle, message, message == 0x0202 ? 0 : 1, (nint)((point.Y << 16) | (point.X & 0xffff)));

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
