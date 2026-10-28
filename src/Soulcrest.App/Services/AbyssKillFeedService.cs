using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>A frozen RAM history and its separately authorized current display binding.</summary>
internal sealed record AbyssKillFeedDisplaySnapshot(long? BindingGeneration, ImmutableArray<AbyssKillFeedEntry> Entries);

/// <summary>
/// Bounded live-only feed. A single gate serializes capture callbacks and settings;
/// no opponent name, event or deduplication watermark is written to disk or logs.
/// </summary>
public sealed class AbyssKillFeedService : IDisposable
{
    public const int MaximumEntries = 10;
    private readonly object _gate = new();
    private readonly SettingsService _settings;
    private readonly INetworkAbyssCombatSource _source;
    private readonly TimeProvider _time;
    private readonly List<AbyssKillFeedEntry> _entries = [];
    private ImmutableArray<AbyssKillFeedEntry> _snapshot = [];
    private OwnCharacterContext? _own;
    private (long CharacterId, int ServerId)? _feedCharacter;
    private long _generation = -1, _sequence;
    private long _observationSequence, _contextOrder = long.MinValue, _filterOrder = long.MinValue;
    private readonly long[] _kindFilterOrders = [long.MinValue, long.MinValue, long.MinValue];
    private FeedFilters _filters;
    private bool _enabled, _running;
    private int _disposed;

    private readonly record struct FeedFilters(bool Kills, bool Deaths, bool Assists)
    {
        public bool Allows(NetworkAbyssCombatKind kind) => kind switch
        { NetworkAbyssCombatKind.Kill => Kills, NetworkAbyssCombatKind.Death => Deaths, NetworkAbyssCombatKind.Assist => Assists, _ => false };
        public bool Allows(AbyssKillFeedKind kind) => kind switch
        { AbyssKillFeedKind.Kill => Kills, AbyssKillFeedKind.Death => Deaths, AbyssKillFeedKind.Assist => Assists, _ => false };
    }

    public AbyssKillFeedService(SettingsService settings, INetworkAbyssCombatSource source)
        : this(settings, source, TimeProvider.System) { }

    internal AbyssKillFeedService(SettingsService settings, INetworkAbyssCombatSource source, TimeProvider time)
    {
        _settings = settings; _source = source; _time = time;
        settings.Changed += Refresh; source.Changed += Refresh; source.OwnCharacterChanged += Refresh;
        source.CombatEvent += Accept;
        Refresh();
    }

    public ImmutableArray<AbyssKillFeedEntry> Snapshot { get { lock (_gate) return _snapshot; } }
    // Historical rows keep their original event generation. This separate display binding
    // authorizes that frozen history only after the same character has entered the world again.
    internal AbyssKillFeedDisplaySnapshot DisplaySnapshot
    {
        get { lock (_gate) return new(_running && _enabled ? _own?.Generation : null, _snapshot); }
    }
    public event Action? Changed;

    private bool Enabled => _settings.Current.AbyssKillFeedEnabled && _settings.Current.LootTrackingEnabled;
    private FeedFilters SelectedKinds
    {
        get
        {
            var capabilities = _source.CombatCapabilities;
            return new(_settings.Current.AbyssKillFeedShowKills && capabilities.KillQualified,
                _settings.Current.AbyssKillFeedShowDeaths && capabilities.DeathQualified,
                _settings.Current.AbyssKillFeedShowAssists && capabilities.AssistQualified);
        }
    }

