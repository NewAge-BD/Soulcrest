using System.Drawing;
using Soulcrest.App.Capture;
using Soulcrest.App.OverlayEditorKit;

namespace Soulcrest.App.Services;

public sealed record OverlayMonitor(string Id, string Label, Rectangle Bounds, Rectangle WorkingArea);

/// <summary>Placement state only. The native host owns the window and previews; no game action is invoked.</summary>
public sealed class OverlayPlacementService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly IOverlayService? _editor;
    private bool _includeEditor;
    private IReadOnlyList<OverlayInstance>? _observedEditorWindows;
    private string? _observedEditorError;
    private readonly Func<IReadOnlyList<OverlayMonitor>> _screens;
    private readonly Func<Rectangle?> _gameBounds;
    private Rectangle? _ownerBounds;
    private bool _gridVisible = true, _snapToGrid = true, _disposed;
    private string _selectedOverlayId = "loot";

    public OverlayPlacementService(SettingsService settings, IOverlayService? editor = null)
        : this(settings, ReadScreens, MonitorCapture.GameWindowBounds, editor) { }

    internal OverlayPlacementService(SettingsService settings, Func<IReadOnlyList<OverlayMonitor>> screens,
        Func<Rectangle?> gameBounds, IOverlayService? editor = null)
    {
        _settings = settings;
        _editor = editor;
        _screens = screens;
        _gameBounds = gameBounds;
        settings.Changed += SettingsChanged;
        if (_editor is not null) _editor.Changed += EditorChanged;
    }

    public event Action? Changed;
    public IReadOnlyList<OverlayMonitor> Monitors { get; private set; } = Array.Empty<OverlayMonitor>();
    public OverlayMonitor? Monitor { get; private set; }
    public bool Active { get; private set; }
    public bool IsEnding { get; private set; }
    /// <summary>The ephemeral inventory for this placement run; live overlay settings remain independent.</summary>
    public IReadOnlyList<OverlayDescriptor> Overlays { get; private set; } = OverlayRegistry.All;
    internal IEnumerable<OverlayDescriptor> PlaceableOverlays => Overlays.Where(overlay => overlay.CanPlace);
    public IReadOnlyList<OverlayPlacementItem> Items { get; private set; } = Array.Empty<OverlayPlacementItem>();
    public static string EditorKey(string overlayId) => "kit:" + overlayId;
    public static bool IsEditorKey(string id) => id.StartsWith("kit:", StringComparison.Ordinal);
    internal static string EditorId(string key) => IsEditorKey(key) ? key[4..] : throw new ArgumentException("This is not an editor window.", nameof(key));
    private static readonly string[] StaticFamilies = ["loot", "achievements", "genus-insight", "boss", "leveling", "alerts", "notifications", "event-timeline", "map", "merchant", "daevanion"];
    public string? Error { get; private set; }
    public string? PreferredMonitorId { get; private set; }
    public bool GridVisible
    {
        get => _gridVisible;
        set { if (_gridVisible == value) return; _gridVisible = value; Changed?.Invoke(); }
    }
    public bool SnapToGrid
    {
        get => _snapToGrid;
        set { if (_snapToGrid == value) return; _snapToGrid = value; Changed?.Invoke(); }
    }
    public string SelectedOverlayId
    {
        get => _selectedOverlayId;
        set
        {
            if (_selectedOverlayId == value) return;
            if (IsEditorKey(value))
            {
                if (_editor is null || !_editor.Overlays.Any(window => EditorKey(window.Id) == value)) return;
            }
            else if (!OverlayRegistry.Get(value).CanPlace) throw new ArgumentException("This overlay is anchored to the game.", nameof(value));
            if (Active && !Items.Any(overlay => overlay.Id == value && overlay.CanPlace)) return;
            _selectedOverlayId = value;
            Changed?.Invoke();
        }
    }

    public void RefreshMonitors(Rectangle? ownerBounds = null)
    {
        if (_disposed) return;
        if (ownerBounds is not null) _ownerBounds = ownerBounds;
        var screens = _screens().Where(screen => screen.Bounds.Width > 0 && screen.Bounds.Height > 0).ToArray();
        var changed = !Monitors.SequenceEqual(screens);
        Monitors = Array.AsReadOnly(screens);
        var preferred = SelectMonitor(screens, _gameBounds() ?? _ownerBounds)?.Id;
        changed |= PreferredMonitorId != preferred;
        PreferredMonitorId = preferred;
        if (Active && Monitor is { } current)
        {
            var replacement = screens.FirstOrDefault(screen => screen.Id == current.Id);
            if (replacement is null || replacement.Bounds != current.Bounds || replacement.WorkingArea != current.WorkingArea)
            {
                Active = false;
                Monitor = null;
                ResetInventory();
                Error = "Die Monitoranordnung hat sich geändert. Place Mode wurde beendet.";
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    public bool StartEditor(string? monitorId = null, string? overlayId = null) =>
        StartCore(monitorId, StaticFamilies, overlayId);

    public bool Start(string? monitorId = null, IEnumerable<string>? overlayIds = null) => StartCore(monitorId, overlayIds, null);

    private bool StartCore(string? monitorId, IEnumerable<string>? overlayIds, string? selectedEditor)
    {
        if (_disposed) return false;
        if (IsEnding)
        {
            Error = "Place Mode wird beendet. Bitte kurz warten.";
            Changed?.Invoke();
            return false;
        }
        if (_editor?.LoadError is { } error) { Fail(error); return false; }
        if (selectedEditor is not null && (_editor is null || !_editor.Overlays.Any(window => window.Id == selectedEditor)))
        {
            Fail("Das Overlay-Fenster ist nicht mehr vorhanden.");
            return false;
        }
        RefreshMonitors();
        var selected = Monitors.FirstOrDefault(screen => screen.Id == (monitorId ?? PreferredMonitorId));
        if (selected is null)
        {
            Fail("Der gewählte Monitor ist nicht mehr verfügbar.");
            return false;
        }
        Monitor = selected;
        Error = null;
        var included = overlayIds?.ToHashSet(StringComparer.Ordinal);
        Overlays = included is null ? OverlayRegistry.All
            : Array.AsReadOnly(OverlayRegistry.All.Where(overlay => included.Contains(overlay.Id)).ToArray());
        _includeEditor = _editor is not null;
        RebuildItems();
        if (selectedEditor is not null && Items.Any(item => item.Id == EditorKey(selectedEditor)))
            _selectedOverlayId = EditorKey(selectedEditor);
        if (!Items.Any(item => item.Id == _selectedOverlayId && item.CanPlace)
            && Items.FirstOrDefault(item => item.CanPlace) is { } first) _selectedOverlayId = first.Id;
        Active = true;
        Changed?.Invoke();
        return true;
    }

    public void Stop()
    {
        if (!Active) return;
        Active = false;
        Monitor = null;
        ResetInventory();
        Changed?.Invoke();
    }

    internal void SetEnding(bool ending)
    {
        if (_disposed || IsEnding == ending) return;
        IsEnding = ending;
        Changed?.Invoke();
    }

    internal void Fail(string message)
    {
        Active = false;
        Monitor = null;
        ResetInventory();
        Error = message;
        Changed?.Invoke();
    }

    public void UpdateLayout(string id, OverlayLayout requested)
    {
        if (_disposed) return;
        var layout = OverlayRegistry.NormalizeLayout(id, requested);
        if (OverlayRegistry.ReadLayout(_settings.Current, id) == layout) return;
        // One atomic settings update per completed gesture, never per pointer event.
        _settings.Update(settings => OverlayRegistry.WriteLayout(settings, id, layout));
    }

    internal async Task<OverlaySaveResult> SaveEditorPlacementAsync(string key, OverlayLayout layout, Size logicalSize, OverlayMonitor monitor)
    {
        if (_disposed || _editor is null) return new("Das Overlay-Fenster ist nicht mehr vorhanden.");
        var fractions = OverlayEditorPlacementGeometry.Fractions(layout, logicalSize, monitor);
        return await _editor.SavePlacementAsync(EditorId(key), monitor.Id, fractions.X, fractions.Y, layout.Scale).ConfigureAwait(false);
    }

    private void RebuildItems()
    {
        var items = Overlays.Select(overlay => new OverlayPlacementItem(overlay.Id, overlay.Title, Native: overlay));
        if (_includeEditor && _editor is not null)
            items = items.Concat(_editor.Overlays.Select(window => new OverlayPlacementItem(EditorKey(window.Id), window.Name, Editor: window)));
        Items = Array.AsReadOnly(items.ToArray());
        _observedEditorWindows = _editor?.Overlays;
        _observedEditorError = _editor?.LoadError;
    }

    private void ResetInventory()
    {
        Overlays = OverlayRegistry.All;
        Items = Array.Empty<OverlayPlacementItem>();
        _includeEditor = false;
    }

    private void EditorChanged()
    {
        if (_disposed || !Active || !_includeEditor) return;
        if (ReferenceEquals(_observedEditorWindows, _editor?.Overlays) && _observedEditorError == _editor?.LoadError) return;
        if (_editor?.LoadError is { } error) { Fail(error); return; }
        RebuildItems();
        if (!Items.Any(item => item.Id == _selectedOverlayId && item.CanPlace)
            && Items.FirstOrDefault(item => item.CanPlace) is { } first) _selectedOverlayId = first.Id;
        Changed?.Invoke();
    }

    internal static OverlayMonitor? SelectMonitor(IReadOnlyList<OverlayMonitor> screens, Rectangle? target)
    {
        if (target is not { } bounds) return screens.FirstOrDefault();
        return screens.OrderByDescending(screen =>
        {
            var intersection = Rectangle.Intersect(screen.Bounds, bounds);
            return (long)Math.Max(0, intersection.Width) * Math.Max(0, intersection.Height);
        }).FirstOrDefault();
    }

    private static IReadOnlyList<OverlayMonitor> ReadScreens() => Screen.AllScreens
        .OrderByDescending(screen => screen.Primary).ThenBy(screen => screen.Bounds.X).ThenBy(screen => screen.Bounds.Y)
        .Select((screen, index) => new OverlayMonitor(screen.DeviceName,
            UiText.F("Monitor {0} · {1} × {2}", index + 1, screen.Bounds.Width, screen.Bounds.Height), screen.Bounds, screen.WorkingArea)).ToArray();

    private void SettingsChanged() { if (!_disposed) Changed?.Invoke(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsEnding = false;
        _settings.Changed -= SettingsChanged;
        if (_editor is not null) _editor.Changed -= EditorChanged;
        Active = false;
        Monitor = null;
        ResetInventory();
    }
}
