using System.Drawing;

namespace Soulcrest.App.Services;

public sealed class AppSettings
{
    public string NameLanguage { get; set; } = "en";
    public string UiLanguage { get; set; } = "";
    public bool AutoOcrLanguage { get; set; } = true;
    public string OcrLanguage { get; set; } = "en-US";
    public string? DetectedGameLanguage { get; set; }
    public string OverlayLanguage { get; set; } = "auto";
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveOcrLanguage => AutoOcrLanguage ? "auto" : OcrLanguage;

    /// <summary>Loot names follow the game unless the overlay has an explicit language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string LootNameLanguage => OverlayLanguage is "de" or "en" ? OverlayLanguage
        : !AutoOcrLanguage ? (OcrLanguage == "de-DE" ? "de" : "en")
        : DetectedGameLanguage is "de" or "en" ? DetectedGameLanguage : NameLanguage;

    public bool LootTrackingEnabled { get; set; } = true;

    /// <summary>Look for a new version on GitHub after every start (UpdateService).</summary>
    public bool CheckUpdatesOnStart { get; set; } = true;

    public bool OverlayEnabled { get; set; } = true;
    public int OverlayX { get; set; } = 40;
    public int OverlayY { get; set; } = 200;
    public string LastMap { get; set; } = "altgard";
    public int? MainX { get; set; }
    public int? MainY { get; set; }
    public int? MainWidth { get; set; }
    public int? MainHeight { get; set; }
    public bool MainWindowMaximized { get; set; }

    /// <summary>Position and size of the Soulcrest window (restored on the next start).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Rectangle? MainWindowBounds
    {
        get => MainX is { } x && MainY is { } y && MainWidth is { } w && MainHeight is { } h && w > 0 && h > 0 ? new Rectangle(x, y, w, h) : null;
        set
        {
            MainX = value?.X;
            MainY = value?.Y;
            MainWidth = value?.Width;
            MainHeight = value?.Height;
        }
    }
    public int? PetWindowX { get; set; }
    public int? PetWindowY { get; set; }
    public int? PetWindowWidth { get; set; }
    public int? PetWindowHeight { get; set; }

    public int? MapRegionX { get; set; }
    public int? MapRegionY { get; set; }
    public int? MapRegionWidth { get; set; }
    public int? MapRegionHeight { get; set; }

    /// <summary>Where the player marker sits in the marked map area (fractions; the in-game map keeps it there).</summary>
    public double PlayerAnchorX { get; set; } = 0.5;
    public double PlayerAnchorY { get; set; } = 0.5;

    /// <summary>Opt-in: the interactive map follows the tracked player (and switches to the map the player is on).</summary>
    public bool FollowPlayer { get; set; }

    /// <summary>
    /// Usual zoom of the small map (screen pixels per map unit). Remembered so that a world map that is
    /// already open when tracking starts is not taken for the small map.
    /// </summary>
    public double? MiniMapZoom { get; set; }

    /// <summary>Tracking was running when the app closed: resume it on the next start.</summary>
    public bool MapTrackingActive { get; set; }

    /// <summary>Lines to the marked targets over the in-game map (RouteOverlayForm).</summary>
    public bool ShowRoutesInGame { get; set; } = true;

    /// <summary>Pet symbols with level and souls on the in-game map (RouteOverlayForm).</summary>
    public bool ShowPetsInGame { get; set; } = true;

    /// <summary>Resources and hidden cubes shown in the map legend also on the in-game map (RouteOverlayForm).</summary>
    public bool ShowResourcesInGame { get; set; } = true;

    /// <summary>
    /// Maps (ids) whose guards get a red danger circle on the in-game map: the guards at the Kibelisks
    /// (&lt;Dawn Legion&gt; Guard, &lt;Vigilante Group&gt; Guard) kill players of the other faction with one hit
    /// (user request 2026-10-07). Off by default; chosen per map (Altgard, Verteron).
    /// </summary>
    public string[] GuardZoneMaps { get; set; } = [];

    /// <summary>Radius of a guard's danger circle in game metres (user decision 2026-10-07: 40 m, adjustable).</summary>
    public int GuardRadiusMeters { get; set; } = 40;

    /// <summary>The player's faction (<see cref="Factions"/>), chosen in the setup; sets <see cref="GuardZoneMaps"/>.</summary>
    public string? Faction { get; set; }

    /// <summary>Size of the pet overlay (1 = 360 px wide); dragged at a corner while unlocked (Ctrl+Alt+L).</summary>
    public double OverlayScale { get; set; } = 1.0;

    /// <summary>
    /// Legend selection of the interactive map per map id: shown categories as "Group/Name" (names stay
    /// when the data package is rebuilt, indices do not). Replaced as a whole on every change, so the
    /// in-game overlay can read it from another thread.
    /// </summary>
    public Dictionary<string, string[]> MapCategories { get; set; } = [];

    /// <summary>
    /// Resource kinds hidden in the legend per map id, as "Group/Category/Kind" (Gem: Ruby hidden while
    /// Sapphire and Diamond stay). Replaced as a whole on every change, like <see cref="MapCategories"/>.
    /// </summary>
    public Dictionary<string, string[]> MapHiddenKinds { get; set; } = [];

