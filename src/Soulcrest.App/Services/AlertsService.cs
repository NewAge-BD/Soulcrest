using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Soulcrest.Core.Alerts;

namespace Soulcrest.App.Services;

public sealed record EventAlert
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public AlertSchedule Schedule { get; init; } = new();
    public bool Enabled { get; init; } = true;
    public int LeadMinutes { get; init; } = 5;
    public bool NotifyAtStart { get; init; } = true;
    public int DurationSeconds { get; init; } = 10;
    public bool ShowOverlay { get; init; } = true;
    public bool PlaySound { get; init; }
    public bool AlwaysShowOverlay { get; init; }
    public int OverlayLeadMinutes { get; init; } = 15;
    public string Sound { get; init; } = "standard";
    public string? SoundFile { get; init; }
    public string Color { get; init; } = "#a78bfa";
    public string SourceLabel { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public bool NeedsConfirmation { get; init; }
    public bool ScheduleConfirmed { get; init; }
}

public sealed record AlertUpcoming(EventAlert Alert, DateTimeOffset? At);
public sealed record AlertPreset(string Id, string Title, string Description);
public sealed record AlertNotice(string Key, string Title, string Message, string Color,
    DateTimeOffset CreatedAt, DateTimeOffset Expires, DateTimeOffset At,
    bool PlaySound, string Sound, bool ShowOverlay, bool Preview = false, string? SoundFile = null, bool AwaitingConfirmation = false);
public sealed record AlertDisplayRow(string Key, string Title, string Message, string Color,
    DateTimeOffset At, bool Started, bool Provisional = false, string? CountdownLabel = null, string? IconId = null,
    string? InlineLabel = null, bool AwaitingConfirmation = false);
public sealed record BossAlertPresentation
{
    public bool ShowOverlay { get; init; } = true;
    public bool AlwaysShowOverlay { get; init; }
    public int OverlayLeadMinutes { get; init; } = 5;
    public bool PlaySound { get; init; }
    public string Sound { get; init; } = "standard";
    public string? SoundFile { get; init; }
}

/// <summary>Local alerts, independent of the selected tab. Boss notifications consume the existing policy.</summary>
public sealed class AlertsService : IDisposable
{
    private const int ItemLimit = 128;
    private const int NoticeLimit = 128;
    private const int ReceiptLimit = 4096;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> Sounds = ["standard", "exclamation", "asterisk", "beep", "hand", "question", "custom"];
    private static readonly Regex ColorPattern = new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly BossRushService? _bosses;
    private readonly SettingsService? _settings;
    private readonly ITimer? _timer;
    private List<EventAlert> _items = [];
    private string[] _soundFiles = [];
    private readonly Dictionary<string, DateTimeOffset> _activatedAt = new(StringComparer.Ordinal);
    private List<Receipt> _receipts = [];
    private Dictionary<string, BossAlertPresentation> _bossPresentation = new(StringComparer.Ordinal);
    private Dictionary<string, EventTimerPresentation> _eventPresentation = new(StringComparer.Ordinal);
    private readonly List<DisplayedNotice> _active = [];
    private readonly HashSet<string> _bossPreviewKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _bossPreviewOrder = new();
    private DateTimeOffset _lastTick;
    private bool _disposed;
    private bool _unsupportedStore;
    private string? _status;

    private sealed record Receipt(string Key, DateTimeOffset At);
    private sealed record DisplayedNotice(AlertNotice Notice, string Owner, long? UntilSpawnSourceId = null);
    private sealed class Store
    {
        public Store() { }
        public int Version { get; set; } = 1;
        public List<EventAlert>? Items { get; set; } = [];
        public string[]? SoundFiles { get; set; } = [];
        public List<Receipt>? Receipts { get; set; } = [];
        public Dictionary<string, BossAlertPresentation>? BossPresentation { get; set; } = [];
        public Dictionary<string, EventTimerPresentation>? EventPresentation { get; set; } = [];
    }

    public AlertsService(BossRushService bosses, SettingsService settings)
        : this(AppPaths.AlertsFile, runTimer: true, bosses: bosses, settings: settings) { }

