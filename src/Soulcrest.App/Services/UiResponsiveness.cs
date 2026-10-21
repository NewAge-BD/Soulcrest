using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Soulcrest.App.Services;

/// <summary>A moment Soulcrest did not respond: the window thread or the map page (WebView).</summary>
public sealed record UiStall(DateTime At, string Part, TimeSpan Duration, string Context);

/// <summary>
/// Records stalls of the window thread and slow answers of the map page for the diagnosis report
/// (user report 2026-10-05: Soulcrest froze when clicked after the game had the mouse). Keeps the last 20.
/// </summary>
public static class UiResponsiveness
{
    private static readonly object Gate = new();
    private static readonly Queue<UiStall> Stalls = new();

    public static IReadOnlyList<UiStall> Recent
    {
        get { lock (Gate) return Stalls.ToArray(); }
    }

    public static void Record(string part, TimeSpan duration, string context)
    {
        lock (Gate)
        {
            Stalls.Enqueue(new UiStall(DateTime.Now, part, duration, context));
            while (Stalls.Count > 20)
                Stalls.Dequeue();
        }
        Trace.TraceWarning("Soulcrest stalled: {0} {1:0} ms ({2})", part, duration.TotalMilliseconds, context);
    }

    /// <summary>
    /// Watches the window thread: it must answer a posted message within a second. A stall is recorded
    /// with its full length once the thread answers again, together with the window that had the focus
    /// when it began.
    /// </summary>
    public static IDisposable Watch(Control window, Func<string> context) =>
        new Watchdog(callback => window.BeginInvoke(callback), context);

    /// <summary>Owns cancellation until the worker has finished; stopping never waits for the UI thread.</summary>
    internal sealed class Watchdog : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private int _disposed;
        internal Task Completion { get; }

        internal Watchdog(Action<Action> post, Func<string> context,
            Func<Task, CancellationToken, Task<bool>>? waitForResponse = null,
            Func<CancellationToken, Task>? pause = null, Action<TimeSpan, string>? record = null,
            Func<string>? foreground = null)
        {
            Completion = Task.Run(() => WatchAsync(post, context, _stop.Token,
                waitForResponse, pause, record, foreground));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _ = Completion.ContinueWith(completed =>
            {
                if (completed.IsFaulted) Trace.TraceWarning("UI watchdog stopped: {0}", completed.Exception);
                _stop.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal static async Task WatchAsync(Action<Action> post, Func<string> context, CancellationToken stop,
        Func<Task, CancellationToken, Task<bool>>? waitForResponse = null,
        Func<CancellationToken, Task>? pause = null, Action<TimeSpan, string>? record = null,
        Func<string>? foreground = null)
    {
        waitForResponse ??= WaitForResponse;
        pause ??= token => Task.Delay(500, token);
        record ??= (duration, state) => Record("Programmfenster", duration, state);
        foreground ??= ForegroundProcess;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // A queued callback may outlive cancellation or the window. A TCS has no disposed
                // wait handle, so a late or repeated acknowledgement remains harmless.
                var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var started = Stopwatch.GetTimestamp();
                try { post(() => answered.TrySetResult()); }
                catch (InvalidOperationException) { return; }
                if (!await waitForResponse(answered.Task, stop).ConfigureAwait(false))
                {
                    var foregroundAtStart = foreground();
                    await answered.Task.WaitAsync(stop).ConfigureAwait(false);
                    stop.ThrowIfCancellationRequested();
                    record(Stopwatch.GetElapsedTime(started), $"{context()}, Vordergrund beim Beginn: {foregroundAtStart}");
                }
                await pause(stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private static async Task<bool> WaitForResponse(Task answered, CancellationToken stop)
    {
        try { await answered.WaitAsync(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    internal static string ForegroundProcess()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == 0)
                return "–";
            _ = GetWindowThreadProcessId(window, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "unbekannt";
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
