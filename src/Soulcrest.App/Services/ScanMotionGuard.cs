using System.Drawing;
using Soulcrest.App.Capture;

namespace Soulcrest.App.Services;

/// <summary>Checks fresh grid pixels during OCR, then once more before accepting its result.</summary>
internal static class ScanMotionGuard
{
    internal static async Task<T?> ReadAsync<T>(Bitmap source, IReadOnlyList<Rectangle> bounds,
        Func<Bitmap> capture, Func<CancellationToken, Task<T>> read, Action invalidate,
        CancellationToken token) where T : class
    {
        var motion = new GridMotion();
        motion.IsStill(source, bounds);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        // ScanAsync also does synchronous portrait matching before its first await.
        var reading = Task.Run(() => read(cancelled.Token), cancelled.Token);
        try
        {
            while (true)
            {
                if (!reading.IsCompleted)
                    await Task.WhenAny(reading, Task.Delay(100, token));
                token.ThrowIfCancellationRequested();
                using var fresh = CaptureReadback.TryCapture(capture);
                if (fresh is null || !motion.MatchesBaseline(fresh, bounds))
                {
                    invalidate();
                    cancelled.Cancel();
                    try { await reading; }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    token.ThrowIfCancellationRequested();
                    return null;
                }
                if (reading.IsCompleted) return await reading;
            }
        }
        finally
        {
            // Never let an abandoned OCR task access a disposed source or the next frame's scanner caches.
            cancelled.Cancel();
            try { await reading; }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        }
    }
}
