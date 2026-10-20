using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Unshown host and isolated test data: no capture, WebView startup or game input.</summary>
[Collection("Settings file")]
public sealed class OverlayPlacementPresentationTests : IDisposable
{
    private readonly Dictionary<string, byte[]?> _backups = [];
    private readonly string _language = UiText.Language;

    public OverlayPlacementPresentationTests()
    {
        Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(AppPaths.DataDirectory);
        foreach (var path in new[]
        {
            AppPaths.SettingsFile, AppPaths.RoutesFile, AppPaths.LegacyRoutesFile, AppPaths.TargetsFile,
            Path.Combine(AppPaths.DataDirectory, "exploration.json"),
            Path.Combine(AppPaths.DataDirectory, "leveling-progress.json")
        })
        {
            _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            File.Delete(path);
        }
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            OverlayEnabled = true, OverlayX = 100, OverlayY = 100,
            BossRushEnabled = true, BossOverlayEnabled = true, BossOverlayVisible = true,
            BossAlertsEnabled = false, BossAlertSound = false,
            LevelingOverlayEnabled = true, EventTimelineOverlayEnabled = true,
            TimerOverlayTimerIds = ["event:catalog:shugo"],
            CheckUpdatesOnStart = false, LootTrackingEnabled = false, OverlayShowMode = "always"
        });
    }

    [Theory]
    [InlineData("foreground", false, true)]
    [InlineData("foreground", true, false)]
    [InlineData("always", false, false)]
    [InlineData("always", true, false)]
    public Task HostAppliesTheSharedForegroundChoiceToEveryOverlay(string mode, bool foreground, bool suppressed) => RunSta(() =>
    {
        using var host = new MainForm();
        var settings = host.Services.GetRequiredService<SettingsService>();
        settings.Current.OverlayShowMode = mode;
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        host.UpdateForegroundVisibility(foreground);
        foreach (var name in new[] { "_overlay", "_bossOverlay", "_levelingOverlay", "_alertsOverlay",
                     "_alertNotifications", "_eventTimelineOverlay", "_routeOverlay", "_merchantOverlay" })
        {
            var panel = (Form)Field(host, name).GetValue(host)!;
            Assert.Equal(suppressed, (bool)Field(panel, "_foregroundSuppressed").GetValue(panel)!);
            if (suppressed) { panel.Show(); Assert.False(panel.Visible); }
        }
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    });

    [Theory]
    [InlineData("_overlay")]
    [InlineData("_bossOverlay")]
    [InlineData("_levelingOverlay")]
    [InlineData("_alertsOverlay")]
    [InlineData("_alertNotifications")]
    [InlineData("_eventTimelineOverlay")]
    [InlineData("_routeOverlay")]
    [InlineData("_merchantOverlay")]
    public Task PlacementHidesEveryLiveSurfaceAndLocksUntilAFreshAltPress(string name) => RunSta(() =>
    {
        using var host = new MainForm();
        var panel = (Form)Field(host, name).GetValue(host)!;
        var pet = host.Services.GetRequiredService<ProgressService>();
        if (panel is PetOverlayForm)
        {
            ((List<LoggedSoul>)Field(pet, "_recent").GetValue(pet)!).Insert(0,
                new(DateTimeOffset.Now, "synthetic-placement-pet", 1, "network", "synthetic notification"));
            Refresh(panel);
        }
        _ = panel.Handle;
        panel.Show();
        var interactive = Method(panel, "SetInteractionEnabled");
        interactive?.Invoke(panel, [true]);
        var settingsBefore = File.ReadAllBytes(AppPaths.SettingsFile);
        Method(panel, "SetPlacementHidden")!.Invoke(panel, [true]);
        panel.Show();
        Refresh(panel);
        Method(panel, "SetSuppressed")?.Invoke(panel, [false]);
        interactive?.Invoke(panel, [true]);
        Method(panel, "ToggleManualInteractionFromHotkey")?.Invoke(panel, null);
        Assert.False(panel.Visible);
        Assert.False(panel.Capture);
        if (interactive is not null)
        {
            Assert.True((bool)Field(panel, "_locked").GetValue(panel)!);
            Assert.False((bool)Field(panel, "_manualInteraction").GetValue(panel)!);
            Assert.False(((Form)Field(panel, "_lockButton").GetValue(panel)!).Visible);
        }
        Method(panel, "SetPlacementHidden")!.Invoke(panel, [false]);
        if (interactive is not null)
        {
            Assert.True((bool)Field(panel, "_locked").GetValue(panel)!);
            interactive.Invoke(panel, [true]);
            Assert.True((bool)Field(panel, "_locked").GetValue(panel)!);
            interactive.Invoke(panel, [false]);
            interactive.Invoke(panel, [true]);
            Assert.False((bool)Field(panel, "_locked").GetValue(panel)!);
            interactive.Invoke(panel, [false]);
        }
        Assert.Equal(settingsBefore, File.ReadAllBytes(AppPaths.SettingsFile));
    });

    [Theory]
    [InlineData("_overlay", "_dragStart")]
    [InlineData("_bossOverlay", "_drag")]
    [InlineData("_levelingOverlay", "_drag")]
    [InlineData("_alertsOverlay", "_drag")]
    [InlineData("_alertNotifications", "_drag")]
    [InlineData("_eventTimelineOverlay", "_drag")]
    public Task EnteringPlacementCancelsNativeGesturesWithoutSavingOrExecutingThem(string name, string dragField) => RunSta(() =>
    {
        using var host = new MainForm();
        var panel = (Form)Field(host, name).GetValue(host)!;
        _ = panel.Handle;
        Method(panel, "SetInteractionEnabled")!.Invoke(panel, [true]);
        Field(panel, dragField).SetValue(panel, new Point(4, 4));
        if (panel is LevelingOverlayForm)
            Field(panel, "_pressed").SetValue(panel, LevelingOverlayAction.Next);
        panel.Capture = true;
        var settingsBefore = File.ReadAllBytes(AppPaths.SettingsFile);
        Method(panel, "SetPlacementHidden")!.Invoke(panel, [true]);
        Assert.Null(Field(panel, dragField).GetValue(panel));
        Assert.False(panel.Capture);
        if (panel is LevelingOverlayForm)
            Assert.Equal(LevelingOverlayAction.None, Field(panel, "_pressed").GetValue(panel));
        Assert.Equal(settingsBefore, File.ReadAllBytes(AppPaths.SettingsFile));
    });

    [Theory]
    [InlineData("_overlay")]
    [InlineData("_bossOverlay")]
    [InlineData("_levelingOverlay")]
    [InlineData("_alertsOverlay")]
    [InlineData("_alertNotifications")]
    [InlineData("_eventTimelineOverlay")]
    [InlineData("_routeOverlay")]
    [InlineData("_merchantOverlay")]
    public Task ForegroundGateHidesEverySurfaceAndRemainsIndependentOfPlacement(string name) => RunSta(() =>
    {
        using var host = new MainForm();
        var panel = (Form)Field(host, name).GetValue(host)!;
        if (panel is PetOverlayForm)
        {
            var progress = host.Services.GetRequiredService<ProgressService>();
            ((List<LoggedSoul>)Field(progress, "_recent").GetValue(progress)!).Insert(0,
                new(DateTimeOffset.Now, "synthetic-foreground-pet", 1, "network", "synthetic notification"));
            Refresh(panel);
        }
        _ = panel.Handle;
        panel.Show();
        Assert.True(panel.Visible);
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        var interactive = Method(panel, "SetInteractionEnabled");
        interactive?.Invoke(panel, [true]);
        Method(panel, "SetForegroundSuppressed")!.Invoke(panel, [true]);
        panel.Show(); Refresh(panel);
        interactive?.Invoke(panel, [true]);
        Method(panel, "ToggleManualInteractionFromHotkey")?.Invoke(panel, null);
        Assert.False(panel.Visible);
        Assert.False(panel.Capture);
        if (interactive is not null)
        {
            Assert.True((bool)Field(panel, "_locked").GetValue(panel)!);
            Assert.False((bool)Field(panel, "_manualInteraction").GetValue(panel)!);
            Assert.False(((Form)Field(panel, "_lockButton").GetValue(panel)!).Visible);
        }
        Method(panel, "SetPlacementHidden")!.Invoke(panel, [true]);
        Method(panel, "SetForegroundSuppressed")!.Invoke(panel, [false]);
        panel.Show(); Refresh(panel);
        Assert.False(panel.Visible); // Foreground return cannot remove the independent placement gate.
        Method(panel, "SetPlacementHidden")!.Invoke(panel, [false]);
        panel.Show();
        Assert.True(panel.Visible);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    });

    [Fact]
    public Task NotificationSuppressionUsesItsOwnPolicyAndDoesNotMutateTheReminderService() => RunSta(() =>
    {
        using var host = new MainForm();
        var notifications = (AlertNotificationsForm)Field(host, "_alertNotifications").GetValue(host)!;
        var alerts = host.Services.GetRequiredService<AlertsService>();
        var before = alerts.NotificationRows.ToArray();
        var settingsBefore = File.ReadAllBytes(AppPaths.SettingsFile);
        notifications.Show();
        Assert.True(notifications.Visible);
        notifications.SetSuppressed(true);
        notifications.Show();
        notifications.RefreshPanel();
        Assert.False(notifications.Visible);
        Assert.True((bool)Field(notifications, "_suppressed").GetValue(notifications)!);
        notifications.SetPlacementHidden(true);
        notifications.SetSuppressed(false);
        notifications.Show();
        Assert.False(notifications.Visible);
        Assert.Equal(before, alerts.NotificationRows.ToArray());
        Assert.Equal(settingsBefore, File.ReadAllBytes(AppPaths.SettingsFile));
    });

    private static FieldInfo Field(object instance, string name)
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field)
                return field;
        throw new MissingFieldException(instance.GetType().FullName, name);
    }

    private static MethodInfo? Method(object instance, string name) => instance.GetType().GetMethod(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static void Refresh(Form form) => (Method(form, "RefreshPanel") ?? Method(form, "RefreshLayout"))?.Invoke(form, null);

    private static Task RunSta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    public void Dispose()
    {
        foreach (var (path, bytes) in _backups)
        {
            if (bytes is null) File.Delete(path);
            else File.WriteAllBytes(path, bytes);
        }
        UiText.Language = _language;
    }
}
