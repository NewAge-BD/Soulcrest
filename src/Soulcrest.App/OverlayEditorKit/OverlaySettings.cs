namespace Soulcrest.App.OverlayEditorKit;

// Adapted from overlay-editor-kit 1.15.0: Overlay/OverlaySettings.cs.
// The editor owns layout only. Live values come from the independent presentation source.
public sealed record OverlayWidget
{
    [System.Text.Json.Serialization.JsonRequired] public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [System.Text.Json.Serialization.JsonRequired] public string Kind { get; init; } = "preview-metric";
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 168;
    public double Height { get; init; } = 72;
    public double? ContentWidth { get; init; }
    public double? ContentHeight { get; init; }
    public bool ShowLabel { get; init; } = true;
    public bool ShowIcon { get; init; } = true;
    public double FontScale { get; init; } = 1;
    public int ItemLimit { get; init; } = 8;
    public int KillFeedRows { get; init; } = 5;
    public string ItemView { get; init; } = "grid";
    public string ItemFilter { get; init; } = "all";
    public IReadOnlyList<string> ItemKeys { get; init; } = [];
    public double ItemSize { get; init; } = 56;
    public string ItemSort { get; init; } = "default";
}

public sealed record OverlaySettings
{
    public bool Enabled { get; init; }
    public string Interaction { get; init; } = "move";
    public string Visibility { get; init; } = "game";
    public OverlayTriggerRules? TriggerRules { get; init; }
    public double Width { get; init; } = 360;
    public double Height { get; init; } = 260;
    // Kit positions are fractions of the available monitor travel, independent
    // of Soulcrest's existing physical desktop coordinates.
    public double PositionX { get; init; } = .02;
    public double PositionY { get; init; } = .15;
    public string? PositionMonitorId { get; init; }
    public double Scale { get; init; } = 1;
    public double BackgroundOpacity { get; init; } = .85;
    public bool ShowBorder { get; init; } = true;
    public bool SnapToGrid { get; init; } = true;
    public bool AutoAlign { get; init; } = true;
    public bool CaptureExcluded { get; init; } = true;
    [System.Text.Json.Serialization.JsonRequired]
    public IReadOnlyList<OverlayWidget> Widgets { get; init; } = [];
}

public sealed record OverlayWidgetDefinition(string Kind, string Label, string Description, string Icon,
    double Width, double Height);

public static class OverlayCatalog
{
    public const int MaximumWidgets = 24;
    /// <summary>Exact bundled currency bindings shared with the native loot presentation.</summary>
    public static string? GameIconAsset(string? kind) => kind switch
    {
        "market-kina" or "market-kina-hour" or "dropped-kina" or "dropped-kina-hour" or "daily-kina" =>
            Services.MonsterLootIconCatalog.Default.ItemAsset(930100031),
        "abyss-points" or "abyss-points-hour" => Services.MonsterLootIconCatalog.Default.ItemAsset(930100003),
        _ => null,
    };

