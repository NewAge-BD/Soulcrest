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
        SeedSoulNotification(host, pet);
        var leveling = Panel<LevelingOverlayForm>(host, "_levelingOverlay");
        var boss = Panel<BossOverlayForm>(host, "_bossOverlay");
        targets.StartRoute("first");
        pet.Show();
        boss.Show();
        _ = leveling.Handle;
        Refresh(leveling, "RefreshPanel");
        Field(map, "<Running>k__BackingField").SetValue(map, true);
        Field(map, "<Found>k__BackingField").SetValue(map, true);
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
        SeedSoulNotification(host, pet);
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
        SeedSoulNotification(host, pet);
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
    public Task ConfiguredMenuLossHidesPanelsAndLocksUntilTheMinimapReturns() => RunSta(() =>
    {
        using var host = new MainForm();
        var map = host.Services.GetRequiredService<MapTrackingService>();
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        SeedSoulNotification(host, pet);
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
        // Boss defaults allow every context; this test deliberately configures a restrictive policy.
        settings.Current.OverlayVisibility["boss"] = new(false, false, false, false);
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
    public Task InteractionTickDoesNotPollContextButMapEventsApplyVisibility() => RunSta(() =>
    {
        using var host = new MainForm();
        _ = host.Handle; // Dispatch events without showing the host or starting capture.
        var map = host.Services.GetRequiredService<MapTrackingService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        SeedSoulNotification(host, pet);
        pet.Show();
        Field(map, "<Running>k__BackingField").SetValue(map, true);
        Field(map, "<Found>k__BackingField").SetValue(map, true);
        host.UpdateOverlayState();
        Assert.True(pet.Visible);

        // A timer tick only handles input/foreground. A map notification applies context rules.
        Field(map, "<Found>k__BackingField").SetValue(map, false);
        host.UpdateOverlayInteractionState();
        Assert.True(pet.Visible);
        map.UpdateEscMenuContext(true);
        Application.DoEvents();
        Assert.False(pet.Visible);
    });

    [RequiresTheory(TestRequirement.PrivateInstanceCatalog)]
    [InlineData(true, 600001)]
    [InlineData(false, 600001)]
    [InlineData(true, 111005)]
    [InlineData(false, 111005)]
    public Task NetworkInstanceUsesSavedInstanceChoiceWithoutObservedUiFlags(bool mapRunning, int mapDataId) => RunSta(() =>
    {
        using var host = new MainForm();
        _ = host.Handle;
        var map = host.Services.GetRequiredService<MapTrackingService>();
        var world = host.Services.GetRequiredService<WorldContextService>();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        var boss = Panel<BossOverlayForm>(host, "_bossOverlay");
        var route = Panel<RouteOverlayForm>(host, "_routeOverlay");
        SeedSoulNotification(host, pet);
        settings.Current.LootTrackingEnabled = true; // No tracker start in this host test.
        settings.Current.OverlayVisibility["loot"] = new(false, false, true, false);
        settings.Current.OverlayVisibility["boss"] = new(false, false, false, false);
        Field(map, "<Running>k__BackingField").SetValue(map, mapRunning);
        Field(map, "<Found>k__BackingField").SetValue(map, false);
        Field(map, "_worldMapOpen").SetValue(map, true);
        Field(map, "_escMenuOpen").SetValue(map, true);
        pet.Show(); boss.Show();

        // World notification is authoritative immediately, before delayed map/UI work can finish.
        world.ObserveOwn(mapDataId, 1, DateTimeOffset.UtcNow);
        Application.DoEvents();
        Assert.True(pet.Visible);
        Assert.False(boss.Visible);
        Assert.True((bool)Field(route, "_suppressed").GetValue(route)!);
        Assert.True(map.NetworkSuspended);
        settings.Update(s => s.OverlayVisibility["loot"] = s.OverlayVisibility["loot"] with { Instances = false });
        Application.DoEvents();
        Assert.False(pet.Visible); // Settings events apply without an interaction timer tick.
        settings.Update(s => s.OverlayVisibility["loot"] = s.OverlayVisibility["loot"] with { Instances = true });
        Application.DoEvents();
        Assert.True(pet.Visible);

        world.Invalidate(WorldContextStatus.Stale, 2, "synthetic-gap");
        Application.DoEvents();
        Assert.Null(map.NetworkInInstance);
        Assert.True(pet.Visible);
        Assert.True(boss.Visible); // Unknown is neither an observed missing minimap nor an instance.
        Assert.True((bool)Field(route, "_suppressed").GetValue(route)!); // Geometry stays disabled.
    });

    [RequiresFact(TestRequirement.PrivateInstanceCatalog)]
    public Task ReentrantWorldChangeFromVisibilityCallbackReappliesEveryHudWithoutTimerPolling() => RunSta(() =>
    {
        using var host = new MainForm();
        _ = host.Handle;
        var map = host.Services.GetRequiredService<MapTrackingService>();
        var world = host.Services.GetRequiredService<WorldContextService>();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        var leveling = Panel<LevelingOverlayForm>(host, "_levelingOverlay");
        var route = Panel<RouteOverlayForm>(host, "_routeOverlay");
        SeedSoulNotification(host, pet);
        targets.StartRoute("first");
        settings.Current.LootTrackingEnabled = true;
        settings.Current.OverlayVisibility["loot"] = new(false, false, true, false);
        settings.Current.OverlayVisibility["leveling"] = new(false, false, true, false);
        world.ObserveOwn(1110, 1, DateTimeOffset.UtcNow);
        Application.DoEvents();
        Field(map, "<Running>k__BackingField").SetValue(map, true);
        Field(map, "<Found>k__BackingField").SetValue(map, true);
        pet.Show();
        _ = leveling.Handle;
        Refresh(leveling, "RefreshPanel");
        host.UpdateOverlayState();
        Assert.True(pet.Visible);
        Assert.True(leveling.Visible);

        var switched = false;
        pet.VisibleChanged += (_, _) =>
        {
            if (pet.Visible || switched) return;
            switched = true;
            world.ObserveOwn(600001, 2, DateTimeOffset.UtcNow);
        };
        Field(map, "_worldMapOpen").SetValue(map, true);
        // Hiding the first HUD publishes a new context midway through this old world-map apply.
        host.UpdateOverlayState();
        Application.DoEvents();
        Assert.True(switched);
        Assert.Equal(600001, world.Snapshot.MapDataId);
        Assert.True(pet.Visible);
        Assert.True(leveling.Visible);
        Assert.True((bool)Field(route, "_suppressed").GetValue(route)!);
        Assert.Equal(new RouteProgress(0, 2), targets.GetRouteProgress("first"));
    });

    [Fact]
    public Task HostReleasesAllContextSubscriptionsOnDispose() => RunSta(() =>
    {
        var host = new MainForm();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var map = host.Services.GetRequiredService<MapTrackingService>();
        var world = host.Services.GetRequiredService<WorldContextService>();
        foreach (var publisher in new object[] { settings, map, world })
            Assert.True(HostSubscriberCount(publisher, host) > 0);
        host.Dispose();
        foreach (var publisher in new object[] { settings, map, world })
            Assert.Equal(0, HostSubscriberCount(publisher, host));
    });

    [Fact]
    public Task EscMenuHidesRestrictedHudAndMapWhileBossSpawnsRemainAllowed() => RunSta(() =>
    {
        using var host = new MainForm();
        var map = host.Services.GetRequiredService<MapTrackingService>();
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        SeedSoulNotification(host, pet);
        var leveling = Panel<LevelingOverlayForm>(host, "_levelingOverlay");
        var boss = Panel<BossOverlayForm>(host, "_bossOverlay");
        var routes = Panel<RouteOverlayForm>(host, "_routeOverlay");
        targets.StartRoute("first"); pet.Show(); boss.Show();
        _ = leveling.Handle; Refresh(leveling, "RefreshPanel");
        Field(map, "<Running>k__BackingField").SetValue(map, true);
        Field(map, "<Found>k__BackingField").SetValue(map, true);
        var oldGeneration = map.ContextGeneration;
        var oldPosition = new PlayerPosition("altgard", 100, 200, DateTimeOffset.UtcNow) { ContextGeneration = oldGeneration };
        Field(map, "<Position>k__BackingField").SetValue(map, oldPosition);
        Field(map, "<DetectedMap>k__BackingField").SetValue(map,
            new DetectedMapContext("altgard", false, oldPosition.At) { ContextGeneration = oldGeneration });
        Assert.True(map.CanUsePlayerPosition);
        var saved = _originalFiles.Keys.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null);
        map.UpdateEscMenuContext(true);
        host.UpdateOverlayState();
        Assert.False(map.Found); // Opening ESC revokes even a previously recognized minimap.
        Assert.True(map.ContextGeneration > oldGeneration);
        Assert.Null(map.Placement);
        Assert.Null(map.DetectedMap);
        Assert.Same(oldPosition, map.Position); // Last-known grey marker only.
        Assert.False(map.CanUsePlayerPosition);
        foreach (var panel in new Form[] { pet, leveling, routes })
        {
            Assert.False(panel.Visible);
            panel.Show();
            Assert.False(panel.Visible);
        }
        Assert.True(boss.Visible);
        Assert.False((bool)Field(boss, "_suppressed").GetValue(boss)!);
        foreach (var panel in new Form[] { pet, leveling })
            Assert.False(((Form)Field(panel, "_lockButton").GetValue(panel)!).Visible);
        Assert.Null(typeof(RouteOverlayForm).GetMethod("Compose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(routes, null));
        map.UpdateEscMenuContext(false);
        host.UpdateOverlayState();
        Assert.False(map.Found);
        Assert.False(map.CanUsePlayerPosition);
        Assert.False(pet.Visible); Assert.False(leveling.Visible); Assert.True(boss.Visible);
        // Closing ESC alone cannot revive the old map. Simulate a new successful image match.
        var freshPosition = new PlayerPosition("altgard", 100, 200, DateTimeOffset.UtcNow) { ContextGeneration = map.ContextGeneration };
        Field(map, "<Position>k__BackingField").SetValue(map, freshPosition);
        Field(map, "<DetectedMap>k__BackingField").SetValue(map,
            new DetectedMapContext("altgard", false, freshPosition.At) { ContextGeneration = map.ContextGeneration });
        Field(map, "<Found>k__BackingField").SetValue(map, true);
        Assert.True(map.CanUsePlayerPosition);
        host.UpdateOverlayState();
        Assert.True(pet.Visible); Assert.True(leveling.Visible); Assert.True(boss.Visible);
        Assert.Equal(new RouteProgress(0, 2), targets.GetRouteProgress("first"));
        foreach (var (path, bytes) in saved)
            Assert.Equal(bytes, File.Exists(path) ? File.ReadAllBytes(path) : null);
    });


    [Fact]
    public Task TimerContextChoicesControlOnlyTheStripAndRespectInstanceAndEscPermissionsSeparately() => RunSta(() =>
    {
        using var host = new MainForm(); // No host show: no game capture, WebView startup or network.
        var map = host.Services.GetRequiredService<MapTrackingService>();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var strip = Panel<AlertsOverlayForm>(host, "_alertsOverlay");
        var notifications = Panel<AlertNotificationsForm>(host, "_alertNotifications");
        var boss = Panel<BossOverlayForm>(host, "_bossOverlay");
        settings.Current.TimerOverlayTimerIds = ["event:catalog:shugo"];
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        _ = strip.Handle;
        strip.RefreshPanel();
        notifications.Show();
        Assert.True(strip.Visible);
        Field(map, "<Running>k__BackingField").SetValue(map, true);
        Field(map, "<Found>k__BackingField").SetValue(map, false);
        Field(map, "_worldMapOpen").SetValue(map, true);
        host.UpdateOverlayState();
        Assert.True(strip.Visible); // Worldmap is allowed by default, even without the minimap.
        strip.ToggleManualInteraction();
        Assert.True(strip.ManualInteractionEnabled);
        settings.Current.OverlayVisibility["alerts"] = settings.Current.OverlayVisibility["alerts"] with { WorldMap = false };
        host.UpdateOverlayState();
        Assert.False(strip.Visible);
        Assert.False(strip.ManualInteractionEnabled);
        Assert.False(((Form)Field(strip, "_lockButton").GetValue(strip)!).Visible);
        strip.Show(); strip.RefreshPanel();
        Assert.False(strip.Visible);
        settings.Current.OverlayVisibility["alerts"] = settings.Current.OverlayVisibility["alerts"] with { WorldMap = true };
        host.UpdateOverlayState();
        Assert.True(strip.Visible);
        Field(map, "_worldMapOpen").SetValue(map, false);
        host.UpdateOverlayState();
        Assert.False(strip.Visible); // Unexplained minimap loss follows the Menu permission.
        settings.Current.OverlayVisibility["alerts"] = settings.Current.OverlayVisibility["alerts"] with { Menu = true };
        host.UpdateOverlayState();
        Assert.True(strip.Visible);
        Assert.False((bool)Field(boss, "_suppressed").GetValue(boss)!); // Boss defaults allow all four contexts independently.
        settings.Current.OverlayVisibility["alerts"] = settings.Current.OverlayVisibility["alerts"] with { Menu = false };
        map.UpdateInstanceContext(false, false, TimeSpan.FromSeconds(30), null);
        Assert.True(map.InInstance);
        host.UpdateOverlayState();
        Assert.False(strip.Visible);
        settings.Current.OverlayVisibility["alerts"] = settings.Current.OverlayVisibility["alerts"] with { Instances = true };
        host.UpdateOverlayState();
        Assert.True(strip.Visible); // An allowed instance is not hidden by the missing minimap again.
        map.UpdateEscMenuContext(true);
        host.UpdateOverlayState();
        Assert.False(strip.Visible); // Explicit ESC inside an instance needs both choices.
        settings.Current.OverlayVisibility["alerts"] = settings.Current.OverlayVisibility["alerts"] with { Menu = true };
        host.UpdateOverlayState();
        Assert.True(strip.Visible);
        Assert.True(notifications.Visible);
        Assert.False((bool)Field(strip, "_suppressed").GetValue(notifications)!);
        settings.Current.OverlayVisibility["alerts"] = new(false, false, false, false);
        Field(map, "<Running>k__BackingField").SetValue(map, false);
        host.UpdateOverlayState();
        Assert.True(strip.Visible); // Stale context cannot suppress with tracking stopped.
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    });

    [Fact]
    public Task StoppedTrackingIgnoresStaleWorldMapMenuAndInstanceContexts() => RunSta(() =>
    {
        using var host = new MainForm();
        var map = host.Services.GetRequiredService<MapTrackingService>();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        var boss = Panel<BossOverlayForm>(host, "_bossOverlay");
        var leveling = Panel<LevelingOverlayForm>(host, "_levelingOverlay");
        SeedSoulNotification(host, pet);
        targets.StartRoute("first");
        _ = leveling.Handle;
        Refresh(leveling, "RefreshPanel");
        pet.Show(); boss.Show();
        settings.Current.OverlayVisibility["boss"] = new(false, false, false, false);
        Field(map, "<Running>k__BackingField").SetValue(map, true);
        Field(map, "<Found>k__BackingField").SetValue(map, false);
        Field(map, "_worldMapOpen").SetValue(map, true);
        map.UpdateInstanceContext(false, false, TimeSpan.FromSeconds(30), null);
        map.UpdateEscMenuContext(true);
        host.UpdateOverlayState();
        Assert.All(new Form[] { pet, boss, leveling }, form => Assert.False(form.Visible));
        Field(map, "<Running>k__BackingField").SetValue(map, false);
        host.UpdateOverlayState();
        Assert.All(new Form[] { pet, boss, leveling }, form => Assert.True(form.Visible));
        Assert.Equal(new RouteProgress(0, 2), targets.GetRouteProgress("first"));
    });

    [Fact]
    public Task PlacementRestorationPreservesSavedOffScreenCoordinatesAndUsesCurrentPetLayout() => RunSta(() =>
    {
        using var host = new MainForm();
        var settings = host.Services.GetRequiredService<SettingsService>();
        var targets = host.Services.GetRequiredService<MapTargetsService>();
        var leveling = Panel<LevelingOverlayForm>(host, "_levelingOverlay");
        var pet = Panel<PetOverlayForm>(host, "_overlay");
        SeedSoulNotification(host, pet);
        targets.StartRoute("first");
        _ = leveling.Handle;
        Refresh(leveling, "RefreshPanel");
        pet.Show();
        pet.SetPlacementHidden(true);
        leveling.SetPlacementHidden(true);
        settings.Update(s =>
        {
            s.LevelingOverlayX = 80000; s.LevelingOverlayY = -70000;
            s.OverlayX = -1100; s.OverlayY = 180; s.OverlayScale = 1.5;
        });
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        pet.SetPlacementHidden(false);
        leveling.SetPlacementHidden(false);
        Assert.Equal((-1100, 180), (pet.Left, pet.Top));
        Assert.Equal(540, pet.Width);
        Assert.Equal((80000, -70000), (settings.Current.LevelingOverlayX, settings.Current.LevelingOverlayY));
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
        Assert.True(leveling.Visible);
        Assert.Equal(new RouteProgress(0, 2), targets.GetRouteProgress("first"));
    });

    private static void SeedSoulNotification(MainForm host, PetOverlayForm pet)
    {
        // These cases exercise HUD suppression with an actual notification, not the now-hidden empty header.
        var progress = host.Services.GetRequiredService<ProgressService>();
        ((List<LoggedSoul>)Field(progress, "_recent").GetValue(progress)!).Insert(0,
            new(DateTimeOffset.Now, "synthetic-visibility-pet", 1, "network", "synthetic notification"));
        Refresh(pet, "RefreshLayout");
    }

    private static T Panel<T>(MainForm host, string name) where T : Form =>
        (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private static FieldInfo Field(object panel, string name) => panel.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static int HostSubscriberCount(object publisher, MainForm host) =>
        ((MulticastDelegate?)Field(publisher, "Changed").GetValue(publisher))?.GetInvocationList()
            .Count(callback => ReferenceEquals(callback.Target, host)) ?? 0;
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
