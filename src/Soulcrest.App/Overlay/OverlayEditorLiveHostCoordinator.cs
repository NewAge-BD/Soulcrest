using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>UI-thread owner of read-only live windows. It never starts tracking, writes facts or plays audio.</summary>
public sealed class OverlayEditorLiveHostCoordinator : IDisposable
{
    private readonly Control _owner;
    private readonly IOverlayService _layouts;
    private readonly IOverlayEditorPresentationSource _presentation;
    private readonly IOverlayEditorLiveRenderer _renderer;
    private readonly Func<OverlayInstance, OverlaySnapshot, bool> _isVisible;
    private readonly Func<IReadOnlyList<OverlayMonitor>> _monitors;
    private readonly Func<OverlayMonitor?> _preferred;
    private readonly Func<bool> _recordings;
    private readonly Func<IOverlayEditorLiveSurface> _surface;
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private IReadOnlyList<OverlayInstance>? _observedLayouts;
    private IReadOnlyList<OverlayMonitor> _observedMonitors = [];
    private string? _observedLoadError;
    private OverlaySnapshot? _snapshot;
    private CancellationTokenSource? _renderCancellation;
    private IDisposable? _lease;
    private bool? _leasedMarket;
    private bool _started, _paused, _disposed, _rendering, _requested, _acquiringLease;
    private long _epoch;
    private long _appliedEpoch;

    private sealed class Slot(IOverlayEditorLiveSurface window, IOverlayEditorLiveRenderSession session, OverlaySettings settings)
    {
        internal IOverlayEditorLiveSurface Window { get; } = window;
        internal IOverlayEditorLiveRenderSession Session { get; } = session;
        internal bool Failed { get; set; }
        internal OverlaySettings Settings { get; set; } = settings;
    }

    public OverlayEditorLiveHostCoordinator(Control owner, IOverlayService layouts,
        IOverlayEditorPresentationSource presentation, IOverlayEditorLiveRenderer renderer,
        Func<OverlayInstance, OverlaySnapshot, bool> isVisible,
        Func<IReadOnlyList<OverlayMonitor>>? monitors = null, Func<OverlayMonitor?>? preferredMonitor = null,
        Func<bool>? visibleInRecordings = null)
        : this(owner, layouts, presentation, renderer, isVisible, monitors, preferredMonitor, visibleInRecordings,
            () => new OverlayEditorLiveWindow()) { }

    internal OverlayEditorLiveHostCoordinator(Control owner, IOverlayService layouts,
        IOverlayEditorPresentationSource presentation, IOverlayEditorLiveRenderer renderer,
        Func<OverlayInstance, OverlaySnapshot, bool> isVisible, Func<IReadOnlyList<OverlayMonitor>>? monitors,
        Func<OverlayMonitor?>? preferredMonitor, Func<bool>? visibleInRecordings, Func<IOverlayEditorLiveSurface> surface)
    {
        _surface = surface;
        _owner = owner; _layouts = layouts; _presentation = presentation; _renderer = renderer; _isVisible = isVisible;
        _monitors = monitors ?? (() => Screen.AllScreens.Select(screen => new OverlayMonitor(screen.DeviceName,
            screen.DeviceName, screen.Bounds, screen.WorkingArea)).ToArray());
        _preferred = preferredMonitor ?? (() => _monitors().FirstOrDefault());
        _recordings = visibleInRecordings ?? (() => false);
        _timer.Tick += Tick;
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;
        _layouts.Changed += LayoutsChanged;
        _presentation.Changed += Invalidate;
        _owner.HandleDestroyed += OwnerHandleDestroyed;
        _owner.HandleCreated += OwnerHandleCreated;
        _timer.Start();
        Invalidate();
    }

    public void SetPlacementActive(bool active)
    {
        if (Marshal(() => SetPlacementActive(active))) return;
        if (_disposed || _paused == active) return;
        _paused = active;
        Invalidate();
    }

