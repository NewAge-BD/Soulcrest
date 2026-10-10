using System.Reflection;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlaySuppressionTests : IDisposable
{
    private readonly Dictionary<string, byte[]?> _originalFiles = [];
    private readonly HashSet<string> _originalDefects;
    private readonly string _originalLanguage = UiText.Language;

    public OverlaySuppressionTests()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        _originalDefects = Directory.GetFiles(AppPaths.DataDirectory, "settings.json.defekt-*").ToHashSet();
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
            OverlayEnabled = true,
            OverlayX = 100,
            OverlayY = 100,
            LevelingOverlayEnabled = true,
            LevelingOverlayX = 500,
            LevelingOverlayY = 100,
            CheckUpdatesOnStart = false,
            BossRushEnabled = true,
            BossOverlayEnabled = true,
            LootTrackingEnabled = false
        });
        RouteFile.Save(AppPaths.RoutesFile, new[]
        {
            new SavedRoute("first", "First route",
                [new RouteStop("altgard", 100, 200, "Quest one", "NPCs", null),
                    new RouteStop("altgard", 200, 300, "Quest two", "NPCs", null)],
                Category: LevelingRouteStyle.Category),
            new SavedRoute("second", "Second route",
                [new RouteStop("altgard", 300, 400, "Quest three", "NPCs", null)],
                Category: LevelingRouteStyle.Category)
        });
    }

    public void Dispose()
    {
        foreach (var (path, contents) in _originalFiles)
        {
            if (contents is null) File.Delete(path);
            else File.WriteAllBytes(path, contents);
        }
        foreach (var path in Directory.GetFiles(AppPaths.DataDirectory, "settings.json.defekt-*"))
            if (!_originalDefects.Contains(path)) File.Delete(path);
        UiText.Language = _originalLanguage;
    }

    [Fact]
    public Task HostHidesOnlyLootAndLevelingForMapOrInstanceAndTogglesSavedPreference() => RunSta(() =>
    {
        using var host = new MainForm();
        var map = host.Services.GetRequiredService<MapTrackingService>();
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        var leveling = Panel<LevelingOverlayForm>(host, "_levelingOverlay");
        var boss = Panel<BossOverlayForm>(host, "_bossOverlay");
        targets.StartRoute("first");
        pet.Show();
        boss.Show();
        _ = leveling.Handle;
        Refresh(leveling, "RefreshPanel");
        Field(map, "_worldMapOpen").SetValue(map, true);
        host.UpdateOverlayState();
        Assert.False(pet.Visible);
        Assert.False(leveling.Visible);
        Assert.True(boss.Visible);
        Assert.True(settings.Current.OverlayEnabled);
        host.ToggleOverlay();
        Assert.False(settings.Current.OverlayEnabled);
        Field(map, "_worldMapOpen").SetValue(map, false);
        host.UpdateOverlayState();
        Assert.False(pet.Visible);
        Assert.True(leveling.Visible);
        host.ToggleOverlay();
        Assert.True(pet.Visible);
        map.UpdateInstanceContext(false, false, TimeSpan.FromSeconds(30), null);
        host.UpdateOverlayState();
        Assert.False(pet.Visible);
        Assert.False(leveling.Visible);
        Assert.True(boss.Visible);
        map.UpdateInstanceContext(true, false, TimeSpan.Zero, null);
        host.UpdateOverlayState();
        Assert.True(pet.Visible);
        Assert.True(leveling.Visible);
        Assert.Equal(new RouteProgress(0, 2), targets.GetRouteProgress("first"));
    });

    [Fact]
    public Task SuppressionHidesPanelsCancelsInputAndSurvivesShowAndRefreshWithoutChangingProgress() => RunSta(() =>
    {
        using var host = new MainForm(); // No host show: no capture, hotkeys, WebView startup or network.
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var exploration = host.Services.GetRequiredService<ExplorationService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        var leveling = Panel<LevelingOverlayForm>(host, "_levelingOverlay");
        targets.StartRoute("first");
        pet.Show();
        _ = leveling.Handle;
        Refresh(leveling, "RefreshPanel");
        Assert.True(pet.Visible);
        Assert.True(leveling.Visible);
        var petPosition = pet.Location;
        var levelingPosition = leveling.Location;
        var saved = _originalFiles.Keys.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);

        Field(leveling, "_pressed").SetValue(leveling, LevelingOverlayAction.Next);
        Field(leveling, "_pressedRouteId").SetValue(leveling, "first");
        Field(leveling, "_pressedCharacterId").SetValue(leveling, exploration.ActiveId);
        Field(leveling, "_pressedProgress").SetValue(leveling, targets.GetRouteProgress("first"));
        pet.Capture = true;
        leveling.Capture = true;
        pet.SetSuppressed(true);
        leveling.SetSuppressed(true);
        Assert.False(pet.Visible);
        Assert.False(leveling.Visible);
        Assert.False(pet.Capture);
        Assert.False(leveling.Capture);
        Assert.Equal(LevelingOverlayAction.None, Field(leveling, "_pressed").GetValue(leveling));
        Assert.Null(Field(leveling, "_pressedRouteId").GetValue(leveling));

        pet.Show();
        leveling.Show();
        Refresh(pet, "RefreshLayout");
        Refresh(leveling, "RefreshPanel");
        pet.SetInteractionEnabled(true);
        leveling.SetInteractionEnabled(true);
        Assert.False(pet.Visible);
        Assert.False(leveling.Visible);
        Assert.True(pet.Locked);
        Assert.True((bool)Field(leveling, "_locked").GetValue(leveling)!);
        Assert.Equal(new RouteProgress(0, 2), targets.GetRouteProgress("first"));

        pet.SetSuppressed(false);
        leveling.SetSuppressed(false);
        Assert.True(pet.Visible);
        Assert.True(leveling.Visible);
        Assert.Equal(petPosition, pet.Location);
        Assert.Equal(levelingPosition, leveling.Location);
        Assert.True(settings.Current.OverlayEnabled);
        Assert.True(settings.Current.LevelingOverlayEnabled);
        foreach (var (path, bytes) in saved)
            Assert.Equal(bytes, File.Exists(path) ? File.ReadAllBytes(path) : null);
    });

    [Fact]
    public Task RestorationUsesCurrentPreferenceAndActiveRouteInsteadOfStaleVisibility() => RunSta(() =>
    {
        using var host = new MainForm();
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        var leveling = Panel<LevelingOverlayForm>(host, "_levelingOverlay");
        targets.StartRoute("first");
        pet.Show();
        _ = leveling.Handle;
        Refresh(leveling, "RefreshPanel");
        pet.SetSuppressed(true);
        leveling.SetSuppressed(true);
        settings.Current.OverlayEnabled = false;
        targets.StopRoute("first");
        pet.SetSuppressed(false);
        leveling.SetSuppressed(false);
        Assert.False(pet.Visible);
        Assert.False(leveling.Visible);

        pet.SetSuppressed(true);
        leveling.SetSuppressed(true);
        settings.Current.OverlayEnabled = true;
        targets.StartRoute("second");
        pet.SetSuppressed(false);
        leveling.SetSuppressed(false);
        Assert.True(pet.Visible);
        Assert.True(leveling.Visible);
        Assert.Equal("second", ((LevelingOverlaySnapshot)Field(leveling, "_snapshot").GetValue(leveling)!).RouteId);

        leveling.SetSuppressed(true);
        settings.Current.LevelingOverlayEnabled = false;
        leveling.SetSuppressed(false);
        Assert.False(leveling.Visible);
        Assert.Equal("second", targets.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(0, 1), targets.GetRouteProgress("second"));
    });

    [Fact]
    public Task MenuLossHidesAllHudPanelsAndLocksUntilTheMinimapReturns() => RunSta(() =>
    {
        using var host = new MainForm();
        var map = host.Services.GetRequiredService<MapTrackingService>();
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        var leveling = Panel<LevelingOverlayForm>(host, "_levelingOverlay");
        var boss = Panel<BossOverlayForm>(host, "_bossOverlay");
        targets.StartRoute("first");
        pet.Show(); boss.Show();
        _ = leveling.Handle;
        Refresh(leveling, "RefreshPanel");
        var saved = _originalFiles.Keys.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null);
        Field(map, "<Running>k__BackingField").SetValue(map, true);
        Field(map, "<Found>k__BackingField").SetValue(map, true);
        host.UpdateOverlayState();
        Assert.All(new Form[] { pet, leveling, boss }, panel => Assert.True(panel.Visible));
        boss.ToggleManualInteraction();
        Field(map, "<Found>k__BackingField").SetValue(map, false); // same loss that hides map markers
        host.UpdateOverlayState();
        foreach (var panel in new Form[] { pet, leveling, boss })
        {
            Assert.False(panel.Visible);
            Assert.False(((Form)Field(panel, "_lockButton").GetValue(panel)!).Visible);
            panel.Show(); // hotkeys/refresh cannot leak the HUD into a menu
            panel.GetType().GetMethod("SetInteractionEnabled")!.Invoke(panel, [true]);
            Assert.False(panel.Visible);
        }
        Refresh(boss, "RefreshPanel");
        Assert.False(boss.Visible);
        Assert.False(boss.ManualInteractionEnabled);
        Assert.True((bool)Field(boss, "_locked").GetValue(boss)!);
        settings.Current.BossOverlayVisible = false; // a preference changed while hidden is respected
        Field(map, "<Found>k__BackingField").SetValue(map, true);
        host.UpdateOverlayState();
        Assert.True(pet.Visible); Assert.True(leveling.Visible); Assert.False(boss.Visible);
        settings.Current.BossOverlayVisible = true;
        Refresh(boss, "RefreshPanel");
        Assert.True(boss.Visible);
        Assert.Equal(new RouteProgress(0, 2), targets.GetRouteProgress("first"));
        foreach (var (path, bytes) in saved)
            Assert.Equal(bytes, File.Exists(path) ? File.ReadAllBytes(path) : null);
        // Stopping tracking cannot leave menus inferred forever.
        Field(map, "<Found>k__BackingField").SetValue(map, false);
        host.UpdateOverlayState();
        Assert.False(pet.Visible);
        Field(map, "<Running>k__BackingField").SetValue(map, false);
        host.UpdateOverlayState();
        Assert.True(pet.Visible); Assert.True(leveling.Visible); Assert.True(boss.Visible);
    });

    [Fact]
    public Task EscMenuHidesHudLocksAndMapEvenWhileTheMinimapIsStillFound() => RunSta(() =>
    {
        using var host = new MainForm();
        var map = host.Services.GetRequiredService<MapTrackingService>();
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        var leveling = Panel<LevelingOverlayForm>(host, "_levelingOverlay");
        var boss = Panel<BossOverlayForm>(host, "_bossOverlay");
        var routes = Panel<RouteOverlayForm>(host, "_routeOverlay");
        targets.StartRoute("first"); pet.Show(); boss.Show();
        _ = leveling.Handle; Refresh(leveling, "RefreshPanel");
        Field(map, "<Running>k__BackingField").SetValue(map, true);
        Field(map, "<Found>k__BackingField").SetValue(map, true);
        var saved = _originalFiles.Keys.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null);
        map.UpdateEscMenuContext(true);
        host.UpdateOverlayState();
        Assert.True(map.Found); // reproduces user's ESC menu with a map recognized behind it
        foreach (var panel in new Form[] { pet, leveling, boss, routes })
        {
            Assert.False(panel.Visible);
            panel.Show();
            Assert.False(panel.Visible);
        }
        foreach (var panel in new Form[] { pet, leveling, boss })
            Assert.False(((Form)Field(panel, "_lockButton").GetValue(panel)!).Visible);
        Assert.Null(typeof(RouteOverlayForm).GetMethod("Compose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(routes, null));
        map.UpdateEscMenuContext(false);
        host.UpdateOverlayState();
        Assert.True(pet.Visible); Assert.True(leveling.Visible); Assert.True(boss.Visible);
        Assert.Equal(new RouteProgress(0, 2), targets.GetRouteProgress("first"));
        foreach (var (path, bytes) in saved)
            Assert.Equal(bytes, File.Exists(path) ? File.ReadAllBytes(path) : null);
    });

    [Fact]
    public Task PetScanPanelStaysVisibleWithoutMinimap() => RunSta(() =>
    {
        // User request 2026-10-10: the pet window covers the minimap during the scan, so the minimap rule
        // must not hide the scan panel; after the scan the rule applies again.
        using var host = new MainForm();
        var scan = host.Services.GetRequiredService<PetScanService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        pet.Show();
        pet.SetSuppressed(true);
        Assert.False(pet.Visible);

        Field(scan, "<Running>k__BackingField").SetValue(scan, true);
        Refresh(pet, "RefreshLayout");
        Assert.True(pet.Visible);

        Field(scan, "<Running>k__BackingField").SetValue(scan, false);
        Refresh(pet, "RefreshLayout");
        Assert.False(pet.Visible);
    });

    private static T Panel<T>(MainForm host, string name) where T : Form =>
        (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private static FieldInfo Field(object panel, string name) => panel.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Refresh(Form panel, string name) => panel.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);

    private static async Task RunSta(Action action)
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
