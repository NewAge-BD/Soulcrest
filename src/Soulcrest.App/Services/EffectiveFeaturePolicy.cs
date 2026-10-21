namespace Soulcrest.App.Services;

public enum MapTrackingMode { NetworkMap, NetworkSuspended, VisualFallback }

/// <summary>Effective permissions only; never rewrites saved switches or progress.</summary>
public static class EffectiveFeaturePolicy
{
    // Abyss retains its native Instance kind in WorldContext. An explicit supported-map bridge
    // preserves the existing open-PvP map/HUD permissions for this particular native category.
    private static bool SupportedAbyss(WorldContextSnapshot context) => context.Kind == WorldContextKind.Instance
        && context.Category == "Abyss" && !string.IsNullOrEmpty(context.SoulcrestMapId)
        && (context.DungeonId is null || context.InstanceLayer == false);

    public static bool? ConfirmedInstance(WorldContextSnapshot? context) =>
        context?.Status == WorldContextStatus.Confirmed ? SupportedAbyss(context) ? false : context.Kind switch
        {
            WorldContextKind.Instance => true,
            WorldContextKind.OpenWorld => false,
            _ => null
        } : null;

    public static MapTrackingMode TrackingMode(bool networkEnabled, WorldContextSnapshot? context) =>
        !networkEnabled ? MapTrackingMode.VisualFallback
        : context?.Status == WorldContextStatus.Confirmed
            && (context.Kind == WorldContextKind.OpenWorld || SupportedAbyss(context))
            && !string.IsNullOrEmpty(context.SoulcrestMapId)
                ? MapTrackingMode.NetworkMap : MapTrackingMode.NetworkSuspended;

    public static bool AllowsLiveMap(WorldContextSnapshot? context, string mapId) =>
        AllowsLiveMap(true, context, mapId);

    public static bool AllowsLiveMap(bool networkEnabled, WorldContextSnapshot? context, string mapId) =>
        TrackingMode(networkEnabled, context) switch
        {
            MapTrackingMode.VisualFallback => true,
            MapTrackingMode.NetworkMap => context?.SoulcrestMapId == mapId,
            _ => false
        };
}