    public static IReadOnlyList<OverlayWidgetDefinition> Widgets { get; } = Array.AsReadOnly(new[]
    {
        new OverlayWidgetDefinition("drop-grid", "Drop-Raster", "Itemicons und Mengen der laufenden Session", "loot", 344, 168),
        new OverlayWidgetDefinition("drop-strip", "Drop-Leiste", "Kompakte Iconreihe mit Mengen", "loot", 344, 112),
        new OverlayWidgetDefinition("drop-list", "Drop-Liste", "Itemnamen und Mengen untereinander", "loot", 344, 224),
        new OverlayWidgetDefinition("duration", "Sessiondauer", "Gemeinsame Dauer der laufenden Session", "clock", 168, 72),
        new OverlayWidgetDefinition("clock", "Lokale Uhrzeit", "Lokale Systemzeit im 24-Stunden-Format", "clock", 248, 72),
        new OverlayWidgetDefinition("market-kina", "Market-Kina (netto)", "Netto-Marktwert der gesammelten Sessionitems", "silver", 168, 72),
        new OverlayWidgetDefinition("dropped-kina", "Erbeutete Kina", "Tatsächlich erbeutete ungebundene Kina der Session", "silver", 168, 72),
        new OverlayWidgetDefinition("abyss-points", "Erhaltene AP", "Bestätigte AP-Gewinne der laufenden Session", "spark", 168, 72),
        new OverlayWidgetDefinition("rank-net", "Rangpunkte netto", "Rangpunkte-Gewinne minus Verluste der Session", "trend", 168, 72),
        new OverlayWidgetDefinition("rank-gained", "Erhaltene Rangpunkte", "Rangpunkte-Gewinne der laufenden Session", "trend", 168, 72),
        new OverlayWidgetDefinition("rank-lost", "Verlorene Rangpunkte", "Rangpunkte-Verluste der laufenden Session", "trend", 168, 72),
        new OverlayWidgetDefinition("daily-kina", "Tages-Kina", "Bestätigter Tagesstand bis 1.000.000 Kina", "trend", 280, 104),
        new OverlayWidgetDefinition("market-kina-hour", "Market-Kina pro Stunde", "Netto-Marktwert pro gemeinsamer Sessionstunde", "trend", 168, 72),
        new OverlayWidgetDefinition("dropped-kina-hour", "Erbeutete Kina pro Stunde", "Ungebundene Kina pro gemeinsamer Sessionstunde", "trend", 168, 72),
        new OverlayWidgetDefinition("abyss-points-hour", "AP pro Stunde", "Bestätigte AP-Gewinne pro Sessionstunde", "trend", 168, 72),
        new OverlayWidgetDefinition("rank-net-hour", "Netto-Rangpunkte pro Stunde", "Rangpunkte netto pro Sessionstunde", "trend", 168, 72),
        new OverlayWidgetDefinition("rank-gained-hour", "Erhaltene Rangpunkte pro Stunde", "Rangpunkte-Gewinne pro Sessionstunde", "trend", 168, 72),
        new OverlayWidgetDefinition("rank-lost-hour", "Verlorene Rangpunkte pro Stunde", "Rangpunkte-Verluste pro Sessionstunde", "trend", 168, 72),
        new OverlayWidgetDefinition("abyss-killfeed", "Killfeed", "Kills, Tode und Assists mit Gegnerrängen", "shield", 368, 176),
        new OverlayWidgetDefinition("abyss-rank-progress", "Abyss-Rangfortschritt", "Fortschritt der eigenen Rangpunktestufe", "trend", 360, 112),
        new OverlayWidgetDefinition("genus-insight", "Gattungsverständnis", "Neun aktuelle Genus-Slots mit Rollbereichen und Favoriten", "spark", 440, 390),
        new OverlayWidgetDefinition("rare-notice", "Besonderer Fund", "Aktueller seltener Fundhinweis", "spark", 344, 112),
    });

    private static readonly OverlayWidgetDefinition LegacyPreview = new("preview-metric", "Beispielwert", "Neutrales Modul mit Beispieldaten", "trend", 168, 72);
    public static OverlayWidgetDefinition? Find(string kind) => kind == LegacyPreview.Kind ? LegacyPreview : Widgets.FirstOrDefault(value => value.Kind == kind);
    public static bool IsLootWidget(string kind) => kind is "drop-grid" or "drop-strip" or "drop-list";

    public static OverlayWidget CreateWidget(string kind, double x = 8, double y = 8)
    {
        var definition = Find(kind) ?? throw new ArgumentException("Unbekanntes Overlay-Modul.", nameof(kind));
        return new() { Kind = kind, X = x, Y = y, Width = definition.Width, Height = definition.Height,
            ItemView = kind switch { "drop-strip" => "strip", "drop-list" => "list", _ => "grid" } };
    }

    public static IReadOnlyList<OverlayWidget> CompactWidgets() => Array.AsReadOnly(new[] { CreateWidget("preview-metric") });

    public static OverlaySettings Preset(string name) => name switch
    {
        "demo" => new() { Widgets = CompactWidgets() },
        "empty" => new() { Widgets = Array.Empty<OverlayWidget>() },
        _ => throw new ArgumentException("Unbekannte Overlay-Vorlage.", nameof(name)),
    };
}