    private void Refresh()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // Never acquire the network/source lock while holding our gate: source callbacks may
        // already hold their own lock when entering this service.
        var order = Interlocked.Increment(ref _observationSequence);
        var own = _source.OwnCharacter; var running = _source.Running; var enabled = Enabled;
        var filters = SelectedKinds;
        bool changed;
        lock (_gate)
        {
            if (_disposed != 0) return;
            changed = ApplyContext(own, running, enabled, order);
            changed |= ApplyFilters(filters, order);
            if (changed) Publish();
        }
        if (changed) RaiseChanged();
    }

    private void Accept(NetworkAbyssCombatEvent observation)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var order = Interlocked.Increment(ref _observationSequence);
        var own = _source.OwnCharacter; var running = _source.Running; var enabled = Enabled;
        var filters = SelectedKinds;
        var now = _time.GetUtcNow(); bool changed;
        lock (_gate)
        {
            if (_disposed != 0) return;
            changed = ApplyContext(own, running, enabled, order);
            changed |= ApplyFilters(filters, order);
            if (_running && ReferenceEquals(_own, own) && own is not null && ReferenceEquals(observation.Character, own)
                && observation.Character.Generation == _generation && observation.Sequence > _sequence
                && Enum.IsDefined(observation.Kind))
            {
                // Retain the watermark while disabled, after eviction and across an opt-out boundary.
                // A delayed current-own callback must consume its sequence before display policy;
                // otherwise a later echo could resurrect the cleared event after re-enabling.
                _sequence = observation.Sequence;
                if (order >= _contextOrder && _enabled && _filters.Allows(observation.Kind)
                    && order >= _kindFilterOrders[(int)observation.Kind]
                    && observation.At != default && observation.At <= now
                    && own.ObservedAt is { } ownAt && ownAt != default && observation.At >= ownAt)
                {
                    var entry = new AbyssKillFeedEntry(observation.At,
                        observation.Kind switch { NetworkAbyssCombatKind.Kill => AbyssKillFeedKind.Kill,
                            NetworkAbyssCombatKind.Death => AbyssKillFeedKind.Death, _ => AbyssKillFeedKind.Assist },
                        DisplayName(observation.OpponentName), DisplayRankDelta(observation.Kind, observation.RankPointDelta))
                    {
                        Generation = observation.Character.Generation, Sequence = observation.Sequence,
                        AbyssPointGain = observation.Kind != NetworkAbyssCombatKind.Death && observation.AbyssPointGain is >= 0
                            ? observation.AbyssPointGain : null,
                        OpponentClassId = DisplayClassId(observation.OpponentClassId),
                        OpponentAbyssRankGrade = observation.OpponentAbyssRankGrade is >= 1 and <= 19
                            ? observation.OpponentAbyssRankGrade : null
                    };
                    _entries.Insert(0, entry);
                    if (_entries.Count > MaximumEntries) _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
                    changed = true;
                }
            }
            if (changed) Publish();
        }
        if (changed) RaiseChanged();
    }

    private bool ApplyContext(OwnCharacterContext? own, bool running, bool enabled, long order)
    {
        if (order < _contextOrder || own is not null && own.Generation < _generation) return false;
        var changed = !ReferenceEquals(_own, own) || _running != running || _enabled != enabled;
        // An unchanged source refresh is not a new context boundary. Advancing it here would
        // discard an earlier delivered, still valid event waiting for this gate.
        // Each own/transport/opt-out boundary still invalidates pre-boundary callbacks;
        // keeping already accepted history never authorizes an old combat observation.
        if (changed) _contextOrder = order;
        // A loading screen temporarily removes Own and supplies a fresh own/entity epoch.
        // History belongs to the stable character/server, not that transient entity epoch.
        // No transport or income continuity is inferred from retaining these frozen rows.
        var character = own is null ? ((long CharacterId, int ServerId)?)null
            : (own.Identity.CharacterId, own.Identity.ServerId);
        if (!running || !enabled || character is { } next && _feedCharacter is { } previous && next != previous)
            _entries.Clear();
        if (!running || !enabled) _feedCharacter = null;
        else if (character is not null) _feedCharacter = character;
        _own = own; _running = running; _enabled = enabled;
        if (own is not null && own.Generation != _generation)
        { _generation = own.Generation; _sequence = 0; }
        return changed;
    }

    private bool ApplyFilters(FeedFilters filters, long order)
    {
        if (order < _filterOrder || filters == _filters) return false;
        foreach (var kind in Enum.GetValues<NetworkAbyssCombatKind>())
            if (filters.Allows(kind) != _filters.Allows(kind)) _kindFilterOrders[(int)kind] = order;
        _filters = filters; _filterOrder = order;
        _entries.RemoveAll(row => !filters.Allows(row.Kind));
        return true;
    }

    private void Publish() => _snapshot = _entries.ToImmutableArray();

    private static string? DisplayName(string? name) => string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl)
        ? null : name.Trim();

    private static long? DisplayRankDelta(NetworkAbyssCombatKind kind, long? delta) => kind switch
    {
        NetworkAbyssCombatKind.Death => delta is <= 0 ? delta : null,
        NetworkAbyssCombatKind.Kill or NetworkAbyssCombatKind.Assist => delta is >= 0 ? delta : null,
        _ => null
    };

    private static string? DisplayClassId(string? classId) => classId is "Gladiator" or "Templar" or "Ranger"
        or "Assassin" or "Elementalist" or "Sorcerer" or "Cleric" or "Chanter" ? classId : null;

    private void RaiseChanged()
    {
        var listeners = Changed;
        if (listeners is null) return;
        foreach (var listener in listeners.GetInvocationList().Cast<Action>())
        {
            try { listener(); }
            catch (Exception)
            {
                // Subscriber exception messages may contain an opponent name. This ephemeral
                // feature deliberately does not log those exceptions or any feed payload.
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _settings.Changed -= Refresh; _source.Changed -= Refresh; _source.OwnCharacterChanged -= Refresh;
        _source.CombatEvent -= Accept;
        lock (_gate) { _entries.Clear(); _snapshot = []; _own = null; _feedCharacter = null; }
    }
}
