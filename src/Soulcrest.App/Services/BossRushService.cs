using System.Text.Json;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public sealed record BossLoot(string Id, string En, string? De, string? Icon, string? Rarity)
{
    public string Name(string language) => language == "de" ? De ?? En : En;
    public string RarityStyle => Rarity switch
    {
        "common" or "rare" or "legend" or "unique" or "epic" or "special" => Rarity,
        _ => "unknown"
    };
    public string RarityLabel => UiText.T(RarityStyle switch
    {
        "common" => "Gewöhnlich",
        "rare" => "Selten",
        "legend" => "Legendär",
        "unique" => "Einzigartig",
        "epic" => "Episch",
        "special" => "Spezial",
        _ => "Seltenheit unbekannt"
    });
}

public sealed record BossPlace(int SpawnId, int NpcId, string En, string? De, double X, double Y, string? Icon = null, IReadOnlyList<BossLoot>? Loot = null)
{
    public string Name(string language) => language == "de" ? De ?? En : En;
}

public sealed record BossChoice(int MapId, string Map, BossPlace Boss)
{
    public string Key => $"{MapId}:{Boss.SpawnId}";
}
public sealed record BossRushEntry(BossChoice Place, bool Spawned, TimeSpan Remaining, TimeSpan Age, bool Cached = false);
public sealed record BossNotice(long Id, BossPlace Boss, bool Spawned, TimeSpan Remaining, DateTimeOffset Expires, DateTimeOffset CreatedAt);

public sealed record BossRushView(string? MapId, BossPlace? Boss, bool Spawned, TimeSpan Remaining, TimeSpan Age, int SpawnedCount, bool Cached = false);