public static class OverlayLayout
{
    public static OverlaySettings ResizeCanvas(OverlaySettings settings, double width, double height)
    {
        var current = Normalize(settings);
        // The canvas is a viewport. Clipped widgets and their content reference
        // sizes survive shrinking it and become visible when enlarged again.
        return current with { Width = Finite(width, current.Width, 160, 1600), Height = Finite(height, current.Height, 64, 1200) };
    }

    public static OverlayWidget ResizeWidget(OverlayWidget widget, double width, double height) => widget with
    {
        ContentWidth = Reference(widget.ContentWidth) ?? Math.Max(1, widget.Width),
        ContentHeight = Reference(widget.ContentHeight) ?? Math.Max(1, widget.Height),
        Width = double.IsFinite(width) ? Math.Max(1, width) : widget.Width,
        Height = double.IsFinite(height) ? Math.Max(1, height) : widget.Height,
    };

    public static OverlaySettings Normalize(OverlaySettings? settings)
    {
        settings ??= new();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var widgets = new List<OverlayWidget>();
        foreach (var widget in (settings.Widgets ?? []).Take(OverlayCatalog.MaximumWidgets))
        {
            if (widget is null || OverlayCatalog.Find(widget.Kind) is not { } definition) continue;
            var id = Guid.TryParse(widget.Id, out var parsed) ? parsed.ToString("N") : Guid.NewGuid().ToString("N");
            if (!ids.Add(id)) continue;
            var width = Finite(widget.Width, definition.Width, 1, 1600);
            var height = Finite(widget.Height, definition.Height, 1, 1200);
            widgets.Add(widget with
            {
                Id = id, Width = width, Height = height,
                ContentWidth = Reference(widget.ContentWidth), ContentHeight = Reference(widget.ContentHeight),
                X = Finite(widget.X, 0, 0, 1600 - width), Y = Finite(widget.Y, 0, 0, 1200 - height),
                FontScale = Finite(widget.FontScale, 1, .7, 2),
                ItemLimit = Math.Clamp(widget.ItemLimit, 1, 24),
                KillFeedRows = Math.Clamp(widget.KillFeedRows, 1, 10),
                ItemSize = Finite(widget.ItemSize, 56, 32, 112),
                ItemView = widget.ItemView is "strip" or "list" or "card" ? widget.ItemView : "grid",
                ItemFilter = widget.ItemFilter is "rare" or "selected" ? widget.ItemFilter : "all",
                ItemSort = widget.ItemSort is "quantity" or "name" ? widget.ItemSort : "default",
                ItemKeys = NormalizeItemKeys(widget.ItemKeys),
            });
        }
        return settings with
        {
            Width = Finite(settings.Width, 360, 160, 1600), Height = Finite(settings.Height, 260, 64, 1200),
            PositionX = Finite(settings.PositionX, .02, 0, 1), PositionY = Finite(settings.PositionY, .15, 0, 1),
            Scale = Finite(settings.Scale, 1, .5, 2), BackgroundOpacity = Finite(settings.BackgroundOpacity, .85, 0, 1),
            Interaction = settings.Interaction is "move" or "locked" or "passthrough" ? settings.Interaction : "move",
            Visibility = settings.Visibility is "game" or "session" or "always" ? settings.Visibility : "game",
            TriggerRules = OverlayTriggerRules.Normalize(settings.TriggerRules),
            Widgets = Array.AsReadOnly(widgets.ToArray()),
        };
    }

    internal static bool ValidMonitorId(string? id) => id is null ||
        (id.Length is > 0 and <= 256 && !string.IsNullOrWhiteSpace(id) && !id.Any(char.IsControl));

    private static double Finite(double value, double fallback, double minimum, double maximum) =>
        Math.Clamp(double.IsFinite(value) ? value : fallback, minimum, maximum);

    private static double? Reference(double? value) => value is > 0 && double.IsFinite(value.Value)
        ? Math.Clamp(value.Value, 1, 1600) : null;

    private static IReadOnlyList<string> NormalizeItemKeys(IReadOnlyList<string>? values)
    {
        var keys = (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256)
            .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
        if (keys.Length == 0) return Array.Empty<string>();
        if (values is System.Collections.ObjectModel.ReadOnlyCollection<string> existing && existing.SequenceEqual(keys)) return existing;
        return Array.AsReadOnly(keys);
    }
}
