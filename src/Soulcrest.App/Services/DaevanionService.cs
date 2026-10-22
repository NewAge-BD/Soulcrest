using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Services;

public sealed record DaevanionSnapshot(OwnCharacterContext Context, DaevanionBoard Board,
    Rectangle GameBounds, long FrameAt, DaevanionBoardDetection Detection, string Language);

/// <summary>Passive board projection. No map-tracking dependency and no secondary capture session.</summary>
public sealed class DaevanionService : IDisposable
{
    private readonly NetworkLootService _network;
    private readonly SettingsService _settings;
    private readonly GameCaptureService _capture;
    private readonly Func<Rectangle?> _gameBounds;
    private readonly object _publicationGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly DaevanionLayoutStability _stability = new();
    private readonly DaevanionBoardPattern[] _patterns;
    private sealed record PresentationPreferences(bool Enabled, string Language, string ShowMode);
    private PresentationPreferences _preferences;
    private Task? _worker;
    private DaevanionSnapshot? _snapshot;
    private string _status = "Warte auf Klassenerkennung.";
    private string? _highlightPlacementStatus;
    private long _revision;
    private int _disposed;

    public DaevanionService(NetworkLootService network, SettingsService settings, GameCaptureService capture)
        : this(network, settings, capture, null) { }

    internal DaevanionService(NetworkLootService network, SettingsService settings, GameCaptureService capture,
        Func<Rectangle?>? gameBounds)
    {
        _network = network; _settings = settings; _capture = capture;
        _preferences = ReadPreferences();
        _gameBounds = gameBounds ?? (() => _settings.Current.OverlayShowMode == "always"
            ? MonitorCapture.GameWindowBounds() : MonitorCapture.ForegroundGameWindowBounds());
        _patterns = DaevanionCatalog.Default.Boards.GroupBy(b => b.FamilyId).Select(g =>
            new DaevanionBoardPattern(g.Key, g.First().Nodes.Select(n =>
                new DaevanionPatternCell(n.Row, n.Column, n.Kind == DaevanionNodeKind.Start)).ToArray())).ToArray();
        _network.OwnCharacterChanged += ContextChanged;
        _settings.Changed += SettingsChanged;
    }

    public DaevanionSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public string Status => Volatile.Read(ref _status);
    public string? HighlightPlacementStatus => Volatile.Read(ref _highlightPlacementStatus);
    public string? ClassId => _network.OwnCharacter?.Identity.ClassId;
    internal long ObservationRevision => Interlocked.Read(ref _revision);
    public event Action? Changed;

