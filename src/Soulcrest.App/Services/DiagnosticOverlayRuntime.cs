using System.Collections.Concurrent;

namespace Soulcrest.App.Services;

public sealed record DiagnosticOverlayRuntimeState(bool Visible, bool HandleCreated, bool Disposed, DateTimeOffset ObservedAt);

/// <summary>Only the host UI thread observes its own windows; diagnosis reads anonymous stored facts.</summary>
public sealed class DiagnosticOverlayRuntime
{
    private readonly ConcurrentDictionary<string, DiagnosticOverlayRuntimeState> _states = new(StringComparer.Ordinal);
    public DiagnosticOverlayRuntimeState? Get(string id) => _states.GetValueOrDefault(id);
    public void Observe(string id, bool visible, bool handleCreated, bool disposed) =>
        _states[id] = new(visible, handleCreated, disposed, DateTimeOffset.UtcNow);
}
