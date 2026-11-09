using Soulcrest.App.Services;

namespace Soulcrest.App.OverlayEditorKit;

public sealed record OverlayEditorLiveContext(bool GameAvailable, bool GameForeground, bool AllowBackground,
    OverlayTriggerArea? Area, MapVisualContextSnapshot Visual);

/// <summary>Window visibility adds filters to the existing qualified presentation; it never authorizes a source.</summary>
public static class OverlayEditorLivePolicy
{
    public static bool IsVisible(OverlaySettings settings, OverlaySnapshot snapshot, OverlayEditorLiveContext context)
    {
        var result = IsVisibleCore(settings, snapshot, context);
        OverlayEditorLiveDiagnostics.Policy(settings, snapshot, context, result);
        return result;
    }

    private static bool IsVisibleCore(OverlaySettings settings, OverlaySnapshot snapshot, OverlayEditorLiveContext context)
    {
        if (!settings.Enabled || (!context.AllowBackground && !context.GameForeground)) return false;
        if (settings.Visibility != "always" && !context.GameAvailable) return false;
        if (GenusInsightOverlayContent.Dedicated(settings) && !GenusInsightOverlayContent.Available(snapshot.GenusInsight)) return false;
        if (settings.Visibility == "session" && (!snapshot.TrackingAvailable || !snapshot.SessionActive)) return false;
        var observations = new OverlayTriggerContextSnapshot(context.Area, context.GameForeground,
            snapshot.TrackingAvailable ? snapshot.SessionActive : null,
            context.Visual.WorldMap, context.Visual.EscMenu, context.Visual.MinimapMissing);
        return OverlayTriggerPolicy.Matches(settings.TriggerRules, observations);
    }

    public static OverlayTriggerArea? Area(bool networkEnabled, WorldContextSnapshot world) =>
        !networkEnabled || world.Status != WorldContextStatus.Confirmed ? null : world.Kind switch
        {
            WorldContextKind.OpenWorld => OverlayTriggerArea.OpenWorld,
            WorldContextKind.Instance when world.Category == "Abyss" => OverlayTriggerArea.Abyss,
            WorldContextKind.Instance => OverlayTriggerArea.Instances,
            _ => null,
        };
}
