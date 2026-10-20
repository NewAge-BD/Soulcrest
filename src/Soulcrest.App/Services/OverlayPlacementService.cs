using System.Drawing;
using Soulcrest.App.Capture;

namespace Soulcrest.App.Services;

public sealed record OverlayMonitor(string Id, string Label, Rectangle Bounds, Rectangle WorkingArea);

/// <summary>Placement state only. The native host owns the window and previews; no game action is invoked.</summary>
public sealed class OverlayPlacementService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Func<IReadOnlyList<OverlayMonitor>> _screens;
    private readonly Func<Rectangle?> _gameBounds;
    private Rectangle? _ownerBounds;
    private bool _gridVisible = true, _snapToGrid = true, _disposed;
    private string _selectedOverlayId = "loot";

    public OverlayPlacementService(SettingsService settings)
        : this(settings, ReadScreens, MonitorCapture.GameWindowBounds) { }

    internal OverlayPlacementService(SettingsService settings, Func<IReadOnlyList<OverlayMonitor>> screens,
        Func<Rectangle?> gameBounds)
    {
        _settings = settings;
        _screens = screens;
        _gameBounds = gameBounds;
        settings.Changed += SettingsChanged;
    }

    public event Action? Changed;
    public IReadOnlyList<OverlayMonitor> Monitors { get; private set; } = Array.Empty<OverlayMonitor>();
    public OverlayMonitor? Monitor { get; private set; }
    public bool Active { get; private set; }
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
            if (!OverlayRegistry.Get(value).CanPlace) throw new ArgumentException("This overlay is anchored to the game.", nameof(value));
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
                Error = "Die Monitoranordnung hat sich geändert. Place Mode wurde beendet.";
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    public bool Start(string? monitorId = null)
    {
        if (_disposed) return false;
        RefreshMonitors();
        var selected = Monitors.FirstOrDefault(screen => screen.Id == (monitorId ?? PreferredMonitorId));
        if (selected is null)
        {
            Error = "Der gewählte Monitor ist nicht mehr verfügbar.";
            Changed?.Invoke();
            return false;
        }
        Monitor = selected;
        Error = null;
        Active = true;
        Changed?.Invoke();
        return true;
    }

    public void Stop()
    {
        if (!Active) return;
        Active = false;
        Monitor = null;
        Changed?.Invoke();
    }

    internal void Fail(string message)
    {
        Active = false;
        Monitor = null;
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
        _settings.Changed -= SettingsChanged;
        Active = false;
        Monitor = null;
    }
}
