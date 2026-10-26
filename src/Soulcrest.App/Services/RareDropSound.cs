using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Soulcrest.App.Services;

internal interface IRareDropAudioSession : IDisposable
{
    Task PlayAsync(byte[] wave, double volume, CancellationToken cancellation);
    // Existing test/custom sessions remain compatible; production implements local MP3 decoding below.
    Task PlayFileAsync(string path, double volume, CancellationToken cancellation) =>
        Task.FromException(new NotSupportedException("This drop audio session does not support local MP3 playback."));
}

/// <summary>Own Windows media channel: replacing a drop never stops the boss/event audio player.</summary>
internal sealed class RareDropAudioSession : IRareDropAudioSession
{
    private readonly object _gate = new();
    private readonly MediaPlayer _player = new() { AutoPlay = false, IsLoopingEnabled = false };
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InMemoryRandomAccessStream? _stream;
    private MediaSource? _source;
    private bool _disposed;

    internal RareDropAudioSession()
    {
        try
        {
            _player.CommandManager.IsEnabled = false;
            _player.MediaEnded += Ended;
            _player.MediaFailed += Failed;
        }
        catch { _player.Dispose(); throw; }
    }

    public async Task PlayAsync(byte[] wave, double volume, CancellationToken cancellation)
    {
        var stream = new InMemoryRandomAccessStream();
        try
        {
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(wave);
                await writer.StoreAsync().AsTask(cancellation).ConfigureAwait(false);
                writer.DetachStream();
            }
            stream.Seek(0);
            lock (_gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_disposed) throw new OperationCanceledException(cancellation);
                _stream = stream;
                _source = MediaSource.CreateFromStream(stream, "audio/wav");
                _player.Volume = RareDropAlertService.NormalizeVolume(volume);
                _player.Source = _source;
                _player.Play();
            }
            await _ended.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) if (!ReferenceEquals(_stream, stream)) stream.Dispose();
        }
    }

    public async Task PlayFileAsync(string path, double volume, CancellationToken cancellation)
    {
        if (!AlertsService.ValidSoundFile(path)) throw new ArgumentException("A local absolute MP3 path is required.", nameof(path));
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(cancellation).ConfigureAwait(false);
        lock (_gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_disposed) throw new OperationCanceledException(cancellation);
            _source = MediaSource.CreateFromStorageFile(file);
            _player.Volume = RareDropAlertService.NormalizeVolume(volume);
            _player.Source = _source;
            _player.Play();
        }
        // Do not retain an accidentally selected long music track indefinitely.
        await _ended.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
    }

    private void Ended(MediaPlayer sender, object args) => _ended.TrySetResult();
    private void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => _ended.TrySetException(
        new InvalidOperationException("Windows could not play the local drop sound.", args.ExtendedErrorCode));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _player.MediaEnded -= Ended; _player.MediaFailed -= Failed;
            _ended.TrySetCanceled();
            try { _player.Pause(); _player.Source = null; }
            catch (Exception error) { LogFile.Error("Rare drop audio stop", error); }
            try { _player.Dispose(); }
            catch (Exception error) { LogFile.Error("Rare drop audio player cleanup", error); }
            try { _source?.Dispose(); }
            catch (Exception error) { LogFile.Error("Rare drop audio source cleanup", error); }
            try { _stream?.Dispose(); }
            catch (Exception error) { LogFile.Error("Rare drop audio stream cleanup", error); }
            _source = null; _stream = null;
        }
    }
}

/// <summary>User-selected original Resonant cue with a shorter tail and reduced noise.</summary>
internal static class RareDropSound
{
    internal const int SampleRate = 48_000;
    internal const double Duration = 4.1;
    private static readonly Lazy<byte[]> Wave = new(LoadWave);
    internal static byte[] CreateWave() => Wave.Value;

    private static byte[] LoadWave()
    {
        // User request 2026-10-26: original Resonant, shorter end and less noise.
        using var stream = typeof(RareDropSound).Assembly.GetManifestResourceStream("Soulcrest.LootDropStandard.wav")
            ?? throw new InvalidOperationException("The embedded standard drop sound is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
