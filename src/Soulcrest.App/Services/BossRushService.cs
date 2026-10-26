using System.Globalization;
using System.Text.Json;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;
using Soulcrest.Core.Alerts;

namespace Soulcrest.App.Services;

public sealed record BossLoot(string Id, string En, string? De, string? Icon, string? Rarity)
{
    public string Name(string language) => language == "de" ? De ?? En : En;
    public string RarityStyle => Rarity switch
    {
        "common" or "rare" or "legend" or "unique" or "epic" or "special" => Rarity,
        _ => "unknown"
    };
    public int RarityRank => RarityStyle switch
    {
        "common" => 0,
        "rare" => 1,
        "legend" => 2,
        "unique" => 3,
        "epic" => 4,
        "special" => 5,
        _ => -1
    };
    public static IOrderedEnumerable<BossLoot> OrderByRarity(IEnumerable<BossLoot> items, string language)
    {
        var names = StringComparer.Create(CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US"), ignoreCase: true);
        return items.OrderByDescending(item => item.RarityRank)
            .ThenBy(item => item.Name(language), names)
            .ThenBy(item => item.Id, StringComparer.Ordinal);
    }
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

public sealed record BossPlace(int SpawnId, int NpcId, string En, string? De, double? X, double? Y, string? Icon = null, IReadOnlyList<BossLoot>? Loot = null, AlertSchedule? CalendarSchedule = null)
{
    public string Name(string language) => language == "de" ? De ?? En : En;
}

public sealed record BossChoice(int MapId, string Map, BossPlace Boss)
{
    public string Key => $"{MapId}:{Boss.SpawnId}";
}
/// <param name="SpawnElapsed">Elapsed server time since the actual spawn; unrelated to list observation Age.</param>
public sealed record BossRushEntry(BossChoice Place, bool Spawned, TimeSpan Remaining, TimeSpan Age, bool Cached = false, bool Calendar = false, TimeSpan? SpawnElapsed = null);
public sealed record BossNotice(long Id, BossPlace Boss, bool Spawned, TimeSpan Remaining, DateTimeOffset Expires, DateTimeOffset CreatedAt, string? Key = null, bool Preview = false, long? SpawnUnixMs = null, bool Calendar = false, bool UntilSpawn = false, DateTimeOffset? PlannedAt = null, long? HoldTimestamp = null, int LeadSeconds = 0);

public sealed record BossRushView(string? MapId, BossPlace? Boss, bool Spawned, TimeSpan Remaining, TimeSpan Age, int SpawnedCount, bool Cached = false);

/// <summary>Confirmed spawned bosses and chronological upcoming navigation from the current native list.</summary>
public sealed class BossRushService : IDisposable
{
    // User request 2026-10-26: spawned navigation markers are visible through the first minute only.
    internal static readonly TimeSpan SpawnMarkerLifetime = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly BossRushState _state;
    private readonly BossTimerCache _cache;
    private readonly Dictionary<int, BossRushState> _savedSchedules = [];
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
        foreach (var saved in _cache.LoadAll().Where(KnownList)) RetainSchedule(saved);
        if (_cache.Load() is { } latest && KnownList(latest)) _state.Restore(latest);
        _network.BossMessageReceived += OnMessage;
        _network.BossSessionReset += Reset;
        _network.BossStreamGap += InvalidateStream;
        _settings.Changed += Refresh;
        _timer = new System.Threading.Timer(_ => { if (_settings.Current.BossRushEnabled || _settings.Current.BossAlertsEnabled || _maps.Values.Any(map => map.Places.Values.Any(boss => boss.CalendarSchedule is not null))) Refresh(); }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public event Action? Changed;

    public IReadOnlyList<BossChoice> Catalogue => _maps.SelectMany(m => m.Value.Places.Values
        .Select(b => new BossChoice(m.Key, m.Value.Map, b))).ToArray();

    /// <summary>Captured maps and verified calendar fallbacks; only the current native list is a live observation.</summary>
    public IReadOnlyList<BossRushEntry> Schedule
    {
        get
        {
            lock (_gate)
            {
                var rows = CapturedSchedule;
                return rows.Concat(BossCalendarTimers.Create(Catalogue, rows, _time.GetUtcNow())).OrderByDescending(row => row.Spawned).ThenBy(row => row.Remaining)
                    .ThenBy(row => row.Place.MapId).ThenBy(row => row.Place.Boss.SpawnId).ToArray();
            }
        }
    }

    private IReadOnlyList<BossRushEntry> CapturedSchedule
    {
        get
        {
            var current = _state.Snapshot;
            // An unknown full list cannot expose a known subset or revive the same map's old cache.
            return _savedSchedules.Where(pair => pair.Key != current?.MapId)
                .SelectMany(pair => Entries(pair.Value.Snapshot)).Concat(Entries(current)).ToArray();
        }
    }

    // Navigation and spawn-overlay choices keep their existing current-map semantics. Historic maps
    // are available to the timer list without becoming routes, next targets, or alert sources.
    private IReadOnlyList<BossRushEntry> CurrentSchedule => Entries(_state.Snapshot);

    private IReadOnlyList<BossRushEntry> Entries(BossRushSnapshot? snapshot)
    {
        if (snapshot is null || !KnownList(snapshot)) return [];
        var map = _maps[snapshot.MapId];
        // Validate every ID before projecting only known times. A wire-zero record must neither
        // hide another boss's timer nor become an expired countdown, route, or spawn confirmation.
        return snapshot.Bosses.Where(boss => boss.SpawnUnixMs.HasValue)
            .OrderByDescending(boss => boss.Spawned).ThenBy(boss => boss.SpawnUnixMs).ThenBy(boss => boss.SpawnId)
            .Select(boss => new BossRushEntry(new(snapshot.MapId, map.Map, map.Places[boss.SpawnId]), boss.Spawned,
                TimeSpan.FromMilliseconds(Math.Max(0, boss.SpawnUnixMs!.Value - snapshot.ServerUnixMs)), snapshot.Age, snapshot.Cached,
                SpawnElapsed: boss.Spawned && snapshot.ServerUnixMs >= boss.SpawnUnixMs.Value
                    ? TimeSpan.FromMilliseconds(snapshot.ServerUnixMs - boss.SpawnUnixMs.Value) : null)).ToArray();
    }

    private void RetainSchedule(BossRushSnapshot snapshot)
    {
        if (!KnownList(snapshot)) return;
        if (!_savedSchedules.TryGetValue(snapshot.MapId, out var saved))
            _savedSchedules[snapshot.MapId] = saved = new BossRushState(_time);
        saved.Restore(snapshot); // retained maps advance only by the monotonic in-session clock
    }

    public BossRushView View
    {
        get
        {
            lock (_gate)
            {
                var rows = CurrentSchedule;
                var next = rows.FirstOrDefault();
                return new(next?.Place.Map, next?.Place.Boss, next?.Spawned ?? false, next?.Remaining ?? TimeSpan.Zero,
                    next?.Age ?? TimeSpan.Zero, rows.Count(boss => boss.Spawned), next?.Cached ?? false);
            }
        }
    }

    public IReadOnlyList<BossRushEntry> OverlaySpawns
    {
        get
        {
            lock (_gate) return CurrentSchedule.Where(boss => !boss.Spawned)
                .Take(Math.Clamp(_settings.Current.BossOverlayCount, 1, 12)).ToArray();
        }
    }

    public IReadOnlyList<BossNotice> Alerts
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                RefreshUntilSpawnAlerts(now);
                if (!_settings.Current.BossAlertsEnabled) return [];
                var alive = _alerts.Where(a => (a.UntilSpawn || a.CreatedAt <= now) && (a.UntilSpawn || a.Expires > now) &&
                    (a.Preview || a.Key is { } key && IsReminderSelected(key))).ToArray();
                var fixedIds = alive.Where(notice => !notice.UntilSpawn).Take(3).Select(notice => notice.Id).ToHashSet();
                // Held warnings do not occupy the fixed-duration queue. The existing banner overflow
                // presents all held sources and up to three fixed notices without delaying new warnings forever.
                return alive.Where(notice => notice.UntilSpawn || fixedIds.Contains(notice.Id))
                    .Select(a => AtDisplay(a, now)).Where(a => !a.UntilSpawn || a.Remaining > TimeSpan.Zero).ToArray();
            }
        }
    }

