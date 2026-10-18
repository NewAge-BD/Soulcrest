namespace Soulcrest.App.Services;

/// <summary>Passive loot consumer and per-session totals, sharing the source with combat.</summary>
public sealed class TrackerService(SettingsService settings, Network.INetworkLootSource? network = null,
    NetworkCaptureCoordinator? capture = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _session = new(StringComparer.Ordinal);
    private readonly NetworkCaptureCoordinator? _capture = capture ?? (network is null ? null : new(network));
    private bool _requested;
    public bool Running { get { lock (_gate) return _requested && (network?.Running ?? false); } }
    public string Status => network?.Status ?? "Netzwerk-Quelle nicht verfügbar.";
    public bool StatusIsError => network?.StatusIsError ?? true;
    public Network.INetworkLootSource? Network => network;
    public event Action? Changed;
    public IReadOnlyDictionary<string, int> SessionTotals
    {
        get { lock (_gate) return new Dictionary<string, int>(_session); }
    }
    public void StartIfEnabled()
    {
        if (settings.Current.LootTrackingEnabled) StartCore();
    }
    public void Start()
    {
        settings.Update(s => s.LootTrackingEnabled = true);
        StartCore();
    }
    private void StartCore()
    {
        if (network is null) return;
        lock (_gate)
        {
            if (!_requested) _session.Clear();
            _requested = true;
        }
        network.Changed -= OnChanged;
        network.Changed += OnChanged;
        network.SoulGained -= OnSoul;
        network.SoulGained += OnSoul;
        _capture!.SetDemand(NetworkCaptureConsumer.Loot, true);
        Changed?.Invoke();
    }
    public void Stop()
    {
        settings.Update(s => s.LootTrackingEnabled = false);
        lock (_gate) _requested = false;
        _capture?.SetDemand(NetworkCaptureConsumer.Loot, false);
        Changed?.Invoke();
    }
    private void OnChanged() => Changed?.Invoke();
    private void OnSoul(Network.NetworkSoul soul)
    {
        lock (_gate)
        {
            if (!_requested) return;
            _session[soul.PetId] = _session.GetValueOrDefault(soul.PetId) + soul.Souls;
        }
        Changed?.Invoke();
    }
    public void Dispose()
    {
        lock (_gate) _requested = false;
        if (network is null) return;
        network.Changed -= OnChanged;
        network.SoulGained -= OnSoul;
        _capture?.SetDemand(NetworkCaptureConsumer.Loot, false); // Preserve the user's preference on shutdown.
    }
}