    public void Invalidate()
    {
        // Revoke pending results on the publishing thread, before the UI callback can run.
        var epoch = Interlocked.Increment(ref _epoch);
        if (Marshal(() => InvalidateCore(epoch))) return;
        InvalidateCore(epoch);
    }

    private void InvalidateCore(long epoch)
    {
        if (_disposed || !_started || epoch != Volatile.Read(ref _epoch) || epoch == Volatile.Read(ref _appliedEpoch)) return;
        _renderCancellation?.Cancel();
        _observedLayouts = _layouts.Overlays;
        _observedLoadError = _layouts.LoadError;
        if (_paused || !_owner.IsHandleCreated || _layouts.LoadError is not null)
        {
            ClearSlots(_paused ? "placement" : !_owner.IsHandleCreated ? "owner-handle" : "layout-load-error", "invalidate");
            ReleaseLease();
            return;
        }
        if (!_layouts.Overlays.Any(window => window.Settings.Enabled && window.Settings.Widgets.Count > 0))
        {
            ClearSlots("no-enabled-content", "invalidate"); ReleaseLease(); return;
        }
        if (!RefreshLease()) return;
        // Revocation uses current policy immediately, before awaiting a browser capture.
        foreach (var id in _slots.Keys.ToArray())
        {
            var window = _layouts.Overlays.FirstOrDefault(item => item.Id == id);
            if (window is null || !Eligible(window, _snapshot ?? new())) RemoveSlot(id, window is null ? "removed-layout" : "ineligible", "pre-capture");
        }
        var previous = _snapshot;
        OverlaySnapshot captured;
        try { captured = _presentation.Capture(false); }
        catch (Exception error)
        {
            OverlayEditorLiveDiagnostics.Error(null, error, "source-capture", epoch);
            ClearSlots("source-error", "capture"); ReleaseLease();
            foreach (var window in _layouts.Overlays) Report(window.Id, false, error.Message);
            return;
        }
        if (epoch != Volatile.Read(ref _epoch)) return;
        _snapshot = captured;
        if (previous is not null)
            foreach (var pair in _slots)
            {
                if (!AuthorityChanged(previous, _snapshot, pair.Value.Settings)) continue;
                OverlayEditorLiveDiagnostics.Lifecycle(pair.Value.Settings, "hide", "authority-changed", "post-capture", epoch,
                    OverlayEditorLiveDiagnostics.AuthorityChanges(previous, _snapshot));
                pair.Value.Window.HideFrame(); Report(pair.Key, false);
            }
        foreach (var id in _slots.Keys.ToArray())
        {
            var window = _layouts.Overlays.FirstOrDefault(item => item.Id == id);
            if (window is null || !Eligible(window, _snapshot)) RemoveSlot(id, window is null ? "removed-layout" : "ineligible", "post-capture");
            else if (_slots[id].Failed) RemoveSlot(id, "failed-slot", "post-capture");
        }
        RefreshLease();
        if (epoch != Volatile.Read(ref _epoch)) return;
        Volatile.Write(ref _appliedEpoch, epoch);
        _requested = true;
        _ = RenderAsync();
    }