    /// <summary>The saved per-boss wish, inheriting the existing global lead when no override exists.</summary>
    public BossReminderPreference GetReminder(string key)
    {
        var settings = _settings.Current;
        var preference = settings.BossReminderOverrides.TryGetValue(key, out var saved) ? saved
            : new BossReminderPreference { LeadSeconds = settings.BossAlertLeadSeconds, LeadSecondsList = settings.BossAlertLeadSecondsList };
        return preference with { LeadSeconds = Math.Clamp(preference.LeadSeconds, 0, 3600), LeadSecondsList = preference.LeadSecondsList is null ? null : preference.WarningSeconds };
    }

    /// <summary>Changes only this boss; selecting a timer or enabling the global master is a separate action.</summary>
    public bool SetReminder(string key, BossReminderPreference preference)
    {
        if (_disposed || preference is null || preference.LeadSeconds is < 0 or > 3600 ||
            !Soulcrest.Core.Alerts.AdvanceWarnings.Valid(preference.LeadSecondsList, 3600) ||
            !Catalogue.Any(choice => choice.Key == key)) return false;
        Dictionary<string, BossReminderPreference>? previous = null, updated = null;
        try
        {
            // Merge inside Settings.Update's lock so simultaneous changes from the two tabs
            // preserve each other's independently configured bosses.
            _settings.Update(settings =>
            {
                previous = settings.BossReminderOverrides;
                updated = new Dictionary<string, BossReminderPreference>(previous, StringComparer.Ordinal) { [key] = preference with { LeadSecondsList = preference.LeadSecondsList is null ? null : preference.WarningSeconds } };
                settings.BossReminderOverrides = updated;
            });
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Settings.Update changes Current before saving. Restore this attempted replacement on failure.
            if (previous is not null && ReferenceEquals(_settings.Current.BossReminderOverrides, updated)) _settings.Current.BossReminderOverrides = previous;
            LogFile.Error("Boss reminder", error);
            return false;
        }
    }

