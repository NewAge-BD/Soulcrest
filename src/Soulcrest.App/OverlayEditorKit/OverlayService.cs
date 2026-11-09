using System.Collections.Concurrent;
using Soulcrest.App.Services;

namespace Soulcrest.App.OverlayEditorKit;

/// <summary>The original kit's layout contract with independent storage; presentation has its own source.</summary>
public sealed class OverlayService : IOverlayService
{
    // All operations for one directory are serialized, including operations from
    // another service instance. A queued edit reads the latest committed file.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SaveGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _saveGate;
    private readonly OverlaySettingsStore _store;
    private readonly OverlayTemplateStore _templateStore;
    private readonly OverlayCollectionSettings _missingDefaults;
    private readonly HashSet<string> _previewing = new(StringComparer.Ordinal);
    private readonly object _previewGate = new();
    private readonly ConcurrentDictionary<string, (bool Visible, string? Error)> _runtime = new(StringComparer.Ordinal);
    private OverlayCollectionSettings _collection = new();
    private IReadOnlyList<OverlayTemplate> _templates = Array.Empty<OverlayTemplate>();
    private bool _disposed;

    public OverlayService(string? directory = null) : this(directory, OverlayEditorDefaults.CreateInitialCollection()) { }

    internal OverlayService(string? directory, OverlayCollectionSettings missingDefaults)
    {
        ArgumentNullException.ThrowIfNull(missingDefaults);
        _missingDefaults = missingDefaults;
        var path = Path.GetFullPath(directory ?? Path.Combine(AppPaths.DataDirectory, "overlay-editor"));
        _saveGate = SaveGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        _store = new(path);
        _templateStore = new(path);
        // Loading is read-only. No directory creation, compatibility import or
        // synthetic snapshot is allowed to write an editor or existing file.
        _saveGate.Wait();
        try
        {
            try { _collection = _store.Load(_missingDefaults); }
            catch (Exception error) when (OverlayKitJson.IsStorageError(error)) { LoadError = LoadFailure(error); }
            try { _templates = _templateStore.Load(); }
            catch (Exception error) when (OverlayKitJson.IsStorageError(error)) { TemplateError = TemplateFailure(error); }
        }
        finally { _saveGate.Release(); }
    }

    public event Action? Changed;
    public OverlaySettings Settings
    {
        get { var current = _collection; return current.Overlays.First(overlay => overlay.Id == current.SelectedOverlayId).Settings; }
    }
    public IReadOnlyList<OverlayInstance> Overlays => _collection.Overlays;
    public string SelectedOverlayId => _collection.SelectedOverlayId;
    public IReadOnlyList<OverlayTemplate> Templates => _templates;
    public string? LoadError { get; private set; }
    public string? TemplateError { get; private set; }
    public OverlayRuntimeState State => GetState(SelectedOverlayId);

    public OverlayRuntimeState GetState(string id)
    {
        bool previewing;
        lock (_previewGate) previewing = !_disposed && _previewing.Contains(id);
        var hasRuntime = _runtime.TryGetValue(id, out var runtime);
        return new()
        {
            Previewing = previewing, IsVisible = previewing || runtime.Visible, IsError = LoadError is not null || runtime.Error is not null,
            Status = LoadError ?? runtime.Error ?? (previewing ? "Editor-Vorschau geöffnet." :
                hasRuntime ? runtime.Visible ? "Overlay wird angezeigt." : "Overlay ist ausgeblendet." : "Editor-Vorschau bereit."),
        };
    }

    public void SetRuntimeState(string id, bool visible, string? error = null)
    {
        if (_disposed || !Overlays.Any(window => window.Id == id)) return;
        var state = (visible && error is null, error);
        if (_runtime.TryGetValue(id, out var previous) && previous == state) return;
        _runtime[id] = state;
        Changed?.Invoke();
    }

