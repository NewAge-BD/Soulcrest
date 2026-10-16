using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Soulcrest.App.Overlay;

/// <summary>Bounded, anonymous reasons for merchant visibility; no offers, identities, paths or pixels.</summary>
internal sealed class MerchantOverlayHealth
{
    private readonly object _gate = new();
    private readonly Queue<(DateTimeOffset At, string Reason)> _transitions = new();
    private string _observation = "not-started", _presentation = "hidden";
    private long _layoutMisses, _captureMisses, _held, _renders, _errors;
    private double? _frameAge;

    public void Observe(string reason, long frameAt)
    {
        lock (_gate)
        {
            // Restrict diagnostics to implementation-owned tokens, never exception messages.
            if (reason is not ("disabled" or "no-offers" or "offer-count" or "not-foreground"
                or "capture-held" or "capture-missing" or "not-window-capture" or "frame-stale"
                or "frame-size-mismatch" or "layout-missing" or "confirming-layout" or "layout-ready"
                or "no-pet-offers" or "capture-error")) reason = "unknown";
            if (reason == "layout-missing") _layoutMisses++;
            if (reason is "capture-held" or "capture-missing") _captureMisses++;
            if (reason == "capture-held") _held++;
            if (reason == "capture-error") _errors++;
            _frameAge = frameAt > 0 && frameAt <= Stopwatch.GetTimestamp() ? Stopwatch.GetElapsedTime(frameAt).TotalMilliseconds : null;
            if (_observation != reason) Add("analysis:" + reason);
            _observation = reason;
        }
    }

    public void Present(string reason)
    {
        lock (_gate)
        {
            if (reason is not ("shown" or "hidden" or "presentation-error")) reason = "unknown";
            if (reason == "shown") _renders++;
            if (reason == "presentation-error") _errors++;
            if (_presentation != reason) Add("display:" + reason);
            _presentation = reason;
        }
    }

    private void Add(string reason)
    {
        _transitions.Enqueue((DateTimeOffset.UtcNow, reason));
        while (_transitions.Count > 24) _transitions.Dequeue();
    }

    public string Report()
    {
        lock (_gate)
        {
            var text = new StringBuilder("\n[Händler-Overlay]\n");
            text.AppendLine($"Beobachtung: {_observation}; Anzeige: {_presentation}");
            text.AppendLine($"Bildalter bei letzter Analyse (ms): {_frameAge?.ToString("F1", CultureInfo.InvariantCulture) ?? "unbekannt"}");
            text.AppendLine($"Layoutausfälle: {_layoutMisses}; Aufnahmeaussetzer: {_captureMisses}; kurz überbrückt: {_held}; Übergaben: {_renders}; Fehler: {_errors}");
            foreach (var entry in _transitions) text.AppendLine($"{entry.At:O} {entry.Reason}");
            return text.ToString();
        }
    }
}
