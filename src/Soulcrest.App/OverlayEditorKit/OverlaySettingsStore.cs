using System.Text.Json;

namespace Soulcrest.App.OverlayEditorKit;

// Adapted from overlay-editor-kit 1.15.0: OverlaySettingsStore/OverlayTemplateStore.
// Reads remain side-effect free; explicit layout migrations are coordinated by OverlayService.
internal sealed class OverlaySettingsStore(string directory)
{
    internal string FilePath { get; } = Path.Combine(directory, "overlay.json");

    internal OverlayCollectionSettings Load(OverlayCollectionSettings? missing = null)
    {
        var collection = OverlayKitJson.Read(FilePath, () => missing ?? new OverlayCollectionSettings());
        if (collection.Version != 2 || collection.AbyssBoundKinaMigrationVersion < 0 || collection.Overlays is null || collection.Overlays.Count == 0)
            throw new InvalidDataException("Das Format der Overlay-Datei wird nicht unterstützt.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var overlay in collection.Overlays)
        {
            if (overlay is null || string.IsNullOrWhiteSpace(overlay.Id) || overlay.Id.Length > 100 ||
                overlay.Id.Any(char.IsControl) || !ids.Add(overlay.Id) || overlay.Settings is null)
                throw new InvalidDataException("Die Overlay-Datei enthält ungültige oder doppelte Fenster.");
            if (!names.Add(OverlayInstance.NormalizeName(overlay.Name)))
                throw new InvalidDataException("Die Overlay-Datei enthält doppelte Namen.");
            ValidateWidgets(overlay.Settings);
        }
        if (!ids.Contains(collection.SelectedOverlayId))
            throw new InvalidDataException("Das ausgewählte Overlay fehlt in der gespeicherten Datei.");
        return collection with
        {
            Overlays = Array.AsReadOnly(collection.Overlays.Select(overlay => overlay with
            {
                Name = OverlayInstance.NormalizeName(overlay.Name), Settings = OverlayLayout.Normalize(overlay.Settings),
            }).ToArray()),
        };
    }

    internal void Save(OverlayCollectionSettings collection)
    {
        foreach (var overlay in collection.Overlays) ValidateWidgets(overlay.Settings);
        OverlayKitJson.Write(FilePath, collection);
    }

    internal static void ValidateWidgets(OverlaySettings settings)
    {
        OverlayTriggerRules.ValidateStored(settings.TriggerRules);
        if (!OverlayLayout.ValidMonitorId(settings.PositionMonitorId))
            throw new InvalidDataException("Die gespeicherte Monitorkennung ist ungültig.");
        if (settings.Widgets is null || settings.Widgets.Count > OverlayCatalog.MaximumWidgets)
            throw new InvalidDataException("Die gespeicherte Modulliste ist ungültig.");
        var ids = new HashSet<Guid>();
        foreach (var widget in settings.Widgets)
        {
            if (widget is null || widget.Kind == OverlayLockWidget.Kind || !Guid.TryParse(widget.Id, out var id) || !ids.Add(id) || OverlayCatalog.Find(widget.Kind) is null)
                throw new InvalidDataException("Die gespeicherte Modulliste enthält ein unbekanntes oder ungültiges Modul.");
            if (widget.ItemView is not ("grid" or "strip" or "list" or "card") ||
                widget.ItemFilter is not ("all" or "rare" or "selected") ||
                widget.ItemSort is not ("default" or "quantity" or "name") ||
                widget.KillFeedRows is < 1 or > 10 || widget.ItemLimit is < 1 or > 24 || !double.IsFinite(widget.ItemSize) || widget.ItemSize is < 32 or > 112 ||
                widget.ItemKeys is { Count: > 24 })
                throw new InvalidDataException("Die gespeicherten Moduloptionen sind ungültig.");
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in widget.ItemKeys ?? [])
                if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(char.IsControl) || !selected.Add(key.Trim()))
                    throw new InvalidDataException("Die gespeicherte Itemauswahl ist ungültig.");
        }
    }
}

internal sealed class OverlayTemplateStore(string directory)
{
    internal const int MaximumTemplates = 30;
    internal string FilePath { get; } = Path.Combine(directory, "overlay-templates.json");

