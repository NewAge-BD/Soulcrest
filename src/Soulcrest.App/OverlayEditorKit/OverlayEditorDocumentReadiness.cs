namespace Soulcrest.App.OverlayEditorKit;

/// <summary>Per-host local-document gate; creating CoreWebView2 alone does not make script execution ready.</summary>
internal sealed class OverlayEditorDocumentReadiness
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong? _navigationId;

    internal Task Ready => _ready.Task;

    internal void NavigationStarting(ulong navigationId, string? uri)
    {
        lock (_gate)
        {
            if (_ready.Task.IsCompleted || !OverlayEditorBitmapRenderer.AllowsResource(uri)) return;
            _navigationId = navigationId;
        }
    }

    internal void DocumentLoaded(ulong navigationId, string? uri)
    {
        lock (_gate)
        {
            if (_ready.Task.IsCompleted || !OverlayEditorBitmapRenderer.AllowsResource(uri)) return;
            // Subscribing after NavigationStarting is safe when the actual local DOM event proves readiness.
            _navigationId ??= navigationId;
            if (_navigationId == navigationId) _ready.TrySetResult();
        }
    }

    internal void NavigationCompleted(ulong navigationId, bool success)
    {
        lock (_gate)
        {
            if (_ready.Task.IsCompleted || _navigationId != navigationId || success) return;
            _ready.TrySetException(new InvalidOperationException("The local editor rendering document did not load."));
        }
    }

    internal void Cancel()
    {
        lock (_gate) _ready.TrySetCanceled();
    }

    internal void Failure(Exception error)
    {
        lock (_gate) _ready.TrySetException(error);
    }
}