    private bool IsReminderSelected(string key) => (_settings.Current.BossAlertIds ?? []).Contains(key, StringComparer.Ordinal)
        && GetReminder(key).Enabled;

    public void PreviewAlert() => PreviewAlert(null);

    public void PreviewAlert(string? key)
    {
        lock (_gate)
        {
            if (!_settings.Current.BossAlertsEnabled) return;
            var choice = key is not null ? Catalogue.FirstOrDefault(b => b.Key == key)
                : Catalogue.FirstOrDefault(b => _settings.Current.BossAlertIds.Contains(b.Key)) ?? Catalogue.FirstOrDefault();
            if (choice is not null) AddAlert(choice.Boss, false, TimeSpan.FromSeconds(GetReminder(choice.Key).WarningSeconds.FirstOrDefault()), choice.Key, preview: true);
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
            var previous = _state.Snapshot;
            changed = _state.Accept(stream, message);
            if (changed)
            {
                if (previous is not null) RetainSchedule(previous);
                if (_state.Snapshot is { } snapshot && KnownList(snapshot))
                {
                    ConfirmHeldAlerts(snapshot);
                    RetainSchedule(snapshot);
                    _cache.Save(snapshot);
                }
            }
        }
        if (changed) Refresh();
    }

    private bool KnownList(BossRushSnapshot snapshot) => _maps.TryGetValue(snapshot.MapId, out var map)
        && snapshot.Bosses.All(b => map.Places.ContainsKey(b.SpawnId));