    internal IReadOnlyList<OverlayTemplate> Load()
    {
        var document = OverlayKitJson.Read(FilePath, () => new StoredDocument { Version = 2, Templates = [] });
        if (document.Version != 2 || document.Templates is null || document.Templates.Length > MaximumTemplates)
            throw new InvalidDataException("Das Format der Vorlagendatei wird nicht unterstützt.");
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templates = new List<OverlayTemplate>();
        foreach (var template in document.Templates)
        {
            if (template is null || template.Layout is null || !Guid.TryParse(template.Id, out var id) || !ids.Add(id) ||
                !names.Add(OverlayTemplate.NormalizeName(template.Name)))
                throw new InvalidDataException("Die Vorlagendatei enthält ungültige oder doppelte Vorlagen.");
            var settings = template.Layout.ToSettings();
            OverlaySettingsStore.ValidateWidgets(settings);
            templates.Add(OverlayTemplate.Create(template.Name, settings, id.ToString("N")));
        }
        return Array.AsReadOnly(templates.ToArray());
    }

    internal void Save(IReadOnlyList<OverlayTemplate> templates)
    {
        if (templates.Count > MaximumTemplates) throw new InvalidDataException("Es sind höchstens 30 eigene Vorlagen möglich.");
        foreach (var template in templates) OverlaySettingsStore.ValidateWidgets(template.Layout);
        OverlayKitJson.Write(FilePath, new StoredDocument
        {
            Version = 2,
            Templates = templates.Select(template => new StoredTemplate
            {
                Id = template.Id, Name = template.Name, Layout = StoredLayout.FromSettings(template.Layout),
            }).ToArray(),
        });
    }

    private sealed record StoredDocument
    {
        public required int Version { get; init; }
        public required StoredTemplate[] Templates { get; init; }
    }

    private sealed record StoredTemplate
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required StoredLayout Layout { get; init; }
    }

    // Behavior and position are deliberately excluded from template documents.
    private sealed record StoredLayout
    {
        public required double Width { get; init; }
        public required double Height { get; init; }
        public required IReadOnlyList<OverlayWidget> Widgets { get; init; }
        public required double Scale { get; init; }
        public required double BackgroundOpacity { get; init; }
        public required bool ShowBorder { get; init; }
        public required bool SnapToGrid { get; init; }
        public bool AutoAlign { get; init; } = true;
        public OverlayLockWidgetPosition? LockWidget { get; init; }

        public OverlaySettings ToSettings() => new()
        {
            Width = Width, Height = Height, Widgets = Widgets, Scale = Scale,
            BackgroundOpacity = BackgroundOpacity, ShowBorder = ShowBorder, SnapToGrid = SnapToGrid, AutoAlign = AutoAlign,
            LockWidget = LockWidget,
        };

        public static StoredLayout FromSettings(OverlaySettings source)
        {
            var layout = OverlayTemplate.CopyLayout(source);
            return new()
            {
                Width = layout.Width, Height = layout.Height, Widgets = layout.Widgets, Scale = layout.Scale,
                BackgroundOpacity = layout.BackgroundOpacity, ShowBorder = layout.ShowBorder,
                SnapToGrid = layout.SnapToGrid, AutoAlign = layout.AutoAlign,
                LockWidget = layout.LockWidget,
            };
        }
    }
}

internal static class OverlayKitJson
{
    internal const int MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    internal static T Read<T>(string path, Func<T> missing)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Die Editor-Datei ist zu groß.");
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Das Format der Editor-Datei wird nicht unterstützt.");
            var version = document.RootElement.EnumerateObject().FirstOrDefault(property =>
                property.Name.Equals("Version", StringComparison.OrdinalIgnoreCase)).Value;
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 2)
                throw new InvalidDataException("Diese Version der Editor-Datei wird nicht unterstützt.");
            return document.RootElement.Deserialize<T>(Options) ?? throw new InvalidDataException("Die Editor-Datei ist leer.");
        }
        catch (FileNotFoundException) { return missing(); }
        catch (DirectoryNotFoundException) { return missing(); }
    }

    internal static void Write<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Die Editor-Konfiguration ist zu groß zum Speichern.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
}
