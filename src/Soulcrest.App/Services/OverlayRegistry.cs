namespace Soulcrest.App.Services;

public sealed record OverlayDescriptor(string Id, string Title, bool CanPlace,
    double MinScale = 1, double MaxScale = 1, int? MinWidth = null, int? MaxWidth = null);

/// <summary>Physical desktop coordinates; Width is a logical, unscaled content width.</summary>
public sealed record OverlayLayout(int X, int Y, double Scale, int? Width = null);

/// <summary>One inventory and layout adapter for the existing overlay settings.</summary>
public static class OverlayRegistry
{
    public static IReadOnlyList<OverlayDescriptor> All { get; } = Array.AsReadOnly(new[]
    {
        new OverlayDescriptor("loot", "Pet-Fortschritt", true, .6, 2.5),
        new OverlayDescriptor("achievements", "Achievement Tracker", true, .6, 2.5),
        new OverlayDescriptor("genus-insight", "Genus Insight", true, .6, 2),
        new OverlayDescriptor("boss", "Boss-Spawns", true, .6, 2),
        new OverlayDescriptor("leveling", "Leveling", true, .7, 1.6),
        new OverlayDescriptor("alerts", "Timer-Leiste", true, .6, 2),
        new OverlayDescriptor("notifications", "Benachrichtigungen", true, .6, 2, 240, 1000),
        new OverlayDescriptor("event-timeline", "Timeline", true, .6, 2, 360, 1600),
        new OverlayDescriptor("map", "Karten-Overlay", false),
        new OverlayDescriptor("merchant", "Händler-Soulcounter", false),
        new OverlayDescriptor("daevanion", "Daevanion-Board", false)
    });

    private static OverlayLayout AchievementLayout(AppSettings settings)
    {
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var scale = double.IsFinite(settings.AchievementOverlayScale) ? Math.Clamp(settings.AchievementOverlayScale, .6, 2.5) : 1;
        return new(settings.AchievementOverlayX ?? workingArea.Right - 24 - (int)Math.Round(360 * scale),
            settings.AchievementOverlayY ?? workingArea.Top + 24, scale);
    }

