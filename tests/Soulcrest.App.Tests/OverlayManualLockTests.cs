using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayManualLockTests : IDisposable
{
    private const int MouseDown = 0x0201, MouseUp = 0x0202, MouseMove = 0x0200;
    private readonly Dictionary<string, byte[]?> _originalFiles = [];
    private readonly string _originalLanguage = UiText.Language;

    public OverlayManualLockTests()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        foreach (var path in new[]
        {
            AppPaths.SettingsFile, AppPaths.RoutesFile, AppPaths.TargetsFile,
            Path.Combine(AppPaths.DataDirectory, "exploration.json"),
            Path.Combine(AppPaths.DataDirectory, "leveling-progress.json")
        })
        {
            _originalFiles.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            File.Delete(path);
        }
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            OverlayEnabled = false, BossRushEnabled = false, BossAlertsEnabled = false,
            LevelingOverlayEnabled = false, BossOverlayLocked = true, LevelingOverlayLocked = true,
            CheckUpdatesOnStart = false, LootTrackingEnabled = false,
            OverlayScale = 1, BossOverlayScale = 1, LevelingOverlayScale = 1
        });
        var stops = Enumerable.Range(0, 4).Select(index =>
            new RouteStop("altgard", 100 + index * 100, 200, $"Quest {index}", "NPCs", null)).ToArray();
        RouteFile.Save(AppPaths.RoutesFile, new[]
        {
            new SavedRoute("first", "First route", stops, Category: LevelingRouteStyle.Category),
            new SavedRoute("other", "Other route", stops, Category: LevelingRouteStyle.Category)
        });
    }

    public void Dispose()
    {
        foreach (var (path, contents) in _originalFiles)
        {
            if (contents is null) File.Delete(path);
            else File.WriteAllBytes(path, contents);
        }
        UiText.Language = _originalLanguage;
    }

    [Theory]
    [InlineData("_overlay")]
    [InlineData("_bossOverlay")]
    [InlineData("_levelingOverlay")]
    public Task ClickingTheNativeLockWithoutAltUnlocksAndAltReleasePreservesThatChoice(string field) => RunOnSta(() =>
    {
        using var host = new MainForm(); // No Show: capture and WebView startup remain inactive.
        var panel = PreparePanel(host, field);
        var hotspot = LockButton(panel);
        var settingsBefore = File.ReadAllBytes(AppPaths.SettingsFile);
        Assert.True(hotspot.Visible);
        Assert.True(hotspot.Locked);
        AssertClickThrough(panel, true);

        Assert.Equal((nint)NativeMethods.MA_NOACTIVATE,
            SendMessage(hotspot.Handle, NativeMethods.WM_MOUSEACTIVATE, panel.Handle, 0));
        ClickLock(panel);
        Assert.True(ManualInteraction(panel));
        Assert.False(hotspot.Locked);
        AssertClickThrough(panel, false);

        host.SetOverlayInteractionEnabled(false); // The normal Alt-release timer must respect manual unlock.
        host.SetOverlayInteractionEnabled(false);
        Assert.True(ManualInteraction(panel));
        AssertClickThrough(panel, false);

        ClickLock(panel);
        Assert.False(ManualInteraction(panel));
        Assert.True(hotspot.Locked);
        AssertClickThrough(panel, true);
        Assert.Equal(settingsBefore, File.ReadAllBytes(AppPaths.SettingsFile));
    });

    [Theory]
    [InlineData("_overlay", "OverlayX", "OverlayY")]
    [InlineData("_bossOverlay", "BossOverlayX", "BossOverlayY")]
    [InlineData("_levelingOverlay", "LevelingOverlayX", "LevelingOverlayY")]
    public Task NativeHeaderDragWorksWithoutAltAndRelockingEndsCaptureAndSavesPosition(string field, string xProperty, string yProperty) => RunOnSta(() =>
    {
        using var host = new MainForm();
        var panel = PreparePanel(host, field);
        var settings = host.Services.GetRequiredService<SettingsService>();
        ClickLock(panel);
        var before = panel.Location;
        var hotspotBefore = LockButton(panel).Location;
        SendMouse(panel, MouseDown, new Point(30, 30));
        Assert.True(panel.Capture);
        SendMouse(panel, MouseMove, new Point(70, 60));
        Assert.Equal(new Point(before.X + 40, before.Y + 30), panel.Location);
        Assert.Equal(new Point(hotspotBefore.X + 40, hotspotBefore.Y + 30), LockButton(panel).Location);

        host.SetOverlayInteractionEnabled(false);
        Assert.True(panel.Capture);
        ClickLock(panel);
        Assert.False(panel.Capture);
        Assert.False(ManualInteraction(panel));
        AssertClickThrough(panel, true);
        Assert.Equal(panel.Left, Setting(settings.Current, xProperty));
        Assert.Equal(panel.Top, Setting(settings.Current, yProperty));
        var persisted = JsonFile.Load<AppSettings>(AppPaths.SettingsFile);
        Assert.Equal(panel.Left, Setting(persisted, xProperty));
        Assert.Equal(panel.Top, Setting(persisted, yProperty));
    });

    [Fact]
    public Task NativeLevelingNextWithoutAltAdvancesOnlyTheCurrentCharacterAndRejectsACharacterChangeDuringClick() => RunOnSta(() =>
    {
        using var host = new MainForm();
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var exploration = host.Services.GetRequiredService<ExplorationService>();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var panel = (LevelingOverlayForm)Field(host, "_levelingOverlay")!;
        settings.Current.LevelingOverlayEnabled = true;
        exploration.Rename("Alice");
        var alice = exploration.ActiveId;
        targets.StartRoute("first");
        targets.AdvanceRoute("first");
        targets.StartRoute("other");
        targets.AdvanceRoute("other");
        targets.AdvanceRoute("other");
        targets.StartRoute("first");
        exploration.Add("Bob");
        var bob = exploration.ActiveId;
        targets.StartRoute("first");
        _ = panel.Handle;
        RefreshLeveling(panel);
        ClickLock(panel);
        host.SetOverlayInteractionEnabled(false);
        var button = NextButton(panel);
        SendMouse(panel, MouseDown, button);
        SendMouse(panel, MouseUp, button);
        Assert.Equal(new RouteProgress(1, 4), targets.GetRouteProgress("first"));
        Assert.Null(targets.GetSavedRouteProgress("other"));

        exploration.Select(alice);
        RefreshLeveling(panel);
        Assert.Equal(new RouteProgress(1, 4), targets.GetRouteProgress("first"));
        Assert.Equal(new RouteProgress(2, 4), targets.GetSavedRouteProgress("other"));
        button = NextButton(panel);
        SendMouse(panel, MouseDown, button);
        exploration.Select(bob);
        SendMouse(panel, MouseUp, button);
        Assert.Equal(new RouteProgress(1, 4), targets.GetRouteProgress("first"));
        exploration.Select(alice);
        Assert.Equal(new RouteProgress(1, 4), targets.GetRouteProgress("first"));
        Assert.Equal(new RouteProgress(2, 4), targets.GetSavedRouteProgress("other"));
    });

    [Theory]
    [InlineData("_overlay")]
    [InlineData("_bossOverlay")]
    [InlineData("_levelingOverlay")]
    public Task HidingThePanelHidesItsLockAndClearsManualInteractionAndCapture(string field) => RunOnSta(() =>
    {
        using var host = new MainForm();
        var panel = PreparePanel(host, field);
        ClickLock(panel);
        SendMouse(panel, MouseDown, new Point(30, 30));
        Assert.True(panel.Capture);
        panel.Hide();
        Assert.False(panel.Visible);
        Assert.False(LockButton(panel).Visible);
        Assert.False(panel.Capture);
        Assert.False(ManualInteraction(panel));
        AssertClickThrough(panel, true);
        panel.Show();
        Assert.True(LockButton(panel).Visible);
        Assert.True(LockButton(panel).Locked);
        Assert.False(ManualInteraction(panel));
        AssertClickThrough(panel, true);
    });

    [Theory]
    [InlineData("_overlay")]
    [InlineData("_levelingOverlay")]
    public Task MapOrDungeonSuppressionHidesTheLockAndCancelsManualInteraction(string field) => RunOnSta(() =>
    {
        using var host = new MainForm();
        var panel = PreparePanel(host, field);
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var progress = targets.GetRouteProgress("first");
        ClickLock(panel);
        SendMouse(panel, MouseDown, new Point(30, 30));
        Suppress(panel, true);
        host.SetOverlayInteractionEnabled(false);
        panel.Show();
        Assert.False(panel.Visible);
        Assert.False(LockButton(panel).Visible);
        Assert.False(panel.Capture);
        Assert.False(ManualInteraction(panel));
        AssertClickThrough(panel, true);
        Assert.Equal(progress, targets.GetRouteProgress("first"));
        Suppress(panel, false);
        Assert.True(panel.Visible);
        Assert.True(LockButton(panel).Visible);
        Assert.True(LockButton(panel).Locked);
        Assert.False(ManualInteraction(panel));
        AssertClickThrough(panel, true);
    });

    [Theory]
    [InlineData("_overlay", "loot.unlock")]
    [InlineData("_bossOverlay", "boss.unlock")]
    [InlineData("_levelingOverlay", "leveling.unlock")]
    public Task ConfiguredGlobalHotkeyUsesTheSameNativeLockAction(string field, string action) => RunOnSta(() =>
    {
        using var host = new MainForm(); // Never activate the host or send input to the game.
        var panel = PreparePanel(host, field);
        var hotkeys = host.Services.GetRequiredService<OverlayHotkeyService>();
        hotkeys.Assign("loot.toggle", null);
        Assert.Null(hotkeys.Assign(action, new OverlayHotkey((int)Keys.F23, 6)));
        hotkeys.Attach(host);
        Assert.Null(hotkeys.Error(action));
        var index = OverlayHotkeys.Actions.Select(a => a.Id).ToList().IndexOf(action);
        Assert.False(ManualInteraction(panel));
        SendMessage(host.Handle, NativeMethods.WM_HOTKEY, 0x5100 + index, 0);
        Assert.True(ManualInteraction(panel));
        Assert.False(LockButton(panel).Locked);
        SendMessage(host.Handle, NativeMethods.WM_HOTKEY, 0x5100 + index, 0);
        Assert.False(ManualInteraction(panel));
        Assert.True(LockButton(panel).Locked);
        panel.Hide();
        SendMessage(host.Handle, NativeMethods.WM_HOTKEY, 0x5100 + index, 0);
        Assert.False(ManualInteraction(panel));
    });

    private static Form PreparePanel(MainForm host, string field)
    {
        var panel = (Form)Field(host, field)!;
        var settings = host.Services.GetRequiredService<SettingsService>();
        if (panel is PetOverlayForm) settings.Current.OverlayEnabled = true;
        if (panel is LevelingOverlayForm leveling)
        {
            settings.Current.LevelingOverlayEnabled = true;
            host.Services.GetRequiredService<MapTargetsService>().StartRoute("first");
            _ = leveling.Handle;
            RefreshLeveling(leveling);
        }
        var area = Screen.PrimaryScreen!.WorkingArea;
        panel.Bounds = new Rectangle(area.Left + 120, area.Top + 120, 360, 400);
        panel.Show();
        LockButton(panel).RefreshTarget();
        return panel;
    }

    private static void ClickLock(Form panel)
    {
        var hotspot = LockButton(panel);
        Assert.True(hotspot.Visible);
        SendMouse(hotspot, MouseDown, new Point(12, 12));
        SendMouse(hotspot, MouseUp, new Point(12, 12));
    }

    private static void SendMouse(Form panel, int message, Point point) =>
        SendMessage(panel.Handle, message, message == MouseUp ? 0 : 1, (nint)((point.Y << 16) | (point.X & 0xffff)));

    private static Point NextButton(LevelingOverlayForm panel)
    {
        var snapshot = (LevelingOverlaySnapshot)Field(panel, "_snapshot")!;
        var button = LevelingOverlayForm.ButtonBounds(LevelingOverlayAction.Next, snapshot.Window.Count);
        return new Point((int)button.X + 10, (int)button.Y + 10);
    }

    private static void RefreshLeveling(LevelingOverlayForm panel) => typeof(LevelingOverlayForm)
        .GetMethod("RefreshPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);

    private static OverlayLockButton LockButton(Form panel) => (OverlayLockButton)Field(panel, "_lockButton")!;
    private static object? Field(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static int Setting(AppSettings settings, string property) => (int)typeof(AppSettings).GetProperty(property)!.GetValue(settings)!;

    private static bool ManualInteraction(Form panel) => panel switch
    {
        PetOverlayForm pet => pet.ManualInteractionEnabled,
        BossOverlayForm boss => boss.ManualInteractionEnabled,
        LevelingOverlayForm leveling => leveling.ManualInteractionEnabled,
        _ => throw new InvalidOperationException("Unexpected overlay panel.")
    };

    private static void Suppress(Form panel, bool suppressed)
    {
        switch (panel)
        {
            case PetOverlayForm pet: pet.SetSuppressed(suppressed); break;
            case LevelingOverlayForm leveling: leveling.SetSuppressed(suppressed); break;
            default: throw new InvalidOperationException("Unexpected suppressed overlay panel.");
        }
    }

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