/// <summary>One red target, oldest currently spawned boss first; otherwise earliest upcoming spawn.</summary>
public sealed class BossRushService : IDisposable
{
    private readonly object _gate = new();
    private readonly BossRushState _state;
    private readonly BossTimerCache _cache;
    private readonly SettingsService _settings;
    private readonly MapTargetsService _targets;
    private readonly NetworkLootService _network;
    private readonly Dictionary<int, (string Map, Dictionary<int, BossPlace> Places)> _maps = [];
    private readonly System.Threading.Timer _timer;
    private bool _disposed;
    private readonly TimeProvider _time;
    private readonly BossAlertPolicy _alertPolicy = new();
    private readonly List<BossNotice> _alerts = [];
    private long _noticeId;
    private sealed record BossMap(int MapId, List<BossPlace> Bosses);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public BossRushService(ProgressService progress, SettingsService settings, MapTargetsService targets, NetworkLootService network, TimeProvider? time = null, string? cachePath = null)
    {
        _settings = settings;
        _targets = targets;
        _network = network;
        _time = time ?? TimeProvider.System;
        _state = new BossRushState(_time);
        _cache = new BossTimerCache(cachePath ?? AppPaths.BossTimersFile, _time);
        if (progress.MapDataDirectory is { } directory)
        {
            foreach (var map in progress.Maps)
            {
                var path = Path.Combine(directory, map.Id, "bosses.json");
                if (!File.Exists(path)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<BossMap>(File.ReadAllText(path), Json) is { } data)
                        _maps[data.MapId] = (map.Id, data.Bosses.ToDictionary(b => b.SpawnId));
                }
                catch (Exception e) when (e is IOException or JsonException or ArgumentException)
                {
                    LogFile.Error("Boss Rush", e);
                }
            }
        }
        if (_cache.Load() is { } saved && KnownList(saved)) _state.Restore(saved);
        _network.BossMessageReceived += OnMessage;
        _network.BossSessionReset += Reset;
        _settings.Changed += Refresh;
        _timer = new System.Threading.Timer(_ => { if (_settings.Current.BossRushEnabled || _settings.Current.BossAlertsEnabled) Refresh(); }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public event Action? Changed;

    public IReadOnlyList<BossChoice> Catalogue => _maps.SelectMany(m => m.Value.Places.Values
        .Select(b => new BossChoice(m.Key, m.Value.Map, b))).ToArray();

    public IReadOnlyList<BossRushEntry> Schedule
    {
        get
        {
            lock (_gate)
            {
                var snapshot = _state.Snapshot;
                if (snapshot is null || !_maps.TryGetValue(snapshot.MapId, out var map)
                    || snapshot.Bosses.Any(b => !map.Places.ContainsKey(b.SpawnId))) return [];
                return snapshot.Bosses.OrderByDescending(b => b.Spawned).ThenBy(b => b.SpawnUnixMs).ThenBy(b => b.SpawnId)
                    .Select(b => new BossRushEntry(new(snapshot.MapId, map.Map, map.Places[b.SpawnId]), b.Spawned,
                        TimeSpan.FromMilliseconds(Math.Max(0, b.SpawnUnixMs - snapshot.ServerUnixMs)), snapshot.Age, snapshot.Cached)).ToArray();
            }
        }
    }

    public BossRushView View
    {
        get
        {
            var rows = Schedule;
            var next = rows.FirstOrDefault();
            return new(next?.Place.Map, next?.Place.Boss, next?.Spawned ?? false, next?.Remaining ?? TimeSpan.Zero,
                next?.Age ?? TimeSpan.Zero, rows.Count(b => b.Spawned), next?.Cached ?? false);
        }
    }

    public IReadOnlyList<BossRushEntry> OverlaySpawns => Schedule.Where(b => !b.Spawned)
        .Take(Math.Clamp(_settings.Current.BossOverlayCount, 1, 12)).ToArray();

    public IReadOnlyList<BossNotice> Alerts
    {
        get
        {
            lock (_gate) return _settings.Current.BossAlertsEnabled
                ? _alerts.Where(a => a.CreatedAt <= _time.GetUtcNow() && a.Expires > _time.GetUtcNow()).Take(3)
                    .Select(a => a with { Remaining = TimeSpan.FromTicks(Math.Max(0, (a.Remaining - (_time.GetUtcNow() - a.CreatedAt)).Ticks)) }).ToArray() : [];
        }
    }

    public void PreviewAlert()
    {
        lock (_gate)
        {
            if (!_settings.Current.BossAlertsEnabled) return;
            var boss = Catalogue.FirstOrDefault(b => _settings.Current.BossAlertIds.Contains(b.Key))?.Boss
                ?? Catalogue.FirstOrDefault()?.Boss;
            if (boss is not null) AddAlert(boss, false, TimeSpan.FromSeconds(_settings.Current.BossAlertLeadSeconds));
        }
        SafeEvent.Raise(Changed, "Boss Rush");
    }

    public static string Countdown(TimeSpan remaining) => $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";

    internal void OnMessage(string stream, byte[] message)
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            changed = _state.Accept(stream, message);
            if (changed && _state.Snapshot is { } snapshot && KnownList(snapshot)) _cache.Save(snapshot);
        }
        if (changed) Refresh();
    }

    private bool KnownList(BossRushSnapshot snapshot) => _maps.TryGetValue(snapshot.MapId, out var map)
        && snapshot.Bosses.All(b => map.Places.ContainsKey(b.SpawnId));

    private void Reset()
    {
        lock (_gate) { _state.Invalidate(); _alerts.Clear(); }
        Refresh();
    }

    private void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var settings = _settings.Current;
            var rows = Schedule;
            var active = settings.BossRushEnabled ? rows.FirstOrDefault(b => b.Spawned) : null;
            var upcoming = settings.BossRushEnabled ? rows.FirstOrDefault(b => !b.Spawned) : null;
            _targets.SetBossRush(Target(active, "boss-rush"));
            _targets.SetBossPreview(Target(upcoming, "boss-next") is { } preview
                ? preview with { After = active is not null ? "boss-rush" : null } : null);
            _alerts.RemoveAll(a => a.Expires <= _time.GetUtcNow());
            if (!settings.BossAlertsEnabled) _alerts.Clear();
            else if (rows.Count > 0 && _state.Snapshot is { } snapshot)
            {
                var selected = settings.BossAlertIds.ToHashSet(StringComparer.Ordinal);
                foreach (var trigger in _alertPolicy.Evaluate(snapshot, selected, settings.BossAlertLeadSeconds))
                    AddAlert(_maps[trigger.MapId].Places[trigger.Boss.SpawnId], trigger.Spawned,
                        TimeSpan.FromMilliseconds(Math.Max(0, trigger.Boss.SpawnUnixMs - snapshot.ServerUnixMs)));
            }
        }
        SafeEvent.Raise(Changed, "Boss Rush");
    }

    private MapTarget? Target(BossRushEntry? row, string id) => row is null ? null
        : new MapTarget(id, row.Place.Map, row.Place.Boss.X, row.Place.Boss.Y,
            row.Place.Boss.Name(_settings.Current.NameLanguage), "Boss Rush Mode", null, Color: "#ef4444");

    private void AddAlert(BossPlace boss, bool spawned, TimeSpan remaining)
    {
        _alerts.RemoveAll(a => a.Boss.SpawnId == boss.SpawnId); // confirmation replaces its advance warning
        // Three visible notices; further selected bosses keep their full display duration in the queue.
        var starts = _time.GetUtcNow();
        if (_alerts.Count >= 3 && _alerts[^3].Expires > starts) starts = _alerts[^3].Expires;
        var atDisplay = TimeSpan.FromTicks(Math.Max(0, (remaining - (starts - _time.GetUtcNow())).Ticks));
        _alerts.Add(new(++_noticeId, boss, spawned, atDisplay,
            starts.AddSeconds(Math.Clamp(_settings.Current.BossAlertDurationSeconds, 3, 60)), starts));
        if (_alerts.Count > 128) _alerts.RemoveAt(0);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _timer.Dispose();
        _network.BossMessageReceived -= OnMessage;
        _network.BossSessionReset -= Reset;
        _settings.Changed -= Refresh;
        _targets.SetBossRush(null);
        _targets.SetBossPreview(null);
    }
}