    private static OverlayLayout GenusInsightLayout(AppSettings settings) => GenusInsightLayout(settings,
        Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080));

    internal static OverlayLayout GenusInsightLayout(AppSettings settings, System.Drawing.Rectangle workingArea)
    {
        var scale = double.IsFinite(settings.GenusInsightOverlayScale) ? Math.Clamp(settings.GenusInsightOverlayScale, .6, 2) : 1;
        var width = (int)Math.Round(340 * scale);
        var height = (int)Math.Round(510 * scale);
        // Leave the native right-hand Genus stats visible; saved desktop positions stay authoritative.
        var nativeStatsWidth = Math.Clamp((int)Math.Round(workingArea.Width * .22), 340, 560);
        var x = Math.Clamp(workingArea.Right - nativeStatsWidth - width - 24,
            workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - width));
        var y = Math.Clamp(workingArea.Top + 180,
            workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - height));
        return new(settings.GenusInsightOverlayX ?? x, settings.GenusInsightOverlayY ?? y, scale);
    }

    public static OverlayDescriptor Get(string id) => All.FirstOrDefault(item => item.Id == id)
        ?? throw new ArgumentException("Unknown overlay.", nameof(id));

    public static OverlayLayout ReadLayout(AppSettings settings, string id) => id switch
    {
        "loot" => new(settings.OverlayX, settings.OverlayY, settings.OverlayScale),
        "achievements" => AchievementLayout(settings),
        "genus-insight" => GenusInsightLayout(settings),
        "boss" => new(settings.BossOverlayX, settings.BossOverlayY, settings.BossOverlayScale),
        "leveling" => new(settings.LevelingOverlayX, settings.LevelingOverlayY, settings.LevelingOverlayScale),
        "alerts" => new(settings.AlertsOverlayX, settings.AlertsOverlayY, settings.AlertsOverlayScale),
        "notifications" => new(settings.AlertNotificationsX, settings.AlertNotificationsY,
            settings.AlertNotificationsScale, settings.AlertNotificationsWidth),
        "event-timeline" => new(settings.EventTimelineOverlayX, settings.EventTimelineOverlayY,
            settings.EventTimelineOverlayScale, settings.EventTimelineOverlayWidth),
        _ => throw new ArgumentException("This overlay has no freely positioned layout.", nameof(id))
    };

    public static OverlayLayout NormalizeLayout(string id, OverlayLayout layout)
    {
        var descriptor = Get(id);
        if (!descriptor.CanPlace) throw new ArgumentException("This overlay is anchored to the game.", nameof(id));
        var scale = double.IsFinite(layout.Scale) ? Math.Clamp(layout.Scale, descriptor.MinScale, descriptor.MaxScale) : 1;
        var width = descriptor.MinWidth is { } minimum && descriptor.MaxWidth is { } maximum
            ? Math.Clamp(layout.Width ?? (id == "event-timeline" ? 720 : 340), minimum, maximum) : (int?)null;
        return layout with { Scale = Math.Round(scale, 3), Width = width };
    }

    public static void WriteLayout(AppSettings settings, string id, OverlayLayout requested)
    {
        var layout = NormalizeLayout(id, requested);
        switch (id)
        {
            case "loot": (settings.OverlayX, settings.OverlayY, settings.OverlayScale) = (layout.X, layout.Y, layout.Scale); break;
            case "achievements": (settings.AchievementOverlayX, settings.AchievementOverlayY, settings.AchievementOverlayScale) = (layout.X, layout.Y, layout.Scale); break;
            case "genus-insight": (settings.GenusInsightOverlayX, settings.GenusInsightOverlayY, settings.GenusInsightOverlayScale) = (layout.X, layout.Y, layout.Scale); break;
            case "boss": (settings.BossOverlayX, settings.BossOverlayY, settings.BossOverlayScale) = (layout.X, layout.Y, layout.Scale); break;
            case "leveling": (settings.LevelingOverlayX, settings.LevelingOverlayY, settings.LevelingOverlayScale) = (layout.X, layout.Y, layout.Scale); break;
            case "alerts": (settings.AlertsOverlayX, settings.AlertsOverlayY, settings.AlertsOverlayScale) = (layout.X, layout.Y, layout.Scale); break;
            case "notifications":
                (settings.AlertNotificationsX, settings.AlertNotificationsY, settings.AlertNotificationsScale, settings.AlertNotificationsWidth)
                    = (layout.X, layout.Y, layout.Scale, layout.Width!.Value); break;
            case "event-timeline":
                (settings.EventTimelineOverlayX, settings.EventTimelineOverlayY, settings.EventTimelineOverlayScale, settings.EventTimelineOverlayWidth)
                    = (layout.X, layout.Y, layout.Scale, layout.Width!.Value); break;
        }
    }

    public static bool IsEnabled(AppSettings settings, string id) => id switch
    {
        "loot" => settings.OverlayEnabled, "achievements" => settings.AchievementOverlayEnabled, "boss" => settings.BossOverlayVisible,
        "genus-insight" => settings.GenusInsightOverlayEnabled,
        "leveling" => settings.LevelingOverlayEnabled, "alerts" => settings.AlertsOverlayEnabled,
        "event-timeline" => settings.EventTimelineOverlayEnabled, "map" => settings.MapOverlayEnabled,
        "merchant" => settings.MerchantOverlayEnabled, "daevanion" => settings.DaevanionOverlayEnabled, "notifications" => true,
        _ => throw new ArgumentException("Unknown overlay.", nameof(id))
    };

    public static void SetEnabled(AppSettings settings, string id, bool enabled)
    {
        switch (id)
        {
            case "loot": settings.OverlayEnabled = enabled; break;
            case "achievements": settings.AchievementOverlayEnabled = enabled; break;
            case "genus-insight": settings.GenusInsightOverlayEnabled = enabled; break;
            case "boss": settings.BossOverlayVisible = enabled; break;
            case "leveling": settings.LevelingOverlayEnabled = enabled; break;
            case "alerts": settings.AlertsOverlayEnabled = enabled; break;
            case "event-timeline": settings.EventTimelineOverlayEnabled = enabled; break;
            case "map": settings.MapOverlayEnabled = enabled; break;
            case "merchant": settings.MerchantOverlayEnabled = enabled; break;
            case "daevanion": settings.DaevanionOverlayEnabled = enabled; break;
            case "notifications": break; // Delivery remains a per-reminder preference.
            default: throw new ArgumentException("Unknown overlay.", nameof(id));
        }
    }
}