    private void LayoutsChanged()
    {
        if (ReferenceEquals(_observedLayouts, _layouts.Overlays) && _observedLoadError == _layouts.LoadError) return;
        Invalidate();
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed || !_started || _paused) return;
        var monitors = _monitors();
        if (!_observedMonitors.SequenceEqual(monitors))
        {
            _observedMonitors = monitors.ToArray();
            foreach (var slot in _slots.Values)
            {
                OverlayEditorLiveDiagnostics.Lifecycle(slot.Settings, "hide", "monitor-change", "timer", Volatile.Read(ref _epoch));
                slot.Window.HideFrame();
            }
            Invalidate();
            return;
        }
        if (_snapshot?.ItemAnimationsEnabled != true) return;
        if (_slots.Any(pair => !pair.Value.Failed && pair.Value.Window.Visible
            && _layouts.Overlays.FirstOrDefault(window => window.Id == pair.Key) is { } current && Animated(current)))
        {
            _requested = true;
            _ = RenderAsync();
        }
    }

    private async Task RenderAsync()
    {
        if (_rendering || _disposed || _paused || !_owner.IsHandleCreated) return;
        _rendering = true;
        try
        {
            while (_requested && !_disposed && !_paused && _owner.IsHandleCreated)
            {
                _requested = false;
                var epoch = Volatile.Read(ref _appliedEpoch);
                if (epoch != Volatile.Read(ref _epoch)) break;
                var snapshot = _snapshot;
                if (snapshot is null) return;
                _renderCancellation?.Dispose();
                _renderCancellation = new();
                var token = _renderCancellation.Token;
                var monitors = _monitors();
                _observedMonitors = monitors.ToArray();
                foreach (var window in _layouts.Overlays.ToArray())
                {
                    if (token.IsCancellationRequested || epoch != Volatile.Read(ref _epoch) || _disposed || _paused) break;
                    if (!Eligible(window, snapshot)) { RemoveSlot(window.Id, "ineligible", "pre-await"); Report(window.Id, false); continue; }
                    var preferred = _preferred();
                    var monitor = monitors.FirstOrDefault(value => value.Id == window.Settings.PositionMonitorId)
                        ?? monitors.FirstOrDefault(value => value.Id == preferred?.Id) ?? monitors.FirstOrDefault();
                    if (monitor is null) { RemoveSlot(window.Id, "monitor-unavailable", "pre-await"); Report(window.Id, false, "Es ist kein Monitor verfügbar."); continue; }
                    try
                    {
                        if (!_slots.TryGetValue(window.Id, out var slot))
                        {
                            var session = _renderer.CreateSession();
                            IOverlayEditorLiveSurface surface;
                            try { surface = _surface(); }
                            catch { session.Dispose(); throw; }
                            surface.GeometryInvalidated += Invalidate;
                            slot = new(surface, session, window.Settings);
                            _slots.Add(window.Id, slot);
                        }
                        if (slot.Failed) continue;
                        slot.Settings = window.Settings;
                        using var bitmap = await slot.Session.UpdateAndCaptureAsync(window, snapshot, token);
                        if (token.IsCancellationRequested || epoch != Volatile.Read(ref _epoch) || _disposed || _paused) break;
                        var current = _layouts.Overlays.FirstOrDefault(value => value.Id == window.Id);
                        if (current is null || !ReferenceEquals(current.Settings, window.Settings) || !Eligible(current, snapshot))
                        {
                            OverlayEditorLiveDiagnostics.Lifecycle(slot.Settings, "hide", current is null ? "removed-layout" : !ReferenceEquals(current.Settings, window.Settings) ? "settings-changed" : "ineligible", "post-await", epoch);
                            slot.Window.HideFrame(); Report(window.Id, false); continue;
                        }
                        var layout = OverlayEditorPlacementGeometry.Read(current.Settings, bitmap.Size, monitor);
                        layout = OverlayPlacementForm.FitLayout(layout, bitmap.Size, monitor.WorkingArea, .5);
                        var size = OverlayEditorPlacementGeometry.ScaledSize(bitmap.Size, layout.Scale);
                        slot.Window.ShowFrame(bitmap, new(new Point(layout.X, layout.Y), size),
                            _recordings() && !current.Settings.CaptureExcluded);
                        CompletePresentation(window.Id, slot, current.Settings, snapshot, epoch);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error)
                    {
                        OverlayEditorLiveDiagnostics.Error(window.Settings, error, "render", epoch);
                        // Obsolete failures cannot revoke a still-authorized surface.
                        if (token.IsCancellationRequested || epoch != Volatile.Read(ref _epoch) || _disposed || _paused) continue;
                        if (_slots.TryGetValue(window.Id, out var slot)) { slot.Failed = true; slot.Window.HideFrame(); }
                        if (epoch == Volatile.Read(ref _epoch) && !_disposed && !_paused) Report(window.Id, false, error.Message);
                    }
                }
                RefreshLease();
            }
        }
        finally { _rendering = false; }
    }

    private void CompletePresentation(string id, Slot presented, OverlaySettings settings, OverlaySnapshot rendered, long epoch)
    {
        // ShowFrame can pump a UI callback or overlap a worker publication. A new
        // value revision alone must not blank the frame that was just presented.
        // Apply a pending revision here so real revocations still take effect
        // before returning, even when their queued UI callback has not run yet.
        for (var attempt = 0; attempt < 2 && !_disposed && !_paused; attempt++)
        {
            var latest = Volatile.Read(ref _epoch);
            if (latest == Volatile.Read(ref _appliedEpoch)) break;
            InvalidateCore(latest);
        }
        // Reconciliation may have removed/disposed this slot or replaced it.
        if (_disposed || !_slots.TryGetValue(id, out var active) || !ReferenceEquals(active, presented)) return;
        var current = _layouts.Overlays.FirstOrDefault(window => window.Id == id);
        var snapshot = _snapshot;
        var allowed = !_paused && _owner.IsHandleCreated
            && Volatile.Read(ref _appliedEpoch) == Volatile.Read(ref _epoch)
            && current is not null && ReferenceEquals(current.Settings, settings)
            && snapshot is not null && !AuthorityChanged(rendered, snapshot, settings) && Eligible(current, snapshot);
        if (!allowed)
        {
            OverlayEditorLiveDiagnostics.Lifecycle(settings, "hide", "revoked", "post-present", epoch);
            presented.Window.HideFrame(); Report(id, false);
        }
        else
        {
            if (presented.Window.Visible && epoch != Volatile.Read(ref _appliedEpoch))
                OverlayEditorLiveDiagnostics.Lifecycle(settings, "retain", "same-authority-refresh", "post-present", epoch);
            Report(id, presented.Window.Visible);
        }
    }

    private static bool AuthorityChanged(OverlaySnapshot previous, OverlaySnapshot current, OverlaySettings settings) =>
        previous.SessionId != current.SessionId || previous.SessionActive != current.SessionActive
        || previous.TrackingAvailable != current.TrackingAvailable || previous.Abyss.ProfileId != current.Abyss.ProfileId
        || previous.Abyss.BindingGeneration != current.Abyss.BindingGeneration
        // Abyss visibility qualifies only its own renderers. A point-source update
        // must not blank an unrelated Kina/clock/drop window while its session stays valid.
        || previous.Abyss.WorldContextVisible != current.Abyss.WorldContextVisible
            && settings.Widgets.Any(widget => widget.Kind is "abyss-killfeed" or "abyss-rank-progress")
        || settings.Widgets.Any(widget => widget.Kind == "genus-insight") && (
            previous.GenusInsight.ProfileId != current.GenusInsight.ProfileId
            || previous.GenusInsight.Generation != current.GenusInsight.Generation
            || previous.GenusInsight.IsCurrent != current.GenusInsight.IsCurrent
            || previous.GenusInsight.MenuVisible != current.GenusInsight.MenuVisible
            || previous.GenusInsight.SelectedGenus != current.GenusInsight.SelectedGenus
            || previous.GenusInsight.ViewedPreset != current.GenusInsight.ViewedPreset
            || previous.GenusInsight.MenuSourceGeneration != current.GenusInsight.MenuSourceGeneration
            || GenusInsightOverlayContent.Available(previous.GenusInsight) != GenusInsightOverlayContent.Available(current.GenusInsight));

    private bool Eligible(OverlayInstance window, OverlaySnapshot snapshot) => window.Settings.Enabled
        && window.Settings.Widgets.Count > 0 && _layouts.LoadError is null && _isVisible(window, snapshot);
    private static bool Animated(OverlayInstance window) => window.Settings.Widgets.Any(widget =>
        OverlayCatalog.IsLootWidget(widget.Kind) || widget.Kind is "rare-notice" or "abyss-killfeed" or "abyss-rank-progress");

    private bool RefreshLease()
    {
        if (_disposed || _paused || !_owner.IsHandleCreated || _layouts.LoadError is not null
            || !_layouts.Overlays.Any(window => window.Settings.Enabled && window.Settings.Widgets.Count > 0))
        { ReleaseLease(); return true; }
        if (_acquiringLease) return true;
        var market = _slots.Any(pair => pair.Value.Window.Visible && _layouts.Overlays.FirstOrDefault(window => window.Id == pair.Key)
            is { } current && current.Settings.Widgets.Any(widget => widget.Kind is "market-kina" or "market-kina-hour"));
        if (_lease is not null && _leasedMarket == market) return true;
        _acquiringLease = true;
        try
        {
            // Keep the shared menu observer alive while only the market requirement changes.
            // Acquire first; reentrant source notifications cannot create another host lease.
            var previous = _lease;
            _lease = _presentation.Acquire(false, market);
            _leasedMarket = market;
            previous?.Dispose();
            return true;
        }
        catch (Exception error)
        {
            OverlayEditorLiveDiagnostics.Error(null, error, "acquire-lease", Volatile.Read(ref _epoch));
            foreach (var pair in _slots) pair.Value.Window.HideFrame();
            foreach (var window in _layouts.Overlays) Report(window.Id, false, error.Message);
            return false;
        }
        finally { _acquiringLease = false; }
    }
    private void ReleaseLease() { _lease?.Dispose(); _lease = null; _leasedMarket = null; }
    private void Report(string id, bool visible, string? error = null)
    {
        if (_disposed) return;
        _layouts.SetRuntimeState(id, visible, error);
    }
    private void RemoveSlot(string id, string reason = "unspecified", string phase = "cleanup")
    {
        if (!_slots.Remove(id, out var slot)) return;
        OverlayEditorLiveDiagnostics.Lifecycle(slot.Settings, "remove", reason, phase, Volatile.Read(ref _epoch));
        slot.Window.GeometryInvalidated -= Invalidate;
        slot.Window.HideFrame();
        slot.Window.Dispose();
        slot.Session.Dispose();
        // Cleanup still publishes hidden after Dispose has revoked reentrant work.
        _layouts.SetRuntimeState(id, false);
    }
    private void ClearSlots(string reason, string phase)
    {
        OverlayEditorLiveDiagnostics.Lifecycle(null, "clear", reason, phase, Volatile.Read(ref _epoch));
        foreach (var id in _slots.Keys.ToArray()) RemoveSlot(id, reason, phase);
    }
    private bool Marshal(Action action)
    {
        if (_disposed || _owner.IsDisposed || _owner.Disposing) return true;
        if (!_owner.InvokeRequired) return false;
        if (_owner.IsHandleCreated)
        {
            try { _owner.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }
        return true;
    }
    private void OwnerHandleDestroyed(object? sender, EventArgs e) { Interlocked.Increment(ref _epoch); _renderCancellation?.Cancel(); ClearSlots("owner-handle-destroyed", "lifecycle"); ReleaseLease(); }
    private void OwnerHandleCreated(object? sender, EventArgs e) => Invalidate();

    public void Dispose()
    {
        if (_disposed) return;
        if (_owner.InvokeRequired) throw new InvalidOperationException("Live overlays must be disposed on their owning UI thread.");
        // Disposing WebView sessions may pump queued UI callbacks. Revoke new work first.
        _disposed = true;
        _timer.Stop(); _timer.Dispose();
        _layouts.Changed -= LayoutsChanged;
        _presentation.Changed -= Invalidate;
        _owner.HandleDestroyed -= OwnerHandleDestroyed;
        _owner.HandleCreated -= OwnerHandleCreated;
        Interlocked.Increment(ref _epoch); _renderCancellation?.Cancel();
        ClearSlots("disposed", "lifecycle"); ReleaseLease();
    }
}