    internal AlertsService(string path, TimeProvider? time = null, bool runTimer = false,
        BossRushService? bosses = null, SettingsService? settings = null)
    {
        _path = Path.GetFullPath(path);
        _time = time ?? TimeProvider.System;
        _bosses = bosses;
        _settings = settings;
        _lastTick = Now;
        try
        {
            var saved = JsonFile.Load<Store>(_path);
            if (saved.Version != 1)
            {
                _unsupportedStore = true;
                _status = UiText.T("Diese Alerts-Dateiversion wird nicht unterstützt.");
            }
            else
            {
                _soundFiles = (saved.SoundFiles ?? []).Where(ValidSoundFile).Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var alert in saved.Items ?? [])
                {
                    if (alert is null || string.IsNullOrWhiteSpace(alert.Id) || !ids.Add(alert.Id)) continue;
                    var safe = CatalogSchedule(alert);
                    if (Validation(safe) is not null) safe = safe with { Enabled = false };
                    _items.Add(Clone(safe));
                    _activatedAt[alert.Id] = _lastTick;
                    if (_items.Count >= ItemLimit) break;
                }
                _receipts = (saved.Receipts ?? []).Where(receipt => receipt is not null && !string.IsNullOrEmpty(receipt.Key))
                    .DistinctBy(receipt => receipt.Key).TakeLast(ReceiptLimit).ToList();
                foreach (var (key, presentation) in saved.BossPresentation ?? [])
                    if (!string.IsNullOrEmpty(key) && presentation is not null && ValidPresentation(presentation))
                        _bossPresentation[key] = presentation with { Sound = SafeSound(presentation.Sound) };
                foreach (var (key, presentation) in saved.EventPresentation ?? [])
                    if (EventCatalog.Find(key) is not null && presentation is not null && ValidPresentation(presentation))
                        _eventPresentation[key] = presentation;
                _soundFiles = SoundFiles.Take(128).ToArray();
            }
        }
        catch (Exception error) when (StorageError(error))
        {
            _unsupportedStore = true; // Never overwrite a file which could not be read.
            _status = UiText.T("Alerts konnten nicht geladen werden.");
            LogFile.Error("Alerts", error);
        }
        if (_bosses is not null) _bosses.Changed += Tick;
        if (_settings is not null) _settings.Changed += SettingsChanged;
        if (runTimer) _timer = _time.CreateTimer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Strip choices are independent of reminder delivery and preserve uncaptured boss choices.</summary>
    public IReadOnlyList<TimerOverlayChoice> TimerOverlayChoices
    {
        get
        {
            lock (_gate) return TimerOverlayPresentation.Choices(TimerOverlayCandidates(Now),
                _settings?.Current.TimerOverlayTimerIds);
        }
    }

    /// <summary>Selected strip countdowns; does not alter the legacy HUD, banners or timeline.</summary>
    public IReadOnlyList<AlertDisplayRow> TimerOverlayRows
    {
        get
        {
            lock (_gate)
            {
                var now = Now;
                return TimerOverlayPresentation.Rows(TimerOverlayCandidates(now),
                    _settings?.Current.TimerOverlayTimerIds, now, _settings?.Current.TimerOverlayLeadMinutes ?? 0);
            }
        }
    }

    private IReadOnlyList<TimerOverlayCandidate> TimerOverlayCandidates(DateTimeOffset now)
    {
        var candidates = EventCatalog.All.Select(definition => TimerOverlayPresentation.FromEvent(definition,
            TimerSnapshot(definition, now))).ToList();
        if (_bosses is not null)
        {
            var legacy = (_settings?.Current.BossAlertIds ?? []).ToHashSet(StringComparer.Ordinal);
            var explicitBosses = (_settings?.Current.TimerOverlayTimerIds ?? [])
                .Where(key => key is not null && key.StartsWith("boss:", StringComparison.Ordinal))
                .Select(key => key[5..]).ToHashSet(StringComparer.Ordinal);
            var captured = _bosses.Schedule.ToDictionary(timer => timer.Place.Key, StringComparer.Ordinal);
            foreach (var choice in _bosses.Catalogue.Where(choice => legacy.Contains(choice.Key) || explicitBosses.Contains(choice.Key)))
                candidates.Add(TimerOverlayPresentation.FromBoss(choice, captured.GetValueOrDefault(choice.Key),
                    legacy.Contains(choice.Key) && BossPresentation(choice.Key).ShowOverlay, BossName(choice.Boss), now));
        }
        candidates.AddRange(_items.Where(alert => !alert.Id.StartsWith(EventCatalog.ReminderPrefix, StringComparison.Ordinal))
            .Select(alert => TimerOverlayPresentation.FromCustom(alert, now)));
        return candidates;
    }

    public IReadOnlyList<MapTimerRow> MapTimers => MapTimerPresentation.Create(EventTimers,
        _bosses?.Schedule ?? [], _bosses?.Catalogue ?? [], _settings?.Current.BossAlertIds ?? [],
        Now, _settings?.Current.NameLanguage ?? "en");

