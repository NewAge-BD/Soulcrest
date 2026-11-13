using System.Runtime.InteropServices;

namespace Soulcrest.App.OverlayEditorKit;

internal enum OverlayEditorRenderStage { Queue, PreviousCapture, Host, PixelCalibration, SurfaceReady, Capture, Decode, DocumentReady }

internal sealed record OverlayEditorRenderHealth(long Succeeded, long Failed, bool LastAttemptFailed,
    OverlayEditorRenderStage? FailureStage = null, string? ExceptionType = null, int? HResult = null);

/// <summary>Retains only counters and the last anonymous failure, including Place Mode before diagnosis starts.</summary>
internal sealed class OverlayEditorRenderDiagnostics
{
    internal static OverlayEditorRenderDiagnostics Current { get; } = new();
    private readonly object _gate = new();
    private OverlayEditorRenderHealth _health = new(0, 0, false);

    internal OverlayEditorRenderHealth Capture() { lock (_gate) return _health; }
    internal void Success() { lock (_gate) _health = _health with { Succeeded = _health.Succeeded + 1, LastAttemptFailed = false }; }
    internal void Failure(OverlayEditorRenderStage stage, Exception error)
    {
        // Wrappers contain local paths/messages. Keep only a bounded underlying error category/code.
        for (var depth = 0; depth < 4 && error.InnerException is { } inner; depth++) error = inner;
        var type = error switch
        {
            COMException => nameof(COMException), UnauthorizedAccessException => nameof(UnauthorizedAccessException),
            FileNotFoundException => nameof(FileNotFoundException), IOException => nameof(IOException),
            TimeoutException or OperationCanceledException => nameof(TimeoutException),
            ArgumentException => nameof(ArgumentException), InvalidOperationException => nameof(InvalidOperationException),
            _ => "Other",
        };
        lock (_gate) _health = new(_health.Succeeded, _health.Failed + 1, true, stage, type, error.HResult);
    }
}
