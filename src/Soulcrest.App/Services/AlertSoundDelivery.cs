namespace Soulcrest.App.Services;

/// <summary>Automatic alert audio must not depend on a window handle or an available UI message queue.</summary>
internal sealed class AlertSoundDelivery : IDisposable
{
    private readonly object _gate = new();
    private readonly AlertsService _alerts;
    private readonly Action<string, string?> _play;
    private readonly Func<bool> _suppressed;
    private bool _disposed;

    public AlertSoundDelivery(AlertsService alerts, AlertSoundPlayer sounds)
        : this(alerts, (sound, file) => { _ = sounds.Play(sound, file); }, () => SmokeTest.Enabled) { }

    internal AlertSoundDelivery(AlertsService alerts, Action<string, string?> play, Func<bool> suppressed)
    {
        _alerts = alerts;
        _play = play;
        _suppressed = suppressed;
        _alerts.NoticeRaised += Deliver;
    }

    private void Deliver(AlertNotice notice)
    {
        lock (_gate)
        {
            if (_disposed || _suppressed() || !notice.PlaySound || notice.Expires <= _alerts.Now) return;
            // AlertSoundPlayer already schedules playback on its own worker; never post it through a form.
            _play(notice.Sound, notice.SoundFile);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _alerts.NoticeRaised -= Deliver;
        }
    }
}
