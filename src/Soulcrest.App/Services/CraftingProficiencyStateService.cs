using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public sealed record CraftingProficiencySnapshot(string? ProfileId,
    ImmutableDictionary<byte, CharacterCraftingProficiency> Proficiencies, bool IsFresh,
    DateTimeOffset? ObservedAt, long Generation, long Sequence, NetworkCombatSourceStatus Status, string Source)
{
    public bool IsKnown => ObservedAt is not null && Status == NetworkCombatSourceStatus.Complete;
    public bool UsingSaved => Source == "saved-own-entry-crafting-proficiencies";
    public int? Level(byte category) => Proficiencies.TryGetValue(category, out var value) ? value.Level : null;
    public static CraftingProficiencySnapshot Empty { get; } = new(null,
        ImmutableDictionary<byte, CharacterCraftingProficiency>.Empty, false, null, 0, 0, NetworkCombatSourceStatus.Unknown, "unknown");
}

/// <summary>Passive projection of the last complete own-entry crafting list, plus separately labelled local history.</summary>
public sealed class CraftingProficiencyStateService : IDisposable
{
    public const string LiveSource = "own-entry-crafting-proficiencies";
    private readonly INetworkCharacterCombatSource _source;
    private readonly Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> _resolve;
    private readonly Func<string?> _selected;
    private readonly ExplorationService? _profiles;
    private readonly CraftingProficiencyStore _store;
    private NetworkCombatDomain<ImmutableDictionary<byte, CharacterCraftingProficiency>>? _lastPersistenceDomain;
    private string? _lastPersistenceProfile;
    private sealed record Publication(CraftingProficiencySnapshot Snapshot, string? StorageError);
    private Publication _published = new(CraftingProficiencySnapshot.Empty, null);
    private int _refreshing, _refreshPending;
    private bool _disposed;
    public event Action? Changed;
    public string? StorageError { get; private set; }

    public CraftingProficiencyStateService(INetworkCharacterCombatSource source, ExplorationService exploration,
        CraftingProficiencyStore store) : this(source, exploration.TryGetBoundCharacter, () => exploration.ActiveId, store)
    {
        _profiles = exploration;
        exploration.Changed += Refresh;
        Refresh();
    }

    internal CraftingProficiencyStateService(INetworkCharacterCombatSource source,
        Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> resolve, Func<string?> selected,
        CraftingProficiencyStore store)
    {
        _source = source; _resolve = resolve; _selected = selected; _store = store;
        source.Changed += Refresh; source.OwnCharacterChanged += Refresh;
        Refresh();
    }

    public CraftingProficiencySnapshot Snapshot
    {
        get
        {
            if (Volatile.Read(ref _disposed)) return CraftingProficiencySnapshot.Empty;
            var evidence = _source.CombatSnapshot;
            var domain = evidence.CraftingProficiencies;
            var own = evidence.Character;
            var profile = own is null ? null : _resolve(own);
            var selected = _selected();
            if (own is null || profile?.ProfileId is not { Length: > 0 } id
                || !Current(evidence) || !ReferenceEquals(evidence, _source.CombatSnapshot))
                return Saved ?? CraftingProficiencySnapshot.Empty with { ProfileId = selected };
            var result = new CraftingProficiencySnapshot(id, domain.Value!, true, domain.ObservedAt,
                evidence.Generation, domain.Sequence, domain.Status, domain.Source);
            return !Volatile.Read(ref _disposed) && _resolve(own)?.ProfileId == id
                && Current(evidence) ? result : CraftingProficiencySnapshot.Empty;
        }
    }

    /// <summary>Historical only, including after restart; never supplied as fresh network evidence.</summary>
    public CraftingProficiencySnapshot? Saved
    {
        get
        {
            if (Volatile.Read(ref _disposed)) return null;
            var selected = _selected(); var saved = _store.Read(selected);
            return saved is null || selected != _selected() || Volatile.Read(ref _disposed) ? null
                : new(saved.ProfileId, saved.Proficiencies, false, saved.ObservedAt, 0, 0,
                    NetworkCombatSourceStatus.Complete, "saved-own-entry-crafting-proficiencies");
        }
    }

    private bool Current(NetworkCharacterCombatSnapshot evidence) => _source.Running
        && ReferenceEquals(evidence, _source.CombatSnapshot) && evidence.Character is { Generation: > 0 } own
        && own.Generation == evidence.Generation
        && evidence.CraftingProficiencies is { Status: NetworkCombatSourceStatus.Complete, IsFresh: true,
            Value: not null, ObservedAt: not null, Sequence: > 0, Source: LiveSource }
        && own.ObservedAt == evidence.CraftingProficiencies.ObservedAt;

    private void Refresh()
    {
        if (Volatile.Read(ref _disposed)) return;
        Volatile.Write(ref _refreshPending, 1);
        // Producer callbacks may be reentrant or concurrent. Never wait on a local lock while
        // reading the network/profile producers: the network may be raising its event under its gate.
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        try
        {
            while (!Volatile.Read(ref _disposed) && Interlocked.Exchange(ref _refreshPending, 0) != 0)
            {
                RefreshOnce();
                var next = new Publication(Snapshot, StorageError);
                var previous = Volatile.Read(ref _published);
                if (next.StorageError == previous.StorageError && Equivalent(next.Snapshot, previous.Snapshot)) continue;
                Volatile.Write(ref _published, next);
                if (!Volatile.Read(ref _disposed)) SafeEvent.Raise(Changed, "Crafting-Fertigkeit");
            }
        }
        finally { Volatile.Write(ref _refreshing, 0); }
        if (!Volatile.Read(ref _disposed) && Volatile.Read(ref _refreshPending) != 0) Refresh();
    }

    private static bool Equivalent(CraftingProficiencySnapshot left, CraftingProficiencySnapshot right) =>
        left.ProfileId == right.ProfileId && left.IsFresh == right.IsFresh && left.ObservedAt == right.ObservedAt
        && left.Generation == right.Generation && left.Sequence == right.Sequence && left.Status == right.Status
        && left.Source == right.Source && (ReferenceEquals(left.Proficiencies, right.Proficiencies)
            || left.Proficiencies.Count == right.Proficiencies.Count && left.Proficiencies.All(pair =>
                right.Proficiencies.TryGetValue(pair.Key, out var value) && pair.Value == value));

    private void RefreshOnce()
    {
        var evidence = _source.CombatSnapshot;
        var snapshot = Snapshot;
        if (snapshot.IsFresh && snapshot.ProfileId is { } id && snapshot.ObservedAt is { } at
            && (!ReferenceEquals(evidence.CraftingProficiencies, _lastPersistenceDomain) || id != _lastPersistenceProfile)
            && Current(evidence))
        {
            // A failed write is retried on the next own-domain/profile fact, never on every HP,
            // loot or combat callback. The existing error remains visible until that retry succeeds.
            _lastPersistenceDomain = evidence.CraftingProficiencies; _lastPersistenceProfile = id;
            try
            {
                _store.Save(new(id, at, snapshot.Proficiencies));
                StorageError = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException or NotSupportedException)
            { StorageError = "Crafting proficiency history could not be saved."; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Volatile.Write(ref _disposed, true);
        _source.Changed -= Refresh; _source.OwnCharacterChanged -= Refresh;
        if (_profiles is not null) _profiles.Changed -= Refresh;
    }
}
