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
    public static void Watch(Control window, Func<string> context, CancellationToken stop)
    {
        var thread = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var answered = new ManualResetEventSlim();
                var started = Stopwatch.GetTimestamp();
                try { window.BeginInvoke(answered.Set); }
                catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { return; }
                if (!answered.Wait(1000, stop) && !stop.IsCancellationRequested)
                {
                    var foreground = ForegroundProcess();
                    while (!answered.Wait(250, stop) && !stop.IsCancellationRequested) { }
                    if (!stop.IsCancellationRequested)
                        Record("Programmfenster", Stopwatch.GetElapsedTime(started), $"{context()}, Vordergrund beim Beginn: {foreground}");
                }
                if (stop.WaitHandle.WaitOne(500))
                    return;
            }
        })
        { IsBackground = true, Name = "Soulcrest UI watchdog" };
        thread.Start();
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