    /// <summary>Player tracking rests in instances without a world map and comes back by itself (saves power).</summary>
    public bool PauseTrackingInInstances { get; set; } = true;

    /// <summary>
    /// Overlays appear in screen recordings and screenshots (OBS display capture). Off by default: then
    /// they are shown on the monitor only.
    /// </summary>
    public bool OverlaysInRecordings { get; set; }

    /// <summary>Progression mode: always lead to the nearest pet below <see cref="ProgressionGoal"/> souls.</summary>
    public bool ProgressionEnabled { get; set; }
    public int ExplorationCompletionRadius { get; set; } = 15;

    /// <summary>
    /// One-time adjustments of saved settings (1: completion radius default 40 → 15, 2026-10-05;
    /// 2: existing installations count the first-start tutorial as seen, 2026-10-07).
    /// </summary>
    public int SettingsRevision { get; set; }

    /// <summary>The first-start tutorial (minimap) was closed.</summary>
    public bool TutorialDone { get; set; }
    public string ProgressionMode { get; set; } = "pets";

    /// <summary>Total souls a pet should reach: 5 (unlocked), 30 (level 2) or 105 (max).</summary>
    public int ProgressionGoal { get; set; } = 5;

    /// <summary>Pets of these levels are hidden on the map and in the in-game map overlay (3 = finished).</summary>
    public bool HideLevel1 { get; set; }
    public bool HideLevel2 { get; set; }
    public bool HideLevel3 { get; set; }

    /// <summary>Show only pets found on no other map than the one shown (map and in-game map overlay).</summary>
    public bool ExclusivePetsOnly { get; set; }

    public bool HidesLevel(int level) => level switch
    {
        1 => HideLevel1,
        2 => HideLevel2,
        >= 3 => HideLevel3,
        _ => false,
    };

    /// <summary>Screen area of the in-game map for player tracking (docs/MAP_TRACKING.md).</summary>
    public Rectangle? MapRegion
    {
        get => MapRegionX is { } x && MapRegionY is { } y && MapRegionWidth is { } w && MapRegionHeight is { } h && w > 0 && h > 0
            ? new Rectangle(x, y, w, h) : null;
        set
        {
            MapRegionX = value?.X;
            MapRegionY = value?.Y;
            MapRegionWidth = value?.Width;
            MapRegionHeight = value?.Height;
        }
    }

    /// <summary>Screen area of the game window for reading the in-game pet window (null = primary screen).</summary>
    public Rectangle? PetWindowRegion
    {
        get => PetWindowX is { } x && PetWindowY is { } y && PetWindowWidth is { } w && PetWindowHeight is { } h && w > 0 && h > 0
            ? new Rectangle(x, y, w, h) : null;
        set
        {
            PetWindowX = value?.X;
            PetWindowY = value?.Y;
            PetWindowWidth = value?.Width;
            PetWindowHeight = value?.Height;
        }
    }


}

public sealed class SettingsService
{
    private readonly object _gate = new();
    public SettingsService()
    {
        var existed = File.Exists(AppPaths.SettingsFile);
        Current = JsonFile.Load<AppSettings>(AppPaths.SettingsFile);
        if (Current.UiLanguage is not ("de" or "en")) Current.UiLanguage = Current.NameLanguage == "de" ? "de" : "en";
        if (Current.OverlayLanguage is not ("auto" or "de" or "en")) Current.OverlayLanguage = "auto";
        if (Current.DetectedGameLanguage is not ("de" or "en")) Current.DetectedGameLanguage = null;
        if (Current.OcrLanguage is not ("de-DE" or "en-US"))
        {
            Current.OcrLanguage = "en-US";
            Current.AutoOcrLanguage = true;
        }
        if (Current.SettingsRevision < 1)
        {
            // The old default was saved with every settings file; a radius the user picked stays.
            if (Current.ExplorationCompletionRadius == 40)
                Current.ExplorationCompletionRadius = 15;
            Current.SettingsRevision = 1;
        }
        if (Current.SettingsRevision < 2)
        {
            // The tutorial is for fresh installations; whoever used Soulcrest before has set it up already.
            Current.TutorialDone |= existed;
            Current.SettingsRevision = 2;
        }
        UiText.Language = Current.UiLanguage;
    }

    public AppSettings Current { get; }

    public event Action? Changed;

    /// <summary>Only confident OCR hints replace the last known game language; unchanged frames do not write settings.</summary>
    public void RememberGameLanguage(string? language)
    {
        if (language is not ("de" or "en")) return;
        lock (_gate)
        {
            if (Current.DetectedGameLanguage == language) return;
            Current.DetectedGameLanguage = language;
            JsonFile.Save(AppPaths.SettingsFile, Current);
        }
        Changed?.Invoke();
    }

    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            UiText.Language = Current.UiLanguage;
            JsonFile.Save(AppPaths.SettingsFile, Current);
        }
        Changed?.Invoke();
    }
}