    // Presentation-only feedback: never changes source evidence or the observation revision.
    internal void SetHighlightPlacementStatus(string? status)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _highlightPlacementStatus, status) != status)
            SafeEvent.Raise(Changed, "Daevanion-Overlay");
    }

    public void Start()
    {
        lock (_publicationGate)
            if (_worker is null && Volatile.Read(ref _disposed) == 0) _worker = Task.Run(Run);
    }

    private bool SourceIsCurrent(DaevanionSnapshot snapshot) => Volatile.Read(ref _disposed) == 0
        && _settings.Current.DaevanionOverlayEnabled && snapshot.Context.Identity.ClassId is { } classId
        && snapshot.Board.ClassId == classId && snapshot.Detection.Family == snapshot.Board.FamilyId
        && ReferenceEquals(snapshot.Context, _network.OwnCharacter)
        && snapshot.Language == _settings.Current.LootNameLanguage
        && DaevanionLayoutStability.Fresh(snapshot.FrameAt) && _gameBounds() == snapshot.GameBounds;

    public bool CanPresent(DaevanionSnapshot snapshot) => ReferenceEquals(snapshot, Snapshot)
        && SourceIsCurrent(snapshot) && ReferenceEquals(snapshot, Snapshot);

    private void ContextChanged() => Invalidate(_network.OwnCharacter?.Identity.ClassId is null
        ? "Warte auf Klassenerkennung." : "Daevanion-Board wird erkannt …");
    private PresentationPreferences ReadPreferences() => new(_settings.Current.DaevanionOverlayEnabled,
        _settings.Current.LootNameLanguage, _settings.Current.OverlayShowMode);

    private void SettingsChanged()
    {
        lock (_publicationGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var preferences = ReadPreferences();
            if (_preferences != preferences)
            {
                _preferences = preferences;
                InvalidateLocked(!preferences.Enabled ? "Daevanion-Overlay ausgeschaltet." : "Daevanion-Board wird erkannt …");
            }
        }
        // Recording visibility needs a new native presentation. Themes and other overlays'
        // settings never invalidate this fixed native board geometry or its frame evidence.
        SafeEvent.Raise(Changed, "Daevanion-Overlay");
    }

    private async Task Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            var revision = ObservationRevision;
            try { Observe(revision); }
            catch (Exception error) when (CaptureReadback.IsBusy(error)) { ObserveMissingCapture(revision); }
            catch (Exception error) when (error is InvalidOperationException or ExternalException or ArgumentException
                or OpenCvSharp.OpenCVException or IOException)
            {
                _stability.Reset(); Publish(null, "Daevanion-Aufnahme nicht verfügbar.", revision);
                Trace.TraceWarning("Daevanion capture: {0}", error.GetType().Name);
            }
            try { await Task.Delay(200, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Observe(long revision)
    {
        var context = _network.OwnCharacter;
        var bounds = _gameBounds();
        var language = _settings.Current.LootNameLanguage;
        if (!_settings.Current.DaevanionOverlayEnabled || context?.Identity.ClassId is null || bounds is null)
        {
            _stability.Reset();
            Publish(null, !_settings.Current.DaevanionOverlayEnabled ? "Daevanion-Overlay ausgeschaltet."
                : context?.Identity.ClassId is null ? "Warte auf Klassenerkennung." : "Warte auf das Spielfenster.", revision);
            return;
        }
        var key = LayoutIdentity(revision, context, language);
        var scale = Math.Min(1d, 1920d / bounds.Value.Width);
        using var image = _capture.TryGrabGameWindowOnly(bounds.Value, scale);
        var at = GameCaptureService.GrabbedFrameAt;
        if (image is null) { ObserveMissingCapture(revision); return; }
        if (!GameCaptureService.GrabbedGameWindowOnly || !DaevanionLayoutStability.Fresh(at)
            || !MerchantLayoutStability.MatchesCaptureSize(bounds.Value.Size, scale, new(image.Width, image.Height)))
        { _stability.Reset(); Publish(null, "Daevanion-Aufnahme nicht verfügbar.", revision); return; }
        var preferred = _stability.Preferred(key, bounds.Value);
        if (preferred is not null) preferred = ScaleDetection(preferred, scale);
        // Like the shop: validate the frozen layout against this new game-window image before
        // allowing a noisy global fit to move it. A preferred layout is never capture evidence.
        var found = DaevanionBoardLocator.Find(image, _patterns, preferred);
        if (found is not null) found = ScaleDetection(found, 1 / scale);
        if (!_stability.Update(key, bounds.Value, at, found))
        { Publish(null, found is null ? "Daevanion-Board nicht erkannt." : "Daevanion-Board wird erkannt …", revision); return; }
        var board = DaevanionCatalog.Default.GetBoard(context.Identity.ClassId, found!.Family);
        if (board is null)
        { _stability.Reset(); Publish(null, "Daevanion-Board nicht erkannt.", revision); return; }
        TryPublishObservation(new(context, board, bounds.Value, _stability.ConfirmedFrameAt,
            _stability.Confirmed!, language), revision);
    }

    internal bool TryPublishObservation(DaevanionSnapshot snapshot, long revision)
    {
        // Never acquire the network lock while holding _publicationGate: network callbacks
        // invalidate under their own lock. The revision check closes the later publication race.
        if (!SourceIsCurrent(snapshot)) return false;
        return Publish(snapshot, "Daevanion-Board erkannt.", revision);
    }

    private static string LayoutIdentity(long revision, OwnCharacterContext context, string language) =>
        $"{revision}:{context.Generation}:{context.Identity.ServerId}:{context.Identity.CharacterId}:{context.Identity.ClassId}:{language}";

    private static DaevanionBoardDetection ScaleDetection(DaevanionBoardDetection detection, double scale) =>
        detection with { Origin = new(detection.Origin.X * scale, detection.Origin.Y * scale),
            Spacing = detection.Spacing * scale, NodeSize = detection.NodeSize * scale,
            Nodes = detection.Nodes.Select(n => n with { Bounds = new OpenCvSharp.Rect(
                (int)Math.Round(n.Bounds.X * scale), (int)Math.Round(n.Bounds.Y * scale),
                (int)Math.Round(n.Bounds.Width * scale), (int)Math.Round(n.Bounds.Height * scale)) }).ToArray() };

    internal void ObserveMissingCapture(long revision)
    {
        // Missing/busy WGC reads are not negative board evidence. Retain exactly the existing
        // snapshot until its original source-frame deadline; never stamp a new time or generation.
        if (Snapshot is { } current && CanPresent(current))
        {
            _stability.RetainAfterMissingCapture(LayoutIdentity(revision, current.Context, current.Language),
                current.GameBounds, DaevanionLayoutStability.Fresh(current.FrameAt));
            return;
        }
        _stability.Reset(); Publish(null, "Daevanion-Aufnahme nicht verfügbar.", revision);
    }

    private void Invalidate(string status)
    {
        lock (_publicationGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            InvalidateLocked(status);
        }
        SafeEvent.Raise(Changed, "Daevanion-Overlay");
    }

    /// <summary>Caller owns the publication gate; events are always raised after releasing it.</summary>
    private void InvalidateLocked(string status)
    {
        Interlocked.Increment(ref _revision);
        Interlocked.Exchange(ref _snapshot, null);
        Interlocked.Exchange(ref _highlightPlacementStatus, null);
        Interlocked.Exchange(ref _status, status);
    }

    private bool Publish(DaevanionSnapshot? snapshot, string status, long revision)
    {
        bool changed;
        lock (_publicationGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || revision != _revision) return false;
            var previous = Interlocked.Exchange(ref _snapshot, snapshot);
            if (snapshot is null) Interlocked.Exchange(ref _highlightPlacementStatus, null);
            var oldStatus = Interlocked.Exchange(ref _status, status);
            changed = !ReferenceEquals(previous, snapshot) || oldStatus != status;
        }
        if (changed) SafeEvent.Raise(Changed, "Daevanion-Overlay");
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _network.OwnCharacterChanged -= ContextChanged; _settings.Changed -= SettingsChanged;
        _stop.Cancel();
        lock (_publicationGate) Interlocked.Exchange(ref _snapshot, null);
        if (_worker is { } worker) _ = worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
        else _stop.Dispose();
    }
}