    public async Task<OverlaySaveResult> ReloadAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return Failure("Der Overlay-Editor wurde bereits beendet.");
            try { _collection = await Task.Run(() => _store.Load(_missingDefaults)).ConfigureAwait(false); LoadError = null; }
            catch (Exception error) when (OverlayKitJson.IsStorageError(error)) { LoadError = LoadFailure(error); }
            try { _templates = await Task.Run(_templateStore.Load).ConfigureAwait(false); TemplateError = null; }
            catch (Exception error) when (OverlayKitJson.IsStorageError(error)) { TemplateError = TemplateFailure(error); }
            PrunePreviews();
            return new(LoadError ?? TemplateError);
        }
        finally { _saveGate.Release(); Changed?.Invoke(); }
    }

    public Task<OverlaySaveResult> SaveAsync(OverlaySettings settings) => SaveAsync(SelectedOverlayId, settings);

    public Task<OverlaySaveResult> SaveAsync(string id, OverlaySettings settings)
    {
        var original = Overlays.FirstOrDefault(overlay => overlay.Id == id)?.Settings;
        return ChangeCollection(collection =>
        {
            var current = Find(collection, id).Settings;
            // Screen moves and other behavior changes may happen while a layout
            // save is queued. Preserve them unless this edit changed that field.
            var normalized = OverlayLayout.Normalize(settings with
            {
                PositionX = current.PositionX, PositionY = current.PositionY, PositionMonitorId = current.PositionMonitorId,
                Scale = original is not null && settings.Scale == original.Scale ? current.Scale : settings.Scale,
                Enabled = original is not null && settings.Enabled == original.Enabled ? current.Enabled : settings.Enabled,
                Interaction = original is not null && settings.Interaction == original.Interaction ? current.Interaction : settings.Interaction,
                Visibility = original is not null && settings.Visibility == original.Visibility ? current.Visibility : settings.Visibility,
                TriggerRules = original is not null && OverlayTriggerRules.Equivalent(settings.TriggerRules, original.TriggerRules) ? current.TriggerRules : settings.TriggerRules,
            });
            return ReplaceSettings(collection, id, normalized);
        });
    }

    public Task<OverlaySaveResult> SavePositionAsync(double x, double y, double? width = null, double? height = null) =>
        SavePositionAsync(SelectedOverlayId, x, y, width, height);

    public Task<OverlaySaveResult> SavePositionAsync(string id, double x, double y, double? width = null, double? height = null) =>
        ChangeCollection(collection =>
        {
            var current = Find(collection, id).Settings;
            return ReplaceSettings(collection, id, OverlayLayout.Normalize(current with
            {
                PositionX = x, PositionY = y, Width = width ?? current.Width, Height = height ?? current.Height,
            }));
        });

    /// <summary>Commits only one completed screen gesture against the latest window, preserving its contents and behavior.</summary>
    public Task<OverlaySaveResult> SavePlacementAsync(string id, string? monitorId, double x, double y, double scale)
    {
        if (!OverlayLayout.ValidMonitorId(monitorId) || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(scale))
            return Task.FromResult(Failure("Die Overlay-Position ist ungültig."));
        return ChangeCollection(collection =>
        {
            var current = Find(collection, id).Settings;
            return ReplaceSettings(collection, id, OverlayLayout.Normalize(current with
            {
                PositionMonitorId = monitorId, PositionX = x, PositionY = y, Scale = scale,
            }));
        });
    }

    public async Task ResetPositionAsync() { _ = await SavePositionAsync(.02, .15).ConfigureAwait(false); }

    public Task<OverlaySaveResult> SelectOverlayAsync(string id) => ChangeCollection(collection =>
    {
        _ = Find(collection, id);
        return collection with { SelectedOverlayId = id };
    });

    public Task<OverlaySaveResult> CreateOverlayAsync(string name, string? duplicateId = null) => ChangeCollection(collection =>
    {
        var normalizedName = ValidateWindowName(collection, name);
        var source = duplicateId is null ? new OverlaySettings { Widgets = [] } : Find(collection, duplicateId).Settings;
        var created = new OverlayInstance
        {
            Id = Guid.NewGuid().ToString("N"), Name = normalizedName,
            Settings = OverlayLayout.Normalize(source with
            {
                Enabled = false,
                Widgets = source.Widgets.Select(widget => widget with { Id = Guid.NewGuid().ToString("N") }).ToArray(),
            }),
        };
        return collection with
        {
            SelectedOverlayId = created.Id, Overlays = Array.AsReadOnly(collection.Overlays.Append(created).ToArray()),
        };
    });

    public Task<OverlaySaveResult> RenameOverlayAsync(string id, string name) => ChangeCollection(collection =>
    {
        _ = Find(collection, id);
        var normalizedName = ValidateWindowName(collection, name, id);
        return collection with { Overlays = Array.AsReadOnly(collection.Overlays.Select(overlay =>
            overlay.Id == id ? overlay with { Name = normalizedName } : overlay).ToArray()) };
    });

    public Task<OverlaySaveResult> DeleteOverlayAsync(string id) => ChangeCollection(collection =>
    {
        _ = Find(collection, id);
        if (collection.Overlays.Count <= 1) throw new ArgumentException("Mindestens ein Overlay muss erhalten bleiben.");
        var next = collection.Overlays.Where(overlay => overlay.Id != id).ToArray();
        return collection with
        {
            Overlays = Array.AsReadOnly(next), SelectedOverlayId = collection.SelectedOverlayId == id ? next[0].Id : collection.SelectedOverlayId,
        };
    });

    public Task<OverlaySaveResult> SaveTemplateAsync(string name, OverlaySettings layout, string? replaceId = null) => ChangeTemplates(templates =>
    {
        var normalizedName = OverlayTemplate.NormalizeName(name);
        if (replaceId is not null && !templates.Any(template => template.Id == replaceId))
            throw new ArgumentException("Die Vorlage wurde nicht gefunden. Bitte erneut auswählen.");
        if (templates.Any(template => template.Id != replaceId && string.Equals(template.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Eine Vorlage mit diesem Namen existiert bereits. Bitte einen anderen Namen wählen oder die vorhandene Vorlage ersetzen.");
        if (replaceId is null && templates.Count >= OverlayTemplateStore.MaximumTemplates)
            throw new ArgumentException("Es sind höchstens 30 eigene Vorlagen möglich.");
        var saved = OverlayTemplate.Create(normalizedName, layout, replaceId);
        return Array.AsReadOnly(replaceId is null ? templates.Append(saved).ToArray() :
            templates.Select(template => template.Id == replaceId ? saved : template).ToArray());
    });

    public Task<OverlaySaveResult> DeleteTemplateAsync(string id) => ChangeTemplates(templates =>
    {
        if (!templates.Any(template => template.Id == id)) throw new ArgumentException("Die Vorlage wurde nicht gefunden. Bitte erneut auswählen.");
        return Array.AsReadOnly(templates.Where(template => template.Id != id).ToArray());
    });

    public Task SetPreviewAsync(bool enabled) => SetPreviewAsync(SelectedOverlayId, enabled);
    public Task SetPreviewAsync(string id, bool enabled)
    {
        if (_disposed || !Overlays.Any(overlay => overlay.Id == id)) return Task.CompletedTask;
        bool changed;
        lock (_previewGate) changed = enabled ? _previewing.Add(id) : _previewing.Remove(id);
        if (changed) Changed?.Invoke();
        return Task.CompletedTask;
    }

    private async Task<OverlaySaveResult> ChangeCollection(Func<OverlayCollectionSettings, OverlayCollectionSettings> change)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return Failure("Der Overlay-Editor wurde bereits beendet.");
            if (LoadError is not null) return Failure(LoadError);
            OverlayCollectionSettings current;
            try { current = await Task.Run(() => _store.Load(_collection)).ConfigureAwait(false); }
            catch (Exception error) when (OverlayKitJson.IsStorageError(error)) { return Failure(LoadError = LoadFailure(error)); }
            OverlayCollectionSettings next;
            try { next = change(current); }
            catch (ArgumentException error) { return Failure(error.Message); }
            try { await Task.Run(() => _store.Save(next)).ConfigureAwait(false); }
            catch (Exception error) when (OverlayKitJson.IsStorageError(error))
            {
                return Failure(LoadError = "Overlay nicht gespeichert. Bitte erneut laden. " + error.Message);
            }
            _collection = next;
            PrunePreviews();
            return new();
        }
        finally { _saveGate.Release(); Changed?.Invoke(); }
    }

    private async Task<OverlaySaveResult> ChangeTemplates(Func<IReadOnlyList<OverlayTemplate>, IReadOnlyList<OverlayTemplate>> change)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return Failure("Der Overlay-Editor wurde bereits beendet.");
            if (TemplateError is not null) return Failure(TemplateError);
            IReadOnlyList<OverlayTemplate> current;
            try { current = await Task.Run(_templateStore.Load).ConfigureAwait(false); }
            catch (Exception error) when (OverlayKitJson.IsStorageError(error)) { return Failure(TemplateError = TemplateFailure(error)); }
            IReadOnlyList<OverlayTemplate> next;
            try { next = change(current); }
            catch (ArgumentException error) { return Failure(error.Message); }
            try { await Task.Run(() => _templateStore.Save(next)).ConfigureAwait(false); }
            catch (Exception error) when (OverlayKitJson.IsStorageError(error))
            {
                return Failure(TemplateError = "Vorlage nicht gespeichert. Bitte erneut laden. " + error.Message);
            }
            _templates = next;
            return new();
        }
        finally { _saveGate.Release(); Changed?.Invoke(); }
    }

    private static OverlayInstance Find(OverlayCollectionSettings collection, string id) => collection.Overlays.FirstOrDefault(overlay => overlay.Id == id)
        ?? throw new ArgumentException("Das Overlay wurde nicht gefunden.");

    private static string ValidateWindowName(OverlayCollectionSettings collection, string name, string? excludedId = null)
    {
        var normalized = OverlayInstance.NormalizeName(name);
        if (collection.Overlays.Any(overlay => overlay.Id != excludedId && string.Equals(overlay.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Ein Overlay mit diesem Namen existiert bereits.");
        return normalized;
    }

    private static OverlayCollectionSettings ReplaceSettings(OverlayCollectionSettings collection, string id, OverlaySettings settings) =>
        collection with { Overlays = Array.AsReadOnly(collection.Overlays.Select(overlay => overlay.Id == id ? overlay with { Settings = settings } : overlay).ToArray()) };

    private void PrunePreviews()
    {
        var retained = Overlays.Select(overlay => overlay.Id).ToHashSet(StringComparer.Ordinal);
        lock (_previewGate) _previewing.IntersectWith(retained);
        foreach (var id in _runtime.Keys.Where(id => !retained.Contains(id))) _runtime.TryRemove(id, out _);
    }

    private static string LoadFailure(Exception error) => "Overlays konnten nicht geladen werden. Die gespeicherte Datei bleibt unverändert. " + error.Message;
    private static string TemplateFailure(Exception error) => "Eigene Vorlagen konnten nicht geladen werden. Die Datei bleibt unverändert. " + error.Message;
    private static OverlaySaveResult Failure(string message) => new(message);

    public void Dispose()
    {
        _disposed = true;
        lock (_previewGate) _previewing.Clear();
        _runtime.Clear();
    }
}
