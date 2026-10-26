namespace Soulcrest.App.Services;

public sealed record RareDropAlertOptions(bool Enabled = true, bool SoundEnabled = true,
    bool AnimationsEnabled = true, double Volume = .6)
{
    public IReadOnlyDictionary<LootItemRarity, DropRarityAlertSettings>? RarityAlerts { get; init; }
    public DropRarityAlertSettings ForRarity(LootItemRarity rarity) => DropRarityAlertSettings.Normalize(
        RarityAlerts is not null && RarityAlerts.TryGetValue(rarity, out var value) ? value : null);
}

public sealed record RareDropNotice(string Id, string Name, long Quantity, LootItemRarity Rarity,
    string? IconPath, DateTimeOffset StartedAt)
{
    public double ElapsedSeconds { get; init; }
}

/// <summary>Ephemeral receipt notifications only. No journal replay, price change or snapshot creates an alert.</summary>
public sealed class RareDropAlertService : IDisposable
{
    public const double DurationSeconds = 4;
    public const int MaximumVisibleNotices = 3;
    private readonly object _gate = new();
    private readonly Func<RareDropAlertOptions> _options;
    private readonly Func<IRareDropAudioSession> _audioFactory;
    private readonly TimeProvider _time;
    private readonly Func<string, bool> _soundFileExists;
    private readonly FarmLootTrackerService? _source;
    private readonly LifeskillTrackerService? _lifeskillSource;
    private IRareDropAudioSession? _audio;
    private CancellationTokenSource? _audioRequest;
    private PlaybackPlan? _audioPlan;
    private readonly List<(RareDropNotice Notice, long Stamp)> _notices = [];
    private bool _visible, _disposed;
    private long _revision;
    private string? _status;
    private sealed record PlaybackPlan(LootItemRarity Rarity, DropRarityAlertSettings Settings, double Volume, bool Preview);

    public RareDropAlertService(FarmLootTrackerService source, Func<RareDropAlertOptions> options,
        LifeskillTrackerService? lifeskillSource = null)
        : this(options, () => new RareDropAudioSession(), TimeProvider.System, source, lifeskillSource) { }

    internal RareDropAlertService(Func<RareDropAlertOptions> options, Func<IRareDropAudioSession> audioFactory, TimeProvider time,
        FarmLootTrackerService? source = null, LifeskillTrackerService? lifeskillSource = null,
        Func<string, bool>? soundFileExists = null)
    {
        _options = options; _audioFactory = audioFactory; _time = time;
        _source = source; _lifeskillSource = lifeskillSource;
        _soundFileExists = soundFileExists ?? File.Exists;
        if (_source is not null) _source.DropAccepted += Accept;
        if (_lifeskillSource is not null) _lifeskillSource.DropAccepted += Accept;
    }

    public event Action? Changed;
    public string? Status { get { lock (_gate) return _status; } }

    public RareDropNotice? Current => CurrentNotices.LastOrDefault();