    public IReadOnlyList<string> SoundFiles
    {
        get
        {
            lock (_gate) return _soundFiles.Concat(_items.Where(item => item.Sound == "custom").Select(item => item.SoundFile))
                .Concat(_bossPresentation.Values.Where(item => item.Sound == "custom").Select(item => item.SoundFile))
                .Where(ValidSoundFile).Select(path => Path.GetFullPath(path!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
    public bool AddSoundFile(string path)
    {
        if (!ValidSoundFile(path)) return false;
        lock (_gate)
        {
            if (_disposed) return false;
            path = Path.GetFullPath(path);
            if (_soundFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) return true;
            if (_soundFiles.Length >= 128) return false;
            var previous = _soundFiles;
            _soundFiles = [.. _soundFiles, path];
            if (!Persist(_items, _receipts, _bossPresentation)) { _soundFiles = previous; return false; }
            _status = null;
        }
        SafeEvent.Raise(Changed, "Alerts");
        return true;
    }

    public event Action? Changed;
    public event Action<AlertNotice>? NoticeRaised;
    public DateTimeOffset Now => _time.GetUtcNow();
    public string? Status { get { lock (_gate) return _status; } }
    public IReadOnlyList<EventAlert> Items { get { lock (_gate) return _items.Select(Clone).ToArray(); } }
    public IReadOnlyList<AlertUpcoming> Upcoming
    {
        get
        {
            lock (_gate) return _items.Where(alert => alert.Enabled)
                .Select(alert => new AlertUpcoming(Clone(alert), AlertSchedules.Next(alert.Schedule, Now)))
                .OrderBy(row => row.At is null).ThenBy(row => row.At).ToArray();
        }
    }
    public IReadOnlyList<EventTimerSnapshot> EventTimers
    {
        get
        {
            lock (_gate)
            {
                var now = Now;
                return EventCatalog.All.Select(definition => TimerSnapshot(definition, now))
                    .OfType<EventTimerSnapshot>().ToArray();
            }
        }
    }

    /// <summary>A catalog timer exists before its optional reminder is saved or enabled.</summary>
    public EventAlert GetEventReminder(string id)
    {
        lock (_gate)
        {
            var definition = EventCatalog.Find(id) ?? throw new ArgumentException("Unknown event catalog id.", nameof(id));
            return _items.FirstOrDefault(item => item.Id == EventCatalog.ReminderId(id)) is { } reminder
                ? Clone(reminder) : EventCatalog.Reminder(definition);
        }
    }

    public bool SetEventReminderEnabled(string id, bool enabled) => Save(GetEventReminder(id) with { Enabled = enabled });

    public bool SetEventPresentation(string id, EventTimerPresentation presentation)
    {
        var saved = false;
        lock (_gate)
        {
            if (_disposed) return false;
            if (EventCatalog.Find(id) is null || presentation is null || !ValidPresentation(presentation))
                _status = ValidationMessage("event-presentation");
            else
            {
                var updated = new Dictionary<string, EventTimerPresentation>(_eventPresentation, StringComparer.Ordinal) { [id] = presentation };
                if (Persist(_items, _receipts, _bossPresentation, updated))
                {
                    _eventPresentation = updated;
                    _status = UiText.T("Event-Overlay gespeichert.");
                    saved = true;
                }
            }
        }
        SafeEvent.Raise(Changed, "Alerts");
        return saved;
    }

    public IReadOnlyList<AlertNotice> Active
    {
        get
        {
            lock (_gate)
            {
                RefreshUntilSpawnBanners();
                var now = Now;
                return _active.Where(row => (row.UntilSpawnSourceId is not null || row.Notice.CreatedAt <= now) && row.Notice.Expires > now)
                    .Select(row => row.Notice).ToArray();
            }
        }
    }

    /// <summary>Transient notification banners; intentionally independent of minimap/HUD suppression.</summary>
    public IReadOnlyList<AlertNotice> NotificationRows
    {
        get
        {
            lock (_gate)
            {
                RefreshUntilSpawnBanners();
                var now = Now;
                return _active.Where(row => row.Notice.ShowOverlay && (row.UntilSpawnSourceId is not null || row.Notice.CreatedAt <= now) && row.Notice.Expires > now &&
                    BossNoticeAllowed(row))
                    .Select(row => row.Notice).ToArray();
            }
        }
    }

    /// <summary>The ordinary HUD contains countdowns only, never transient notification banners.</summary>
    public IReadOnlyList<AlertDisplayRow> CountdownRows => DisplayRows(includeNotifications: false);

    /// <summary>Compatibility snapshot combining countdowns and transient banners.</summary>
    public IReadOnlyList<AlertDisplayRow> OverlayRows => DisplayRows(includeNotifications: true);

    private IReadOnlyList<AlertDisplayRow> DisplayRows(bool includeNotifications)
    {
        lock (_gate)
        {
            RefreshUntilSpawnBanners();
            var now = Now;
            var rows = new Dictionary<string, AlertDisplayRow>(StringComparer.Ordinal);
            foreach (var alert in _items.Where(alert => alert.Enabled && alert.ShowOverlay &&
                !alert.Id.StartsWith(EventCatalog.ReminderPrefix, StringComparison.Ordinal)))
            {
                if (AlertSchedules.Next(alert.Schedule, now) is not { } at) continue;
                if (!alert.AlwaysShowOverlay && at - now > TimeSpan.FromMinutes(alert.OverlayLeadMinutes)) continue;
                var owner = "event:" + alert.Id;
                rows[owner] = new(owner, alert.Title, alert.Message, alert.Color, at, false);
            }
            foreach (var definition in EventCatalog.All)
            {
                var presentation = EventPresentation(definition.Id);
                if (!presentation.ShowOverlay || TimerSnapshot(definition, now) is not { } timer) continue;
                var window = timer.Window;
                if (!window.IsActive && !presentation.AlwaysShowOverlay &&
                    window.StartsAt - now > TimeSpan.FromMinutes(presentation.OverlayLeadMinutes)) continue;
                var at = window.Phase == EventWindowPhase.PortalOpen ? window.PortalEndsAt!.Value :
                    window.IsActive ? window.EndsAt : window.StartsAt;
                var label = window.Phase == EventWindowPhase.PortalOpen ? "Portal schließt in" :
                    window.IsActive ? "Endet in" : "Startet in";
                var owner = "event:" + EventCatalog.ReminderId(definition.Id);
                rows[owner] = new(owner, UiText.T(definition.Title), "", definition.Color, at,
                    window.IsActive, CountdownLabel: UiText.T(label));
            }
            if (_bosses is not null && _settings?.Current.BossAlertsEnabled == true)
            {
                var selected = (_settings.Current.BossAlertIds ?? []).ToHashSet(StringComparer.Ordinal);
                foreach (var boss in _bosses.Schedule.Where(row => selected.Contains(row.Place.Key)))
                {
                    var presentation = BossPresentation(boss.Place.Key);
                    if (!presentation.ShowOverlay || !presentation.AlwaysShowOverlay &&
                        (boss.Spawned || boss.Remaining > TimeSpan.FromMinutes(presentation.OverlayLeadMinutes))) continue;
                    var owner = "boss:" + boss.Place.Key;
                    rows[owner] = new(owner, BossName(boss.Place.Boss), "", "#ef4444",
                        SafeAdd(now, boss.Remaining), boss.Spawned, boss.Cached || (!boss.Spawned && boss.Remaining <= TimeSpan.Zero));
                }
            }
            if (includeNotifications)
            {
                foreach (var active in _active.Where(row => (row.UntilSpawnSourceId is not null || row.Notice.CreatedAt <= now) && row.Notice.Expires > now && row.Notice.ShowOverlay))
                {
                    if (!BossNoticeAllowed(active)) continue;
                    var notice = active.Notice;
                    // Captured boss state and catalog phase take precedence over an older notification.
                    if (rows.ContainsKey(active.Owner) && (active.Owner.StartsWith("event:catalog:", StringComparison.Ordinal) ||
                        active.Owner.StartsWith("boss:", StringComparison.Ordinal) && !active.Notice.Preview && active.UntilSpawnSourceId is null)) continue;
                    rows[active.Owner] = new(active.Owner, notice.Title, notice.Message, notice.Color, notice.At,
                        notice.At <= now && !notice.AwaitingConfirmation, AwaitingConfirmation: notice.AwaitingConfirmation);
                }
            }
            return rows.Values.OrderByDescending(row => row.Started).ThenBy(row => row.At)
                .ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
        }
    }

    public bool Save(EventAlert alert)
    {
        bool saved;
        lock (_gate)
        {
            if (_disposed) return false;
            alert = CatalogSchedule(alert);
            var code = Validation(alert);
            if (code is not null) { _status = ValidationMessage(code); saved = false; }
            else
            {
                var normalized = Clone(alert with { Title = alert.Title.Trim(), Message = alert.Message.Trim() });
                var items = _items.Where(item => item.Id != normalized.Id).ToList();
                items.Add(normalized);
                if (items.Count > ItemLimit) { _status = ValidationMessage("limit"); saved = false; }
                else if (Persist(items, _receipts, _bossPresentation))
                {
                    _items = items;
                    _activatedAt[alert.Id] = Now;
                    _active.RemoveAll(row => row.Owner == "event:" + alert.Id);
                    _status = UiText.T("Alert gespeichert.");
                    saved = true;
                }
                else saved = false;
            }
        }
        SafeEvent.Raise(Changed, "Alerts");
        return saved;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            if (_disposed) return false;
            var items = _items.Where(item => item.Id != id).ToList();
            if (items.Count == _items.Count) return false;
            removed = Persist(items, _receipts, _bossPresentation);
            if (removed)
            {
                _items = items;
                _activatedAt.Remove(id);
                _active.RemoveAll(row => row.Owner == "event:" + id);
                _status = UiText.T("Alert gelöscht.");
            }
        }
        SafeEvent.Raise(Changed, "Alerts");
        return removed;
    }

    public void SetEnabled(string id, bool enabled)
    {
        EventAlert? alert;
        lock (_gate) alert = _items.FirstOrDefault(item => item.Id == id);
        if (alert is not null) Save(alert with { Enabled = enabled });
    }

    public void Preview(string id)
    {
        AlertNotice? notice = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_items.FirstOrDefault(item => item.Id == id) is not { } alert) return;
            var now = Now;
            notice = new("preview:" + Guid.NewGuid().ToString("N"), alert.Title, alert.Message,
                SafeColor(alert.Color), now, SafeAdd(now, TimeSpan.FromSeconds(Math.Clamp(alert.DurationSeconds, 3, 60))),
                AlertSchedules.Next(alert.Schedule, now) ?? now, alert.PlaySound, SafeSound(alert.Sound), alert.ShowOverlay, true, alert.SoundFile);
            AddNotice(notice, "event:" + id);
            _status = UiText.T("Alert-Vorschau gestartet.");
        }
        Publish([notice]);
    }

    public BossAlertPresentation GetBossPresentation(string key)
    {
        lock (_gate) return BossPresentation(key);
    }

    public bool SetBossPresentation(string key, BossAlertPresentation presentation)
    {
        var saved = false;
        lock (_gate)
        {
            if (_disposed) return false;
            if (string.IsNullOrWhiteSpace(key) || presentation is null || !ValidPresentation(presentation))
                _status = ValidationMessage("presentation");
            else
            {
                presentation = presentation with { Sound = SafeSound(presentation.Sound) };
                var updated = new Dictionary<string, BossAlertPresentation>(_bossPresentation, StringComparer.Ordinal) { [key] = presentation };
                if (Persist(_items, _receipts, updated))
                {
                    _bossPresentation = updated;
                    _status = UiText.T("Boss-Alert gespeichert.");
                    saved = true;
                }
            }
        }
        SafeEvent.Raise(Changed, "Alerts");
        return saved;
    }

    internal void Tick()
    {
        List<(AlertNotice Notice, string Owner, long? UntilSpawnSourceId)> generated = [];
        lock (_gate)
        {
            if (_disposed) return;
            var now = Now;
            RefreshUntilSpawnBanners();
            _active.RemoveAll(row => row.Notice.Expires <= now || row.UntilSpawnSourceId is null && row.Notice.CreatedAt > now || !BossNoticeAllowed(row));
            var from = _lastTick > now ? now : _lastTick < SafeAdd(now, -Grace) ? SafeAdd(now, -Grace) : _lastTick;
            var receipts = _receipts.ToList();
            var keys = receipts.Select(receipt => receipt.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var alert in _items.Where(item => item.Enabled))
            {
                var alertFrom = _activatedAt.TryGetValue(alert.Id, out var activated) && activated > from ? activated : from;
                var leads = new HashSet<int>();
                if (alert.LeadMinutes > 0) leads.Add(alert.LeadMinutes);
                if (alert.NotifyAtStart) leads.Add(0);
                foreach (var lead in leads)
                {
                    var occurrence = AlertSchedules.Next(alert.Schedule, SafeAdd(alertFrom, TimeSpan.FromMinutes(lead)));
                    for (var count = 0; occurrence is { } at && at <= SafeAdd(now, TimeSpan.FromMinutes(lead)) && count < NoticeLimit; count++)
                    {
                        var key = $"event:{alert.Id}:{at.UtcTicks}:{lead}";
                        if (keys.Add(key))
                        {
                            receipts.Add(new(key, now));
                            generated.Add((new(key, alert.Title, alert.Message, alert.Color, now,
                                SafeAdd(now, TimeSpan.FromSeconds(alert.DurationSeconds)), at,
                                alert.PlaySound, alert.Sound, alert.ShowOverlay &&
                                (alert.Id.StartsWith(EventCatalog.ReminderPrefix, StringComparison.Ordinal) ||
                                    alert.AlwaysShowOverlay || at - now <= TimeSpan.FromMinutes(alert.OverlayLeadMinutes)),
                                SoundFile: alert.SoundFile), "event:" + alert.Id, null));
                        }
                        occurrence = AlertSchedules.Next(alert.Schedule, at);
                    }
                }
            }
            if (_bosses is not null && _settings?.Current.BossAlertsEnabled == true)
            {
                foreach (var source in _bosses.Alerts)
                {
                    if (source.Key is not { Length: > 0 } bossKey) continue;
                    var previewKey = $"boss:{bossKey}:{source.CreatedAt.UtcTicks}:{source.Id}";
                    if (source.Preview)
                    {
                        if (!_bossPreviewKeys.Add(previewKey)) continue;
                        _bossPreviewOrder.Enqueue(previewKey);
                        while (_bossPreviewOrder.Count > NoticeLimit) _bossPreviewKeys.Remove(_bossPreviewOrder.Dequeue());
                        var previewPresentation = BossPresentation(bossKey);
                        generated.Add((new("preview:" + previewKey, BossName(source.Boss), UiText.T("Vorschau"), "#ef4444", now,
                            source.Expires, SafeAdd(now, source.Remaining), previewPresentation.PlaySound,
                            previewPresentation.Sound, true, true, previewPresentation.SoundFile), "boss:" + bossKey, null));
                        continue;
                    }
                    if (source.SpawnUnixMs is not { } cycle || !_bosses.GetReminder(bossKey).Enabled) continue;
                    // A held source may outlive the bounded persistent receipt history. Its already
                    // delivered copy remains the authoritative in-session dedupe until confirmation.
                    if (source.UntilSpawn && _active.Any(row => row.UntilSpawnSourceId == source.Id)) continue;
                    // Match the existing BossAlertPolicy identity across process restarts. The
                    // local notice id and delivery time change when a fresh list is received.
                    var key = $"boss:{bossKey}:{cycle}:{(source.Spawned ? "spawn" : "lead")}";
                    if (!keys.Add(key)) continue;
                    receipts.Add(new(key, now));
                    // The source already passed the full-list/clock or verified-calendar policy.
                    // Map changes and later gaps do not revoke a notification that was validly created.
                    if (!(_settings.Current.BossAlertIds ?? []).Contains(bossKey, StringComparer.Ordinal)) continue;
                    var presentation = BossPresentation(bossKey);
                    generated.Add((new(key, BossName(source.Boss), "", "#ef4444", now, source.Expires,
                        source.PlannedAt ?? SafeAdd(now, source.Remaining), presentation.PlaySound, presentation.Sound,
                        true, // Boss reminders always produce their own banner, independently of timer-HUD windows.
                        SoundFile: presentation.SoundFile, AwaitingConfirmation: source.UntilSpawn && source.Remaining <= TimeSpan.Zero),
                        "boss:" + bossKey, source.UntilSpawn ? source.Id : null));
                }
            }
            if (receipts.Count != _receipts.Count)
            {
                receipts = receipts.TakeLast(ReceiptLimit).ToList();
                if (!Persist(_items, receipts, _bossPresentation)) generated.RemoveAll(row => !row.Notice.Preview);
                else
                {
                    _receipts = receipts;
                    _lastTick = now;
                }
            }
            else _lastTick = now;
            foreach (var (notice, owner, sourceId) in generated) AddNotice(notice, owner, sourceId);
        }
        Publish(generated.Select(row => row.Notice));
    }

    private void SettingsChanged()
    {
        lock (_gate)
        {
            var selected = (_settings?.Current.BossAlertIds ?? []).ToHashSet(StringComparer.Ordinal);
            _active.RemoveAll(row => row.Owner.StartsWith("boss:", StringComparison.Ordinal) &&
                (_settings?.Current.BossAlertsEnabled != true || !selected.Contains(row.Owner[5..]) ||
                    !row.Notice.Preview && _bosses?.GetReminder(row.Owner[5..]).Enabled != true));
        }
        Tick();
    }

    private void RefreshUntilSpawnBanners()
    {
        for (var index = _active.Count - 1; index >= 0; index--)
        {
            var row = _active[index];
            if (row.UntilSpawnSourceId is not { } sourceId) continue;
            if (_bosses?.UntilSpawnNotice(sourceId) is not { } source)
            {
                _active.RemoveAt(index);
                continue;
            }
            _active[index] = row with { Notice = row.Notice with
            {
                Expires = source.Expires,
                At = source.PlannedAt ?? row.Notice.At,
                AwaitingConfirmation = source.Remaining <= TimeSpan.Zero,
                ShowOverlay = true
            } };
        }
    }

    private bool BossNoticeAllowed(DisplayedNotice row) =>
        !row.Owner.StartsWith("boss:", StringComparison.Ordinal) ||
        _settings?.Current.BossAlertsEnabled == true && (row.Notice.Preview ||
            (_settings.Current.BossAlertIds ?? []).Contains(row.Owner[5..], StringComparer.Ordinal) &&
            _bosses?.GetReminder(row.Owner[5..]).Enabled == true &&
            (row.UntilSpawnSourceId is not { } id || _bosses.UntilSpawnNotice(id) is not null));

    private void AddNotice(AlertNotice notice, string owner, long? untilSpawnSourceId = null)
    {
        _active.RemoveAll(row => row.Owner == owner && (row.UntilSpawnSourceId is null || row.UntilSpawnSourceId == untilSpawnSourceId));
        _active.Add(new(notice, owner, untilSpawnSourceId));
        if (_active.Count(row => row.UntilSpawnSourceId is null) > NoticeLimit)
            _active.RemoveAt(_active.FindIndex(row => row.UntilSpawnSourceId is null));
    }

    private void Publish(IEnumerable<AlertNotice> notices)
    {
        foreach (var notice in notices)
            foreach (var subscriber in NoticeRaised?.GetInvocationList().Cast<Action<AlertNotice>>() ?? [])
            {
                try { subscriber(notice); }
                catch (Exception error) { LogFile.Error("Alerts notice", error); }
            }
        SafeEvent.Raise(Changed, "Alerts");
    }

    private bool Persist(List<EventAlert> items, List<Receipt> receipts, Dictionary<string, BossAlertPresentation> presentations,
        Dictionary<string, EventTimerPresentation>? eventPresentations = null)
    {
        if (_unsupportedStore) { _status = UiText.T("Alerts-Datei konnte nicht sicher geöffnet werden."); return false; }
        try
        {
            JsonFile.Save(_path, new Store { Items = items, Receipts = receipts, BossPresentation = presentations,
                EventPresentation = eventPresentations ?? _eventPresentation, SoundFiles = _soundFiles });
            return true;
        }
        catch (Exception error) when (StorageError(error))
        {
            _status = UiText.T("Alerts konnten nicht gespeichert werden.");
            LogFile.Error("Alerts", error);
            return false;
        }
    }

    private EventTimerSnapshot? TimerSnapshot(EventTimerDefinition definition, DateTimeOffset now)
    {
        var window = EventWindows.At(definition.Schedule, now, TimeSpan.FromMinutes(definition.DurationMinutes),
            TimeSpan.FromMinutes(definition.PortalMinutes));
        if (window is null) return null;
        var reminder = _items.FirstOrDefault(item => item.Id == EventCatalog.ReminderId(definition.Id));
        return new(definition, window, reminder is null ? null : Clone(reminder), EventPresentation(definition.Id));
    }
    private EventTimerPresentation EventPresentation(string key) => _eventPresentation.GetValueOrDefault(key) ?? new();
    private static bool ValidPresentation(EventTimerPresentation value) => value.OverlayLeadMinutes is >= 0 and <= 10080;
    private static EventAlert CatalogSchedule(EventAlert alert)
    {
        if (alert?.Id is not { Length: > 0 } id || !id.StartsWith(EventCatalog.ReminderPrefix, StringComparison.Ordinal)) return alert!;
        return EventCatalog.Find(id[EventCatalog.ReminderPrefix.Length..]) is { } definition
            ? alert with { Schedule = definition.Schedule, SourceLabel = "", SourceUrl = definition.SourceUrl } : alert;
    }
    private BossAlertPresentation BossPresentation(string key) => _bossPresentation.TryGetValue(key, out var value)
        ? value : new() { PlaySound = _settings?.Current.BossAlertSound ?? false };
    private string BossName(BossPlace boss) => boss.Name(_settings?.Current.OverlayLanguage is "de" or "en"
        ? _settings.Current.OverlayLanguage : _settings?.Current.NameLanguage ?? "en");
    private static EventAlert Clone(EventAlert alert) => alert with
    {
        Schedule = (alert.Schedule ?? new AlertSchedule()) with
        { Times = alert.Schedule?.Times?.ToArray() ?? [], Weekdays = alert.Schedule?.Weekdays?.ToArray() ?? [] },
        Title = alert.Title ?? "", Message = alert.Message ?? "", SourceLabel = alert.SourceLabel ?? "", SourceUrl = alert.SourceUrl ?? "",
        Color = SafeColor(alert.Color), Sound = SafeSound(alert.Sound),
        SoundFile = ValidSoundFile(alert.SoundFile) ? Path.GetFullPath(alert.SoundFile!) : null
    };
    private static string SafeColor(string? color) => color is not null && ColorPattern.IsMatch(color) ? color : "#a78bfa";
    // User request 2026-10-12: one generated standard tone; old built-in values load without losing reminders.
    private static string SafeSound(string? sound) => sound == "custom" ? "custom" : "standard";
    private static bool ValidPresentation(BossAlertPresentation value) => value.OverlayLeadMinutes is >= 0 and <= 10080 &&
        Sounds.Contains(value.Sound ?? "") && ValidSoundChoice(value.Sound, value.SoundFile);
    private static bool ValidSoundChoice(string? sound, string? soundFile) =>
        (sound != "custom" || ValidSoundFile(soundFile)) && (string.IsNullOrEmpty(soundFile) || ValidSoundFile(soundFile));

    /// <summary>Only local absolute MP3 paths; playback checks file availability separately.</summary>
    internal static bool ValidSoundFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Length < 3 ||
            !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/') ||
            path.AsSpan(2).Contains(':') || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            path.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0 ||
            !Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)) return false;
        try { _ = Path.GetFullPath(path); return true; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static bool StorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException;
    private static DateTimeOffset SafeAdd(DateTimeOffset time, TimeSpan span)
    {
        if (span.Ticks > 0 && DateTimeOffset.MaxValue.UtcTicks - time.UtcTicks < span.Ticks) return DateTimeOffset.MaxValue;
        if (span.Ticks < 0 && time.UtcTicks < -span.Ticks) return DateTimeOffset.MinValue;
        return new DateTimeOffset(time.UtcTicks + span.Ticks, TimeSpan.Zero);
    }
    private static string? Validation(EventAlert? alert)
    {
        if (alert is null || string.IsNullOrWhiteSpace(alert.Id) || alert.Id.Length > 100) return "id";
        if (alert.Id.StartsWith(EventCatalog.ReminderPrefix, StringComparison.Ordinal) &&
            EventCatalog.Find(alert.Id[EventCatalog.ReminderPrefix.Length..]) is null) return "id";
        if (string.IsNullOrWhiteSpace(alert.Title) || alert.Title.Length > 100) return "title";
        if (alert.Message is null || alert.Message.Length > 500) return "message";
        if (AlertSchedules.Error(alert.Schedule) is { } error) return error;
        if (alert.Enabled && alert.NeedsConfirmation && !alert.ScheduleConfirmed) return "confirmation";
        if (alert.LeadMinutes is < 0 or > 10080 || alert.OverlayLeadMinutes is < 0 or > 10080) return "lead";
        if (alert.DurationSeconds is < 3 or > 60) return "duration";
        if (alert.Sound is null || !Sounds.Contains(alert.Sound)) return "sound";
        if (!ValidSoundChoice(alert.Sound, alert.SoundFile)) return "sound-file";
        if (alert.Color is null || !ColorPattern.IsMatch(alert.Color)) return "color";
        if (!string.IsNullOrEmpty(alert.SourceUrl) &&
            (!Uri.TryCreate(alert.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)) return "source";
        return null;
    }

    public static EventAlert NewAlert() => new() { Title = UiText.T("Neuer Alert") };
    public static IReadOnlyList<AlertPreset> Presets => EventCatalog.All
        .Select(definition => new AlertPreset(definition.Id, definition.Title, UiText.T(definition.Description)))
        .Append(new("cooldown", UiText.T("Eigener Cooldown"), UiText.T("Wiederholt nach einer frei wählbaren Anzahl Minuten.")))
        .ToArray();

    public static EventAlert FromPreset(string id, string zone)
    {
        if (EventCatalog.Find(id) is { } definition)
            return EventCatalog.Reminder(definition) with { Id = Guid.NewGuid().ToString("N"), Enabled = true,
                NotifyAtStart = true, OverlayLeadMinutes = 15 };
        var rule = NewAlert() with { Schedule = new() { TimeZoneId = zone } };
        return id == "cooldown" ? rule with { Title = UiText.T("Eigener Cooldown"),
            Schedule = new() { Kind = AlertScheduleKind.Interval, At = DateTimeOffset.UtcNow, TimeZoneId = zone } } : rule;
    }

    public static string ScheduleDescription(AlertSchedule schedule)
    {
        var times = string.Join(", ", schedule.Times?.Order().Select(time => time.ToString("HH:mm", CultureInfo.InvariantCulture)) ?? []);
        var text = schedule.Kind switch
        {
            AlertScheduleKind.Hourly => UiText.F("Stündlich um :{0}", schedule.Minute.ToString("00", CultureInfo.InvariantCulture)),
            AlertScheduleKind.Daily => UiText.F("Täglich um {0}", times),
            AlertScheduleKind.Weekly => UiText.F("Wöchentlich: {0}, {1}", string.Join(", ", (schedule.Weekdays ?? []).Select(DayLabel)), times),
            AlertScheduleKind.EveryNDays => UiText.F("Alle {0} Tage ab {1} um {2}", schedule.EveryDays, schedule.AnchorDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?", times),
            AlertScheduleKind.Interval => UiText.F("Alle {0} Minuten", schedule.IntervalMinutes),
            AlertScheduleKind.Once => UiText.F("Einmalig: {0}", schedule.At?.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) ?? "?"),
            _ => "?"
        };
        return text + " · " + schedule.TimeZoneId;
    }
    private static string DayLabel(DayOfWeek day) => UiText.T(day switch
    {
        DayOfWeek.Monday => "Montag", DayOfWeek.Tuesday => "Dienstag", DayOfWeek.Wednesday => "Mittwoch",
        DayOfWeek.Thursday => "Donnerstag", DayOfWeek.Friday => "Freitag", DayOfWeek.Saturday => "Samstag", _ => "Sonntag"
    });
    public static string ValidationMessage(string code) => UiText.T(code switch
    {
        "time-zone" => "Bitte eine gültige Zeitzone angeben.",
        "minute" => "Die Minute muss zwischen 0 und 59 liegen.",
        "times" => "Bitte mindestens eine Uhrzeit eintragen.",
        "weekdays" => "Bitte mindestens einen gültigen Wochentag wählen.",
        "anchor-date" => "Bitte ein Startdatum für den Zyklus eintragen.",
        "every-days" => "Der Tagesabstand muss größer als null sein.",
        "at" => "Bitte einen gültigen Startzeitpunkt eintragen.",
        "interval-minutes" => "Der Minutenabstand muss größer als null sein.",
        "title" => "Bitte einen Titel mit höchstens 100 Zeichen eintragen.",
        "message" => "Die Nachricht darf höchstens 500 Zeichen enthalten.",
        "confirmation" => "Bitte die regionalen Eventzeiten vor der Aktivierung bestätigen.",
        "lead" => "Vorwarnung und Overlayfenster müssen zwischen 0 und 10080 Minuten liegen.",
        "duration" => "Die Anzeigedauer muss zwischen 3 und 60 Sekunden liegen.",
        "sound" => "Bitte einen verfügbaren Signalton wählen.",
        "sound-file" => "Bitte eine lokale MP3-Datei mit vollständigem Pfad auswählen.",
        "color" => "Bitte eine gültige Markerfarbe wählen.",
        "source" => "Die Quellenadresse muss eine HTTPS-Adresse sein.",
        "limit" => "Maximal 128 eigene Alerts sind möglich.",
        "presentation" => "Bitte gültige Boss-Alert-Einstellungen wählen.",
        "event-presentation" => "Bitte gültige Event-Overlay-Einstellungen wählen.",
        _ => "Bitte einen gültigen Alert-Zeitplan angeben."
    });

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _timer?.Dispose();
        if (_bosses is not null) _bosses.Changed -= Tick;
        if (_settings is not null) _settings.Changed -= SettingsChanged;
    }
}
