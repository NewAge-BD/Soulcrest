namespace Soulcrest.App.Services;

/// <summary>Only observations of the main window, never an instruction to start a renderer.</summary>
public sealed record AppThemeRuntimeSnapshot(string RequestedId, string? NativeAppliedId, string? WebAppliedId,
    string? NativeError, string? WebError, DateTimeOffset? AppliedAtUtc, long Revision);

/// <summary>User request 2026-11-11: passive, anonymous application state for the main-window theme.</summary>
public sealed class AppThemeRuntime
{
    private readonly object _gate = new();
    private AppThemeRuntimeSnapshot _snapshot = new(AppThemeDefinition.DefaultId, null, null, null, null, null, 0);
    public AppThemeRuntimeSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public AppThemeRuntimeSnapshot Request(string? id)
    {
        lock (_gate)
        {
            var normalized = AppThemeDefinition.NormalizeId(id);
            if (_snapshot.RequestedId != normalized)
                _snapshot = new(normalized, _snapshot.NativeAppliedId, null, null, null, null, _snapshot.Revision + 1);
            return _snapshot;
        }
    }

    public void InvalidateWeb()
    {
        lock (_gate)
            _snapshot = _snapshot with { WebAppliedId = null, WebError = null, AppliedAtUtc = null, Revision = _snapshot.Revision + 1 };
    }

    public void ObserveNative(long revision, string? error = null)
    {
        lock (_gate)
        {
            if (revision != _snapshot.Revision) return;
            _snapshot = _snapshot with { NativeAppliedId = error is null ? _snapshot.RequestedId : null, NativeError = error };
        }
    }

    public void ObserveWeb(long revision, string? appliedId, string? error = null)
    {
        lock (_gate)
        {
            if (revision != _snapshot.Revision) return;
            var matches = appliedId == _snapshot.RequestedId && error is null;
            _snapshot = _snapshot with { WebAppliedId = matches ? appliedId : null,
                WebError = error ?? (matches ? null : "ThemeAcknowledgementMismatch"),
                AppliedAtUtc = matches ? DateTimeOffset.UtcNow : null };
        }
    }
}
