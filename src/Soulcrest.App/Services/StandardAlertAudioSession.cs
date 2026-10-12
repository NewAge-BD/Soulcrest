using System.Media;

namespace Soulcrest.App.Services;

/// <summary>Original short double tone, embedded for portable and single-file builds.</summary>
internal sealed class StandardAlertAudioSession : IAlertAudioSession
{
    private readonly object _gate = new();
    private Stream? _stream;
    private SoundPlayer? _player;
    private bool _disposed;
    public event Action<Exception>? Failed { add { } remove { } }
    public Task StartAsync(string path, CancellationToken cancellation)
    {
        lock (_gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_disposed) throw new OperationCanceledException(cancellation);
            _stream = typeof(StandardAlertAudioSession).Assembly.GetManifestResourceStream("Soulcrest.AlertStandard.wav")
                ?? throw new InvalidOperationException("Embedded alert audio is missing.");
            _player = new SoundPlayer(_stream);
            _player.Load();
            cancellation.ThrowIfCancellationRequested();
            _player.Play();
        }
        return Task.CompletedTask;
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _player?.Stop(); _player?.Dispose(); _stream?.Dispose();
            _player = null; _stream = null;
        }
    }
}
