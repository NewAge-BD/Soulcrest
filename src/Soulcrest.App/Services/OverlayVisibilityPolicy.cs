namespace Soulcrest.App.Services;

public sealed record OverlayVisibilityRules(bool WorldMap = true, bool Menu = true,
    bool Instances = true, bool MissingMinimap = true);

public readonly record struct OverlayContext(bool TrackingRunning, bool MinimapFound,
    bool WorldMapOpen, bool EscMenuOpen, bool InInstance, bool? NetworkInInstance = null)
{
    // No observation is different from an observed missing minimap or open menu.
    public bool VisualObservationSuspended { get; init; }
}

/// <summary>Independent, overlapping context permissions for each freely placed HUD.</summary>
public static class OverlayVisibilityPolicy
{
    public static OverlayVisibilityRules Get(AppSettings settings, string id) =>
        settings.OverlayVisibility?.GetValueOrDefault(id) ?? Initial(settings, id);

    private static OverlayVisibilityRules Initial(AppSettings settings, string id) => id switch
    {
        "loot" or "leveling" => new(false, false, false, false),
        // User request 2026-10-20: boss spawns remain visible in all four contexts.
        "boss" or "notifications" or "farm-loot" => new(),
        "event-timeline" => new(true, !settings.EventTimelineFollowMinimap, true, !settings.EventTimelineFollowMinimap),
        "alerts" => new(settings.TimerOverlayShowWorldMap, settings.TimerOverlayShowMenu, settings.TimerOverlayShowInstances, true),
        _ => throw new ArgumentException("This overlay has no configurable HUD context policy.", nameof(id))
    };

    public static bool IsForegroundSuppressed(AppSettings settings, bool gameInForeground) =>
        settings.OverlayShowMode != "always" && !gameInForeground;

    internal static void Normalize(AppSettings settings)
    {
        settings.OverlayVisibility ??= new(StringComparer.Ordinal);
        foreach (var item in OverlayRegistry.All.Where(item => item.CanPlace))
            settings.OverlayVisibility[item.Id] = Get(settings, item.Id);
    }

    public static bool IsSuppressed(AppSettings settings, string id, OverlayContext context) =>
        IsSuppressed(Get(settings, id), context);

    public static bool IsSuppressed(OverlayVisibilityRules rules, OverlayContext context)
    {
        if (!context.TrackingRunning || context.VisualObservationSuspended)
            return context.NetworkInInstance == true && !rules.Instances;
        var inInstance = context.NetworkInInstance ?? context.InInstance;
        var missing = !context.MinimapFound && !context.WorldMapOpen;
        var menu = context.EscMenuOpen || missing && !inInstance;
        return context.WorldMapOpen && !rules.WorldMap
            || menu && !rules.Menu
            || inInstance && !rules.Instances
            || missing && !rules.MissingMinimap;
    }
}
