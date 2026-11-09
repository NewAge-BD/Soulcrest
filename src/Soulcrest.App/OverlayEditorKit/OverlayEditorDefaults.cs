namespace Soulcrest.App.OverlayEditorKit;

public sealed record OverlayDefaultPreset(string Id, string Label);

/// <summary>Portable layout definitions approved from the editor setup; no personal IDs, placement or live data.</summary>
public static class OverlayEditorDefaults
{
    public const string OpenWorld = "open-world";
    public const string Abyss = "abyss";

    public static IReadOnlyList<OverlayDefaultPreset> All { get; } = Array.AsReadOnly(new[]
    {
        new OverlayDefaultPreset(OpenWorld, "Open World"),
        new OverlayDefaultPreset(Abyss, "Abyss"),
    });

    /// <summary>Each invocation creates independent widget identities and collections, never reading user files.</summary>
    public static OverlaySettings CreateSettings(string id) => id switch
    {
        OpenWorld => OpenWorldSettings(),
        Abyss => AbyssSettings(),
        _ => throw new ArgumentException("Unbekannte Overlay-Vorlage.", nameof(id)),
    };

    public static OverlayCollectionSettings CreateInitialCollection() => CreateCollection([OpenWorld, Abyss], Abyss);

    public static OverlayCollectionSettings CreateCollection(IEnumerable<string> presetIds, string? selectedPresetId = null)
    {
        ArgumentNullException.ThrowIfNull(presetIds);
        var ids = presetIds.ToArray();
        if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            ids.Any(id => !All.Any(preset => preset.Id == id)))
            throw new ArgumentException("Die Standard-Overlays sind ungültig.", nameof(presetIds));
        selectedPresetId ??= ids[0];
        if (!ids.Contains(selectedPresetId, StringComparer.Ordinal))
            throw new ArgumentException("Das ausgewählte Standard-Overlay fehlt.", nameof(selectedPresetId));
        var windows = ids.Select((id, index) => new OverlayInstance
        {
            Id = index == 0 ? OverlayInstance.DefaultId : "builtin-" + id,
            Name = All.First(preset => preset.Id == id).Label,
            Settings = CreateSettings(id),
        }).ToArray();
        return new()
        {
            SelectedOverlayId = windows[Array.IndexOf(ids, selectedPresetId)].Id,
            Overlays = Array.AsReadOnly(windows),
        };
    }

    private static OverlaySettings OpenWorldSettings() => new()
    {
        Width = 336, Height = 616,
        TriggerRules = Rules(OverlayTriggerArea.OpenWorld) with { MinimapMissing = OverlayTriggerRequirement.Excluded },
        Widgets = Array.AsReadOnly(new[]
        {
            Widget("duration", 168, 176, 168, 48, 168, 72, font: 1.5),
            Widget("market-kina", 0, 120, 168, 64, 168, 72, font: 1.3),
            Widget("clock", 0, 176, 168, 48, 248, 72, font: 1.65),
            Widget("dropped-kina", 0, 72, 168, 64, 168, 72, font: 1.3),
            Widget("daily-kina", 0, 0, 336, 104, 280, 104, font: 1.45),
            Widget("dropped-kina-hour", 168, 72, 168, 64, 168, 72, font: 1.3),
            Widget("market-kina-hour", 168, 120, 168, 64, 168, 72, font: 1.3),
            Widget("drop-list", 0, 216, 336, 400, 344, 224, itemView: "list"),
        }),
    };

    private static OverlaySettings AbyssSettings() => new()
    {
        Width = 360, Height = 400, Scale = .967741935483871,
        TriggerRules = Rules(OverlayTriggerArea.Abyss),
        // Preserve the configured viewport, including eight pixels clipped at the right and bottom.
        Widgets = Array.AsReadOnly(new[]
        {
            Widget("dropped-kina", 0, 184, 184, 72, 168, 72, font: 1.2),
            Widget("dropped-kina-hour", 184, 184, 184, 72, 168, 72, font: 1.2),
            Widget("abyss-points-hour", 184, 136, 184, 72, 168, 72, font: 1.2),
            Widget("rank-net-hour", 184, 80, 184, 72, 168, 72, label: true),
            Widget("abyss-points", 0, 136, 184, 72, 168, 72, font: 1.2),
            Widget("rank-net", 0, 80, 184, 72, 168, 72, label: true),
            Widget("abyss-killfeed", 0, 232, 368, 176, null, null, label: true),
            Widget("abyss-rank-progress", 0, 0, 368, 112, 360, 112),
        }),
    };

    private static OverlayWidget Widget(string kind, double x, double y, double width, double height,
        double? contentWidth, double? contentHeight, bool label = false, double font = 1, string itemView = "grid") => new()
    {
        Kind = kind, X = x, Y = y, Width = width, Height = height,
        ContentWidth = contentWidth, ContentHeight = contentHeight,
        ShowLabel = label, FontScale = font, ItemView = itemView,
    };

    private static OverlayTriggerRules Rules(OverlayTriggerArea area) => new()
    {
        Areas = Array.AsReadOnly(new[] { area }), Foreground = OverlayTriggerRequirement.Required,
        SessionActive = OverlayTriggerRequirement.Required, WorldMap = OverlayTriggerRequirement.Excluded,
        EscMenu = OverlayTriggerRequirement.Excluded,
    };
}
