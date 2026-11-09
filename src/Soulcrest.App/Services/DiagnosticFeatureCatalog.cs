namespace Soulcrest.App.Services;

/// <summary>User request 2026-11-09: every product feature has a selectable, passive diagnosis.</summary>
public static class DiagnosticFeatureCatalog
{
    private static DiagnosticFeature Feature(string id, string group, string label, params string[] dependencies) =>
        new(id, group, label, Array.AsReadOnly(dependencies));

    public static IReadOnlyList<DiagnosticFeature> All { get; } = Array.AsReadOnly(new[]
    {
        Feature("system", "App/Grundlagen", "System und Einstellungen"),
        Feature("settings-storage", "App/Grundlagen", "Einstellungen und Speicherung", "system"),
        Feature("ui", "App/Grundlagen", "Oberfläche, Setup und Sprache", "system"),
        Feature("updates", "App/Grundlagen", "Updates", "system"),
        Feature("network", "App/Grundlagen", "Netzwerk-Tracking", "system"),
        Feature("capture", "App/Grundlagen", "Bildaufnahme", "system"),
        Feature("map", "Karte/Navigation", "Karte und Spieler-Tracking", "capture", "network"),
        Feature("navigation", "Karte/Navigation", "Ziele und Navigation", "map"),
        Feature("routes", "Karte/Navigation", "Routen und Leveling", "navigation"),
        Feature("map-leveling", "Karte/Navigation", "Leveling-Fortschritt", "routes"),
        Feature("pets", "Pets/Erkundung", "Pet-Fortschritt", "network"),
        Feature("exploration", "Pets/Erkundung", "Erkundungsfortschritt", "network"),
        Feature("merchant", "Pets/Erkundung", "Händler-Souls", "network"),
        Feature("boss-rush", "Boss/Alerts", "Boss Rush", "network"),
        Feature("loot-pve", "Loot", "Open-World-PvE", "network"),
        Feature("loot-lifeskill", "Loot", "Lifeskill", "network"),
        Feature("loot-abyss", "Loot", "Abyss, AP und Rangpunkte", "network"),
        Feature("loot-replay", "Loot", "Session-Replay", "system"),
        Feature("loot-market", "Loot", "Exchange-Preisbewertung", "system"),
        Feature("loot-alerts", "Loot", "Fundhinweise und Ton", "system"),
        Feature("loot-sessions", "Loot", "Gemeinsame Loot-Sessions", "network"),
        Feature("loot-killfeed", "Loot", "Abyss-Killfeed", "loot-abyss"),
        Feature("loot-statistics", "Loot", "Statistik und Sessiontimeline", "loot-sessions"),
        Feature("character", "Charakter", "Charakter- und Quellenbindung", "network"),
        Feature("combat-stat-overview", "Charakter", "Stat Overview", "network"),
        Feature("combat-daevanion", "Charakter", "Daevanion Board und Planung", "network"),
        Feature("combat-wings", "Charakter", "Wings", "network"),
        Feature("combat-gear", "Charakter", "Gear", "network"),
        Feature("combat-pets", "Charakter", "Pet-Effekte", "pets"),
        Feature("combat-genus-insight", "Charakter", "Genus Insight", "network"),
        Feature("combat-pantheon", "Charakter", "Pantheon", "network"),
        Feature("combat-achievements", "Charakter", "Achievements", "network"),
        Feature("combat-titles", "Charakter", "Titles", "network"),
        Feature("combat-skills", "Charakter", "Skills", "network"),
        Feature("alerts", "Boss/Alerts", "Alerts und Erinnerungen", "system"),
        Feature("overlay-editor", "Overlays", "Overlay Editor", "system"),
        Feature("overlay-hotkeys", "Overlays", "Overlay-Hotkeys", "system"),
        Feature("overlay-loot", "Overlays", "Pet-Fortschritt-Overlay", "pets"),
        Feature("overlay-achievements", "Overlays", "Achievement-Overlay", "combat-achievements"),
        Feature("overlay-boss", "Overlays", "Boss-Overlay", "boss-rush"),
        Feature("overlay-leveling", "Overlays", "Leveling-Overlay", "routes"),
        Feature("overlay-alerts", "Overlays", "Timer-Leiste", "alerts"),
        Feature("overlay-notifications", "Overlays", "Benachrichtigungen", "alerts"),
        Feature("overlay-event-timeline", "Overlays", "Event-Timeline", "alerts", "boss-rush"),
        Feature("overlay-map", "Overlays", "Karten-Overlay", "map"),
        Feature("overlay-merchant", "Overlays", "Händler-Soulcounter", "network", "capture"),
        Feature("overlay-daevanion", "Overlays", "Daevanion-Overlay", "combat-daevanion", "capture"),
        Feature("widget-drop-grid", "Overlays", "Widget: Drop-Raster", "overlay-editor", "loot-pve"),
        Feature("widget-drop-strip", "Overlays", "Widget: Drop-Leiste", "overlay-editor", "loot-pve"),
        Feature("widget-drop-list", "Overlays", "Widget: Drop-Liste", "overlay-editor", "loot-pve"),
        Feature("widget-duration", "Overlays", "Widget: Sessiondauer", "overlay-editor", "loot-pve"),
        Feature("widget-clock", "Overlays", "Widget: Lokale Uhrzeit", "overlay-editor"),
        Feature("widget-market-kina", "Overlays", "Widget: Market-Kina", "overlay-editor", "loot-market"),
        Feature("widget-dropped-kina", "Overlays", "Widget: Erbeutete Kina", "overlay-editor", "loot-pve"),
        Feature("widget-abyss-points", "Overlays", "Widget: Erhaltene AP", "overlay-editor", "loot-abyss"),
        Feature("widget-rank-net", "Overlays", "Widget: Rangpunkte netto", "overlay-editor", "loot-abyss"),
        Feature("widget-rank-gained", "Overlays", "Widget: Erhaltene Rangpunkte", "overlay-editor", "loot-abyss"),
        Feature("widget-rank-lost", "Overlays", "Widget: Verlorene Rangpunkte", "overlay-editor", "loot-abyss"),
        Feature("widget-daily-kina", "Overlays", "Widget: Tages-Kina", "overlay-editor", "loot-pve"),
        Feature("widget-market-kina-hour", "Overlays", "Widget: Market-Kina pro Stunde", "overlay-editor", "loot-market"),
        Feature("widget-dropped-kina-hour", "Overlays", "Widget: Kina pro Stunde", "overlay-editor", "loot-pve"),
        Feature("widget-abyss-points-hour", "Overlays", "Widget: AP pro Stunde", "overlay-editor", "loot-abyss"),
        Feature("widget-rank-net-hour", "Overlays", "Widget: Rangpunkte netto pro Stunde", "overlay-editor", "loot-abyss"),
        Feature("widget-rank-gained-hour", "Overlays", "Widget: Rangpunkte pro Stunde", "overlay-editor", "loot-abyss"),
        Feature("widget-rank-lost-hour", "Overlays", "Widget: Rangpunktverluste pro Stunde", "overlay-editor", "loot-abyss"),
        Feature("widget-abyss-killfeed", "Overlays", "Widget: Killfeed", "overlay-editor", "loot-abyss"),
        Feature("widget-abyss-rank-progress", "Overlays", "Widget: Abyss-Rangfortschritt", "overlay-editor", "loot-abyss"),
        Feature("widget-genus-insight", "Overlays", "Widget: Gattungsverständnis", "overlay-editor", "combat-genus-insight"),
        Feature("widget-rare-notice", "Overlays", "Widget: Besonderer Fund", "overlay-editor", "loot-alerts"),
        Feature("widget-overlay-lock", "Overlays", "Widget: Overlay-Sperre", "overlay-editor")
    });

    public static IReadOnlyList<DiagnosticFeature> Resolve(IEnumerable<string> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var byId = All.ToDictionary(feature => feature.Id, StringComparer.Ordinal);
        var included = new HashSet<string>(StringComparer.Ordinal);
        void Include(string id)
        {
            if (!byId.TryGetValue(id, out var feature)) throw new ArgumentException("Unknown diagnostic feature.", nameof(selected));
            if (!included.Add(id)) return;
            foreach (var dependency in feature.Dependencies) Include(dependency);
        }
        foreach (var id in selected) Include(id);
        return All.Where(feature => included.Contains(feature.Id)).ToArray();
    }
}
