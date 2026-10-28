namespace Soulcrest.App.Services;

public sealed record AbyssRankSoundOptions(bool Enabled = true, double Volume = .6);

/// <summary>A dedicated local channel for confirmed rank-up sequences; independent of alerts and drops.</summary>
public sealed class AbyssRankSoundPlayer : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Func<AbyssRankSoundOptions> _options;
    private readonly Func<IRareDropAudioSession> _createSession;
    private readonly Func<long, long, bool, byte[]> _createWave;
    private CancellationTokenSource? _request;
    private IRareDropAudioSession? _session;
    private AbyssRankSoundOptions _lastOptions;
    private long _revision;
    private bool _disposed;

    public AbyssRankSoundPlayer(Func<AbyssRankSoundOptions> options)
        : this(options, () => new RareDropAudioSession(TimeSpan.FromSeconds(12)), AbyssRankSound.CreateWave) { }

    internal AbyssRankSoundPlayer(Func<AbyssRankSoundOptions> options,
        Func<IRareDropAudioSession> createSession, Func<long, long, bool, byte[]> createWave)
    {
        _options = options;
        _createSession = createSession;
        _createWave = createWave;
        _lastOptions = Normalize(options());
    }

    /// <summary>Called once when the qualified visual sequence starts. Never plays for a descent.</summary>
    public void Start(long fromPoints, long toPoints, bool reduced) => _ = PlayAsync(fromPoints, toPoints, reduced);

    public Task<bool> PreviewAsync(bool reduced = false)
    {
        var stages = AbyssRankCatalog.Default.Stages;
        return PlayAsync(stages[8].Threshold, stages[9].Threshold, reduced);
    }

    internal Task<bool> PlayAsync(long fromPoints, long toPoints, bool reduced)
    {
        if (fromPoints < 0 || toPoints <= fromPoints
            || AbyssRankCatalog.Default.FindStage(fromPoints) == AbyssRankCatalog.Default.FindStage(toPoints))
            return Task.FromResult(false);
        CancellationTokenSource request;
        long revision;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);
            var options = Normalize(_options());
            if (!options.Enabled || options.Volume <= 0) return Task.FromResult(false);
            _request?.Cancel();
            request = new();
            _request = request;
            revision = ++_revision;
        }
        return Task.Run(() => RunAsync(fromPoints, toPoints, reduced, revision, request));
    }

    private async Task<bool> RunAsync(long fromPoints, long toPoints, bool reduced, long revision,
        CancellationTokenSource request)
    {
        var entered = false;
        IRareDropAudioSession? session = null;
        try
        {
            await _serial.WaitAsync(request.Token).ConfigureAwait(false);
            entered = true;
            request.Token.ThrowIfCancellationRequested();
            var wave = _createWave(fromPoints, toPoints, reduced);
            double volume;
            lock (_gate)
            {
                request.Token.ThrowIfCancellationRequested();
                var options = Normalize(_options());
                if (_disposed || revision != _revision || !options.Enabled || options.Volume <= 0) return false;
                volume = options.Volume;
                session = _createSession();
                _session = session;
            }
            await session.PlayAsync(wave, volume, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { LogFile.Error("Abyss rank-up audio", error); return false; }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_session, session)) _session = null;
                if (ReferenceEquals(_request, request)) _request = null;
            }
            DisposeSession(session);
            if (entered) _serial.Release();
            request.Dispose();
        }
    }

    public void ApplySettings()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var options = Normalize(_options());
            if (options == _lastOptions) return;
            _lastOptions = options;
        }
        Stop();
    }

    /// <summary>Invalidates pending synthesis as well as current playback on every context/visibility break.</summary>
    public void Stop()
    {
        IRareDropAudioSession? session;
        lock (_gate)
        {
            ++_revision;
            _request?.Cancel();
            session = _session;
            _session = null;
        }
        DisposeSession(session);
    }

    private static AbyssRankSoundOptions Normalize(AbyssRankSoundOptions options) => options with
    { Volume = double.IsFinite(options.Volume) ? Math.Clamp(options.Volume, 0, 1) : .6 };

    private static void DisposeSession(IRareDropAudioSession? session)
    {
        try { session?.Dispose(); }
        catch (Exception error) { LogFile.Error("Abyss rank audio cleanup", error); }
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        Stop();
    }
}