    private void InvalidateStream(string stream)
    {
        lock (_gate)
        {
            if (!_state.Invalidate(stream)) return;
            // Invalidation prevents new policy triggers; already authorized notifications retain
            // their fixed expiry or the remaining countdown of their original warning cycle.
        }
        Refresh();
    }

    private void Reset()
    {
        lock (_gate) _state.Invalidate(); // Existing notifications keep their independently chosen lifetime.
        Refresh();
    }

    private void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var settings = _settings.Current;
            var rows = CurrentSchedule;
            _targets.SetBossNavigationTargets(settings.BossRushEnabled
                ? NavigationTargets(rows, settings.BossNavigationPreviewCount, settings.NameLanguage, settings.BossRushShowOlderSpawns) : []);
            RefreshUntilSpawnAlerts(_time.GetUtcNow());
            _alerts.RemoveAll(a => !a.UntilSpawn && a.Expires <= _time.GetUtcNow() ||
                !a.Preview && (a.Key is not { } key || !IsReminderSelected(key)));
            if (!settings.BossAlertsEnabled) _alerts.Clear();
            else if (rows.Count > 0 && _state.Snapshot is { } snapshot)
            {
                var selected = settings.BossAlertIds.ToHashSet(StringComparer.Ordinal);
                foreach (var trigger in _alertPolicy.EvaluateMany(snapshot, selected,
                    key => GetReminder(key) is { Enabled: true } preference ? preference.WarningSeconds : null))
                    if (trigger.Boss.SpawnUnixMs is { } at)
                        AddAlert(_maps[trigger.MapId].Places[trigger.Boss.SpawnId], trigger.Spawned,
                            TimeSpan.FromMilliseconds(Math.Max(0, at - snapshot.ServerUnixMs)), $"{trigger.MapId}:{trigger.Boss.SpawnId}", spawnUnixMs: at, leadSeconds: trigger.LeadSeconds);
            }
            if (settings.BossAlertsEnabled)
            {
                // Calendar rules supply advance warnings only. They never confirm a spawn,
                // enter the network cache, or change the network-driven navigation target.
                var now = _time.GetUtcNow();
                var selected = settings.BossAlertIds.ToHashSet(StringComparer.Ordinal);
                foreach (var calendar in BossCalendarTimers.Create(Catalogue, CapturedSchedule, now))
                {
                    var at = (now + calendar.Remaining).ToUnixTimeMilliseconds();
                    var snapshot = new BossRushSnapshot(calendar.Place.MapId,
                        [new(calendar.Place.Boss.SpawnId, false, at)], now.ToUnixTimeMilliseconds(), TimeSpan.Zero);
                    foreach (var trigger in _alertPolicy.EvaluateMany(snapshot, selected,
                        key => GetReminder(key) is { Enabled: true } preference ? preference.WarningSeconds : null))
                        AddAlert(calendar.Place.Boss, false, calendar.Remaining, calendar.Place.Key,
                            spawnUnixMs: trigger.Boss.SpawnUnixMs, calendar: true, leadSeconds: trigger.LeadSeconds);
                }
            }
        }
        SafeEvent.Raise(Changed, "Boss Rush");
    }

    // User request 2026-10-25: all spawned bosses branch from the player; upcoming bosses form their own chain.
    internal static IReadOnlyList<MapTarget> NavigationTargets(IReadOnlyList<BossRushEntry> rows, int previewCount, string language, bool showOlderSpawns = false)
    {
        List<MapTarget> targets = [];
        foreach (var row in rows.Where(row => row.Spawned && row.SpawnElapsed is { } elapsed
            && elapsed >= TimeSpan.Zero && (showOlderSpawns || elapsed <= SpawnMarkerLifetime)))
            if (NavigationTarget(row, "spawned", null, language) is { } target) targets.Add(target);
        string? previous = null;
        var upcoming = rows.Where(row => !row.Spawned).Take(1 + Math.Clamp(previewCount, 0, 23));
        foreach (var row in upcoming)
        {
            if (NavigationTarget(row, previous is null ? "next" : "preview", previous, language) is { } target)
                targets.Add(target);
            // Keep the missing predecessor's ID: renderers must break that edge rather than invent a player branch.
            previous = NavigationId(row);
        }
        return targets;
    }

    private static string NavigationId(BossRushEntry row) => "boss:" + row.Place.Key;

    private static MapTarget? NavigationTarget(BossRushEntry row, string role, string? after, string language) =>
        row.Place.Boss is { X: { } x, Y: { } y } boss && double.IsFinite(x) && double.IsFinite(y)
        ? new MapTarget(NavigationId(row), row.Place.Map, x, y, boss.Name(language), "Boss Rush Mode", null,
            After: after, Color: row.Cached || row.Spawned ? "#4ade80" : "#ef4444",
            BossSpawnId: boss.SpawnId, BossRole: role, BossProvisional: row.Cached) : null;

    private void AddAlert(BossPlace boss, bool spawned, TimeSpan remaining, string? key = null, bool preview = false, long? spawnUnixMs = null, bool calendar = false, int leadSeconds = 0)
    {
        bool SameBoss(BossNotice notice) => key is not null ? notice.Key == key : notice.Boss.SpawnId == boss.SpawnId;
        // Later stages replace the delivery identity but retain the original held-window anchor.
        var held = !spawned && !preview ? _alerts.FirstOrDefault(notice => notice.UntilSpawn && SameBoss(notice)) : null;
        if (held is not null && held.SpawnUnixMs != spawnUnixMs) return;
        if (held is not null) _alerts.Remove(held);
        // Explicit previews are separate short notices; they must not cancel an existing held warning.
        _alerts.RemoveAll(notice => SameBoss(notice) && (!notice.UntilSpawn || spawned && !preview));
        var untilSpawn = !spawned && !preview && _settings.Current.BossAlertUntilSpawn;
        var now = _time.GetUtcNow();
        var starts = now;
        var fixedQueue = _alerts.Where(notice => !notice.UntilSpawn).ToArray();
        if (!untilSpawn && fixedQueue.Length >= 3 && fixedQueue[^3].Expires > starts) starts = fixedQueue[^3].Expires;
        var atDisplay = TimeSpan.FromTicks(Math.Max(0, (remaining - (starts - now)).Ticks));
        var expires = untilSpawn ? SafeAdd(now, remaining)
            : SafeAdd(starts, TimeSpan.FromSeconds(Math.Clamp(_settings.Current.BossAlertDurationSeconds, 3, 60)));
        _alerts.Add(held is not null ? held with { Id = ++_noticeId, LeadSeconds = leadSeconds }
            : new(++_noticeId, boss, spawned, atDisplay, expires, starts, key, preview, spawnUnixMs, calendar,
                untilSpawn, untilSpawn ? SafeAdd(now, remaining) : null, untilSpawn ? _time.GetTimestamp() : null, leadSeconds));
        // The bounded short-notice history cannot silently evict an active full-window warning.
        if (_alerts.Count(notice => !notice.UntilSpawn) > 128)
            _alerts.RemoveAt(_alerts.FindIndex(notice => !notice.UntilSpawn));
    }

    /// <summary>Active full-window IDs, independently of the current map or the fixed-duration display queue.</summary>
    internal BossNotice? UntilSpawnNotice(long id)
    {
        lock (_gate)
        {
            if (_disposed || !_settings.Current.BossAlertsEnabled) return null;
            var now = _time.GetUtcNow();
            RefreshUntilSpawnAlerts(now);
            if (_alerts.FirstOrDefault(notice => notice.Id == id && notice.UntilSpawn
                && notice.Key is { } key && IsReminderSelected(key)) is not { } found) return null;
            var current = AtDisplay(found, now);
            if (current.Remaining > TimeSpan.Zero) return current;
            _alerts.RemoveAll(notice => notice.Id == id);
            return null;
        }
    }

    private BossNotice AtDisplay(BossNotice notice, DateTimeOffset now)
    {
        var elapsed = notice.UntilSpawn && notice.HoldTimestamp is { } stamp
            ? _time.GetElapsedTime(stamp, _time.GetTimestamp()) : now - notice.CreatedAt;
        var remaining = TimeSpan.FromTicks(Math.Max(0, (notice.Remaining - elapsed).Ticks));
        return notice with
        {
            Remaining = remaining,
            PlannedAt = notice.UntilSpawn && remaining > TimeSpan.Zero ? SafeAdd(now, remaining) : notice.PlannedAt
        };
    }

    /// <summary>A newly accepted complete known list can replace a warning early with a real spawn notice.</summary>
    private void ConfirmHeldAlerts(BossRushSnapshot snapshot)
    {
        if (snapshot.Cached) return;
        var confirmed = snapshot.Bosses.Where(boss => boss.Spawned)
            .Select(boss => $"{snapshot.MapId}:{boss.SpawnId}").ToHashSet(StringComparer.Ordinal);
        _alerts.RemoveAll(notice => notice.UntilSpawn && notice.Key is { } key && confirmed.Contains(key));
    }

    private void RefreshUntilSpawnAlerts(DateTimeOffset now)
    {
        // User request 2026-10-18: a full warning window ends at countdown zero, never by inferring
        // a spawn. Gaps/maps preserve its monotonic remaining time; another cycle cannot renew it.
        var snapshot = _state.Snapshot;
        var fresh = snapshot is { Cached: false } && KnownList(snapshot) ? snapshot : null;
        for (var index = _alerts.Count - 1; index >= 0; index--)
        {
            var notice = _alerts[index];
            if (!notice.UntilSpawn) continue;
            var remaining = AtDisplay(notice, now).Remaining;
            if (fresh is not null && notice.Key is { } key && notice.SpawnUnixMs is { } cycle)
            {
                var native = fresh.Bosses.FirstOrDefault(boss => $"{fresh.MapId}:{boss.SpawnId}" == key);
                if (native is { Spawned: false, SpawnUnixMs: { } at } && at == cycle)
                {
                    remaining = TimeSpan.FromMilliseconds(Math.Max(0, at - fresh.ServerUnixMs));
                    notice = notice with { Remaining = remaining, HoldTimestamp = _time.GetTimestamp() };
                }
            }
            if (remaining <= TimeSpan.Zero)
            {
                _alerts.RemoveAt(index);
                continue;
            }
            var end = SafeAdd(now, remaining);
            // Without a same-cycle clock correction, keep the original monotonic anchor unchanged.
            _alerts[index] = notice with { Expires = end, PlannedAt = end };
        }
    }

    private static DateTimeOffset SafeAdd(DateTimeOffset time, TimeSpan span)
    {
        if (span.Ticks > 0 && DateTimeOffset.MaxValue.UtcTicks - time.UtcTicks < span.Ticks) return DateTimeOffset.MaxValue;
        if (span.Ticks < 0 && time.UtcTicks < -span.Ticks) return DateTimeOffset.MinValue;
        return new DateTimeOffset(time.UtcTicks + span.Ticks, TimeSpan.Zero);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _timer.Dispose();
        _network.BossMessageReceived -= OnMessage;
        _network.BossSessionReset -= Reset;
        _network.BossStreamGap -= InvalidateStream;
        _settings.Changed -= Refresh;
        _targets.SetBossNavigationTargets([]);
    }
}
