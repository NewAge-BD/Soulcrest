namespace Soulcrest.App.Services;

/// <summary>
/// When the notice about an unexpected UI error may appear (review 2026-10-07): one at a time, and the same
/// message at most once a minute. A recurring error (a timer throwing every 250 ms) stacked nested message
/// boxes before, because the box's own message loop ran the failing timer again. Every error is still logged.
/// </summary>
public static class ErrorNotice
{
    internal static readonly TimeSpan Repeat = TimeSpan.FromMinutes(1);
    private static readonly object Gate = new();
    private static bool _open;
    private static readonly Dictionary<string, DateTime> Shown = [];

    public static bool ShouldShow(string message, DateTime now)
    {
        lock (Gate)
        {
            if (_open || (Shown.TryGetValue(message, out var last) && now - last < Repeat))
                return false;
            _open = true;
            Shown[message] = now;
            return true;
        }
    }

    public static void Closed()
    {
        lock (Gate)
            _open = false;
    }
}