    public IReadOnlyList<RareDropNotice> CurrentNotices
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || !_visible || !_options().Enabled) return [];
                var options = _options();
                var now = _time.GetTimestamp();
                _notices.RemoveAll(entry => !options.ForRarity(entry.Notice.Rarity).Enabled ||
                    _time.GetElapsedTime(entry.Stamp, now).TotalSeconds >= DurationSeconds);
                return _notices.Select(entry => entry.Notice with
                    { ElapsedSeconds = Math.Max(0, _time.GetElapsedTime(entry.Stamp, now).TotalSeconds) }).ToArray();
            }
        }
    }

    public void SetPresentationVisible(bool visible)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _visible = visible;
            if (!visible)
            {
                _notices.Clear();
                // An explicit settings preview has no dependency on overlay visibility.
                if (_audioPlan?.Preview != true) StopAudio();
            }
        }
    }

    public void ApplySettings()
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            var options = _options();
            if (!options.Enabled && _notices.Count != 0) { _notices.Clear(); changed = true; }
            if (_notices.RemoveAll(entry => !options.ForRarity(entry.Notice.Rarity).Enabled) != 0) changed = true;
            if (_audioPlan is { } plan && !PlaybackAllowed(plan, options))
            { StopAudio(); _status = null; changed = true; }
        }
        if (changed) SafeEvent.Raise(Changed, "Rare drop settings");
    }

    internal void Accept(AcceptedFarmLootDrop drop)
    {
        var rarity = MonsterLootItemCatalog.Default.DropRarity(drop.LootKey);
        if (drop.Quantity <= 0 || !MonsterLootItemCatalog.IsExceptional(rarity)) return;
        lock (_gate)
        {
            if (_disposed || !_visible) return;
            var options = _options();
            var tier = options.ForRarity(rarity);
            if (!options.Enabled || !tier.Enabled) return;
            // The source emits once after durable acceptance. Equal-looking receipts can be real repeats.
            // At most three simultaneous notices, each expiring from its actual receipt. Never queue delayed alerts.
            if (_notices.Count == MaximumVisibleNotices) _notices.RemoveAt(0);
            _notices.Add((new(Guid.NewGuid().ToString("N"), drop.Name, drop.Quantity, rarity,
                MonsterLootIconCatalog.Default.DropAsset(drop.LootKey), _time.GetUtcNow())
            {
            }, _time.GetTimestamp()));
            StopAudio();
            if (options.SoundEnabled && tier.SoundEnabled && NormalizeVolume(options.Volume) > 0)
                _ = StartAudio(new(rarity, tier, NormalizeVolume(options.Volume), Preview: false));
        }
        SafeEvent.Raise(Changed, "Rare drop receipt");
    }

    /// <summary>Explicit preview only: no fake drop, receipt, notice or statistic is produced.</summary>
    public Task<bool> PreviewAsync(LootItemRarity rarity)
    {
        if (!DropRarityAlertSettings.Supports(rarity)) throw new ArgumentOutOfRangeException(nameof(rarity));
        Task<bool> playback;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);
            var options = _options();
            var plan = new PlaybackPlan(rarity, options.ForRarity(rarity), NormalizeVolume(options.Volume), Preview: true);
            StopAudio();
            if (!PlaybackAllowed(plan, options))
            { _status = "Drop-Ton ist deaktiviert."; playback = Task.FromResult(false); }
            else
            { _status = "Drop-Ton wird wiedergegeben."; playback = StartAudio(plan); }
        }
        SafeEvent.Raise(Changed, "Rare drop preview");
        return playback;
    }

    // Called under _gate; a newer receipt/preview always replaces older drop audio on its own channel.
    private Task<bool> StartAudio(PlaybackPlan plan)
    {
        var request = new CancellationTokenSource();
        _audioRequest = request; _audioPlan = plan;
        var revision = ++_revision;
        return Task.Run(() => PlayAsync(revision, plan, request));
    }

    private static bool PlaybackAllowed(PlaybackPlan plan, RareDropAlertOptions options) =>
        options.Enabled && options.SoundEnabled && NormalizeVolume(options.Volume) > 0 &&
        NormalizeVolume(options.Volume) == plan.Volume && options.ForRarity(plan.Rarity) == plan.Settings &&
        plan.Settings.Enabled && plan.Settings.SoundEnabled;

    private IRareDropAudioSession CreateAudio(long revision, PlaybackPlan plan, CancellationTokenSource request)
    {
        lock (_gate)
        {
            if (_disposed || (!plan.Preview && !_visible) || revision != _revision || request.IsCancellationRequested ||
                !PlaybackAllowed(plan, _options())) throw new OperationCanceledException(request.Token);
            var audio = _audioFactory(); _audio = audio; return audio;
        }
    }

    private async Task<bool> PlayAsync(long revision, PlaybackPlan plan, CancellationTokenSource request)
    {
        IRareDropAudioSession? audio = null;
        string? fallback = null;
        try
        {
            if (plan.Settings.Sound == "custom")
            {
                var file = plan.Settings.SoundFile;
                if (!AlertsService.ValidSoundFile(file) || !_soundFileExists(file!))
                    fallback = "Eigene MP3 nicht verfügbar. Standardton wird verwendet.";
                else
                {
                    try
                    {
                        audio = CreateAudio(revision, plan, request);
                        await audio.PlayFileAsync(Path.GetFullPath(file!), plan.Volume, request.Token).ConfigureAwait(false);
                        SetStatus("Drop-Ton abgespielt.", revision);
                        return true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        LogFile.Error("Rare drop custom audio", error);
                        lock (_gate)
                        {
                            if (ReferenceEquals(_audio, audio)) _audio = null;
                            DisposeAudio(audio); audio = null;
                        }
                        fallback = "Eigene MP3 konnte nicht abgespielt werden. Standardton wird verwendet.";
                    }
                }
            }
            if (fallback is not null) SetStatus(fallback, revision);
            audio = CreateAudio(revision, plan, request);
            await audio.PlayAsync(RareDropSound.CreateWave(), plan.Volume, request.Token).ConfigureAwait(false);
            SetStatus(fallback ?? "Drop-Ton abgespielt.", revision);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error)
        {
            LogFile.Error("Rare drop audio", error);
            SetStatus("Drop-Ton konnte nicht abgespielt werden.", revision);
            return false;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_audio, audio)) _audio = null;
                if (ReferenceEquals(_audioRequest, request)) { _audioRequest = null; _audioPlan = null; }
                DisposeAudio(audio);
            }
            request.Dispose();
        }
    }

    private void SetStatus(string status, long revision)
    {
        lock (_gate)
        {
            if (_disposed || revision != _revision) return;
            _status = status;
        }
        SafeEvent.Raise(Changed, "Rare drop audio");
    }

    private void StopAudio()
    {
        ++_revision;
        _audioRequest?.Cancel();
        _audioRequest = null;
        _audioPlan = null;
        DisposeAudio(_audio);
        _audio = null;
    }

    private static void DisposeAudio(IRareDropAudioSession? audio)
    {
        try { audio?.Dispose(); }
        catch (Exception error) { LogFile.Error("Rare drop audio cleanup", error); }
    }

    internal static double NormalizeVolume(double volume) => double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : .6;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _notices.Clear(); StopAudio();
        }
        if (_source is not null) _source.DropAccepted -= Accept;
        if (_lifeskillSource is not null) _lifeskillSource.DropAccepted -= Accept;
        GC.SuppressFinalize(this);
    }
}
