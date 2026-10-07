namespace Soulcrest.App.Services;

/// <summary>Passive network loot tracking and per-session totals.</summary>
public sealed class TrackerService(SettingsService settings, Network.INetworkLootSource? network = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _session = new(StringComparer.Ordinal);
    public bool Running => network?.Running ?? false;
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
        if (Running || network is null) return;
        lock (_gate) _session.Clear();
        network.Changed -= OnChanged;
        network.Changed += OnChanged;
        network.SoulGained -= OnSoul;
        network.SoulGained += OnSoul;
        network.Start();
        Changed?.Invoke();
    }
    public void Stop()
    {
        settings.Update(s => s.LootTrackingEnabled = false);
        network?.Stop();
        Changed?.Invoke();
    }
    private void OnChanged() => Changed?.Invoke();
    private void OnSoul(Network.NetworkSoul soul)
    {
        lock (_gate) _session[soul.PetId] = _session.GetValueOrDefault(soul.PetId) + soul.Souls;
        Changed?.Invoke();
    }
    public void Dispose()
    {
        if (network is null) return;
        network.Changed -= OnChanged;
        network.SoulGained -= OnSoul;
        network.Stop(); // Shutdown must preserve the user's enabled preference.
    }
}
