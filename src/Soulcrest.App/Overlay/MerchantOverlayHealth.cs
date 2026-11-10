using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Soulcrest.App.Overlay;

internal sealed record MerchantOverlayHealthTransition(DateTimeOffset At, string Reason);

internal sealed record MerchantOverlayHealthSnapshot(string Observation, string Presentation, string WorkerState,
    bool PlacementHidden, bool ForegroundSuppressed, int? TotalOffers, int? PetRows, int? BadgeCount,
    DateTimeOffset? AnalysisObservedAtUtc, DateTimeOffset? PresentationObservedAtUtc,
    double? AnalysisAgeMilliseconds, double? FrameAgeMilliseconds,
    long LayoutMisses, long CaptureMisses, long Held, long Renders, long Errors,
    IReadOnlyList<MerchantOverlayHealthTransition> Transitions);

/// <summary>Bounded, anonymous reasons for merchant visibility; no offers, identities, paths or pixels.</summary>
internal sealed class MerchantOverlayHealth
{
    private readonly object _gate = new();
    private readonly Queue<MerchantOverlayHealthTransition> _transitions = new();
    private readonly Func<long> _timestamp;
    private readonly Func<DateTimeOffset> _utcNow;
    private string _observation = "not-started", _presentation = "hidden";
    private string _workerState = "not-started";
    private bool _placementHidden, _foregroundSuppressed;
    private int? _totalOffers, _petRows, _badgeCount;
    private long _layoutMisses, _captureMisses, _held, _renders, _errors;
    private long? _analysisAt, _frameAt;
    private DateTimeOffset? _analysisObservedAtUtc, _presentationObservedAtUtc;

    internal MerchantOverlayHealth(Func<long>? timestamp = null, Func<DateTimeOffset>? utcNow = null)
    {
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public void SetPresentationSuppression(bool placementHidden, bool foregroundSuppressed)
    {
        lock (_gate)
        {
            if (_placementHidden != placementHidden || _foregroundSuppressed != foregroundSuppressed)
                Add(placementHidden ? foregroundSuppressed ? "gate:placement-and-foreground" : "gate:placement-hidden"
                    : foregroundSuppressed ? "gate:foreground-suppressed" : "gate:released");
            _placementHidden = placementHidden;
            _foregroundSuppressed = foregroundSuppressed;
            if (placementHidden || foregroundSuppressed) PresentCore("hidden");
        }
    }

    public void SetWorkerState(string state)
    {
        lock (_gate)
        {
            if (state is not ("not-started" or "running" or "stopped" or "faulted")) state = "unknown";
            if (_workerState == state) return;
            if (state == "faulted") _errors++;
            Add("worker:" + state);
            _workerState = state;
        }
    }

    public void ObserveOffers(int? totalOffers, int? petRows, int? badgeCount)
    {
        lock (_gate)
        {
            _totalOffers = totalOffers is >= 0 ? totalOffers : null;
            _petRows = petRows is >= 0 ? petRows : null;
            _badgeCount = badgeCount is >= 0 ? badgeCount : null;
        }
    }

    public void Observe(string reason, long frameAt)
    {
        lock (_gate)
        {
            // Restrict diagnostics to implementation-owned tokens, never exception messages.
            if (reason is not ("disabled" or "no-offers" or "offer-count" or "not-foreground"
                or "capture-held" or "capture-missing" or "not-window-capture" or "frame-stale"
                or "frame-size-mismatch" or "layout-missing" or "confirming-layout" or "layout-ready"
                or "no-pet-offers" or "badge-bounds-invalid" or "capture-error")) reason = "unknown";
            if (reason == "layout-missing") _layoutMisses++;
            if (reason is "capture-held" or "capture-missing") _captureMisses++;
            if (reason == "capture-held") _held++;
            if (reason == "capture-error") _errors++;
            var now = _timestamp();
            _analysisAt = now;
            _analysisObservedAtUtc = _utcNow().ToUniversalTime();
            _frameAt = frameAt > 0 && frameAt <= now ? frameAt : null;
            if (_observation != reason) Add("analysis:" + reason);
            _observation = reason;
        }
    }

    public void Present(string reason)
    {
        lock (_gate)
        {
            if (reason is not ("shown" or "hidden" or "presentation-error")) reason = "unknown";
            PresentCore(reason);
        }
    }

    private void PresentCore(string reason)
    {
        if (reason == "shown") _renders++;
        if (reason == "presentation-error") _errors++;
        if (_presentation != reason) Add("display:" + reason);
        _presentation = reason;
        _presentationObservedAtUtc = _utcNow().ToUniversalTime();
    }

    private void Add(string reason)
    {
        _transitions.Enqueue(new(_utcNow().ToUniversalTime(), reason));
        while (_transitions.Count > 24) _transitions.Dequeue();
    }

    public MerchantOverlayHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _timestamp();
            double? Age(long? at) => at is { } stamp && stamp <= now
                ? Stopwatch.GetElapsedTime(stamp, now).TotalMilliseconds : null;
            return new(_observation, _presentation, _workerState, _placementHidden, _foregroundSuppressed,
                _totalOffers, _petRows, _badgeCount, _analysisObservedAtUtc, _presentationObservedAtUtc,
                Age(_analysisAt), Age(_frameAt), _layoutMisses, _captureMisses, _held, _renders, _errors,
                Array.AsReadOnly(_transitions.ToArray()));
        }
    }

    public string Report()
    {
        var state = Snapshot();
        var text = new StringBuilder("\n[Händler-Overlay]\n");
        text.AppendLine($"Beobachtung: {state.Observation}; Anzeige: {state.Presentation}");
        text.AppendLine($"Worker: {state.WorkerState}; Platzierung verborgen: {state.PlacementHidden}; Vordergrund gesperrt: {state.ForegroundSuppressed}");
        text.AppendLine($"Bildalter bei letzter Analyse (ms): {state.FrameAgeMilliseconds?.ToString("F1", CultureInfo.InvariantCulture) ?? "unbekannt"}");
        text.AppendLine($"Layoutausfälle: {state.LayoutMisses}; Aufnahmeaussetzer: {state.CaptureMisses}; kurz überbrückt: {state.Held}; Übergaben: {state.Renders}; Fehler: {state.Errors}");
        foreach (var entry in state.Transitions) text.AppendLine($"{entry.At:O} {entry.Reason}");
        return text.ToString();
    }
}
