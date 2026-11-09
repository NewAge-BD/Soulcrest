using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pantheon;

namespace Soulcrest.App.Services;

/// <summary>Own-bound decoration display and character-local history; never a source of game requests.</summary>
public sealed class PantheonStateService : IDisposable
{
    private readonly INetworkPantheonSource _source;
    private readonly INetworkCharacterCombatSource _own;
    private readonly ExplorationService _profiles;
    private readonly object _gate = new();
    private sealed record Publication(PantheonSnapshot Snapshot, NetworkPantheonSnapshot? Network = null,
        OwnCharacterContext? Own = null, CharacterPantheonHistory? History = null);
    private PantheonSnapshot _snapshot = PantheonSnapshot.Empty;
    private Publication _publication = new(PantheonSnapshot.Empty);
    private int _refreshing, _pending;
    private bool _disposed;
    public event Action? Changed;

    public PantheonStateService(INetworkPantheonSource source, INetworkCharacterCombatSource own, ExplorationService profiles)
    {
        _source = source; _own = own; _profiles = profiles;
        source.Changed += Refresh; own.Changed += Refresh; own.OwnCharacterChanged += Refresh; profiles.Changed += Refresh;
        Refresh();
    }

    public PantheonSnapshot Snapshot
    {
        get
        {
            Refresh();
            Publication publication;
            lock (_gate) { if (_disposed) return PantheonSnapshot.Empty; publication = _publication; }
            // A reentrant/parallel read cannot wait for Refresh, but its old cache is not authority.
            // All producer/profile reads stay outside our gate, including the final return fence.
            if (!CanReturn(publication)) return PantheonSnapshot.Empty;
            lock (_gate) if (_disposed || !ReferenceEquals(publication, _publication)) return PantheonSnapshot.Empty;
            return CanReturn(publication) ? publication.Snapshot : PantheonSnapshot.Empty;
        }
    }

    private bool CanReturn(Publication publication)
    {
        if (Volatile.Read(ref _disposed)) return false;
        var value = publication.Snapshot;
        var activeId = _profiles.ActiveId;
        if (value.ProfileId != activeId) return false;
        if (value.SourceKind == PantheonSourceKind.Live)
            return publication.Network is { } network && publication.Own is { } own
                && LiveSource(network, own) && SameOwn(network, own, activeId)
                && _profiles.ActiveId == activeId;
        if (value.SourceKind == PantheonSourceKind.Saved)
            return publication.History is { } history
                && ReferenceEquals(_profiles.TryGetSavedPantheon(activeId)?.State, history)
                && _profiles.ActiveId == activeId;
        return value.SourceKind == PantheonSourceKind.Unknown && _profiles.ActiveId == activeId;
    }

    private void Refresh()
    {
        if (Volatile.Read(ref _disposed)) return;
        Volatile.Write(ref _pending, 1);
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        var changed = false;
        try
        {
            while (!Volatile.Read(ref _disposed) && Interlocked.Exchange(ref _pending, 0) != 0)
            {
                var next = Read();
                lock (_gate)
                {
                    if (_disposed) break;
                    if (!Equivalent(_snapshot, next.Snapshot)) { _snapshot = next.Snapshot; changed = true; }
                    if (!ReferenceEquals(_publication.Snapshot, _snapshot) || !ReferenceEquals(_publication.Network, next.Network)
                        || !ReferenceEquals(_publication.Own, next.Own) || !ReferenceEquals(_publication.History, next.History))
                        _publication = next with { Snapshot = _snapshot };
                }
            }
        }
        finally { Volatile.Write(ref _refreshing, 0); }
        if (!Volatile.Read(ref _disposed) && Volatile.Read(ref _pending) != 0) Refresh();
        if (changed && !Volatile.Read(ref _disposed)) SafeEvent.Raise(Changed, "Pantheon");
    }

    private Publication Read()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var activeId = _profiles.ActiveId;
            var network = _source.PantheonSnapshot;
            var own = _own.CombatSnapshot.Character;
            var bound = own is null ? null : _profiles.TryGetBoundCharacter(own);
            var domain = network.Decorations;
            var live = own is not null && LiveSource(network, own) && !string.IsNullOrWhiteSpace(bound?.ProfileId);
            if (live && SameOwn(network, own!, bound!.ProfileId!))
                _profiles.ApplyNetworkPantheon(own!, bound!.ProfileId!, domain);
            if (activeId != _profiles.ActiveId) continue;
            if (live && bound!.ProfileId == activeId && SameOwn(network, own!, activeId))
            {
                var current = Project(activeId, domain.Value!.Decorations, domain.ObservedAt!.Value, PantheonSourceKind.Live);
                if (activeId == _profiles.ActiveId && SameOwn(network, own!, activeId)) return new(current, network, own);
                continue;
            }
            var saved = _profiles.TryGetSavedPantheon(activeId);
            if (activeId != _profiles.ActiveId) continue;
            var result = saved is null ? PantheonSnapshot.Empty with { ProfileId = activeId }
                : Project(activeId, saved.State.Decorations, saved.State.ObservedAt, PantheonSourceKind.Saved);
            if (activeId == _profiles.ActiveId && _profiles.TryGetSavedPantheon(activeId)?.State == saved?.State)
                return new(result, History: saved?.State);
        }
        return new(PantheonSnapshot.Empty);
    }

    private bool LiveSource(NetworkPantheonSnapshot network, OwnCharacterContext own) => !Volatile.Read(ref _disposed)
        && _source.Running && _own.Running && ReferenceEquals(network.Character, own)
        && own.Generation > 0 && network.Generation == own.Generation && network.Sequence > 0
        && network.Decorations is { IsFresh: true, Status: NetworkCombatSourceStatus.Complete,
            Value: not null, ObservedAt: not null, Sequence: > 0 } domain
        && domain.Sequence <= network.Sequence && ExplorationService.PantheonSource(domain.Source);

    private bool SameOwn(NetworkPantheonSnapshot network, OwnCharacterContext own, string profileId)
    {
        return !Volatile.Read(ref _disposed) && _source.Running && _own.Running
            && OwnIsCurrent(own) && ReferenceEquals(network, _source.PantheonSnapshot)
            && network.Generation == own.Generation && _profiles.TryGetBoundCharacter(own)?.ProfileId == profileId
            && ReferenceEquals(network, _source.PantheonSnapshot) && OwnIsCurrent(own)
            && _source.Running && _own.Running;
    }

    private bool OwnIsCurrent(OwnCharacterContext own)
    {
        var current = _own.CombatSnapshot;
        return ReferenceEquals(own, current.Character) && current.Generation == own.Generation;
    }

    internal static PantheonSnapshot Project(string profileId, ImmutableDictionary<int, byte> decorations,
        DateTimeOffset at, PantheonSourceKind sourceKind)
    {
        var catalog = PantheonCatalog.Default;
        var placed = decorations.Where(pair => pair.Value != 0).ToArray();
        // The meaning of slot zero and a full current placement is qualified separately
        // by the network adapter. Never promote arbitrary preset pages to current equipment.
        var slotsValid = placed.All(pair => catalog.Slots.Any(slot => slot.Id == pair.Value))
            && placed.Select(pair => pair.Value).Distinct().Count() == placed.Length;
        var slots = catalog.Slots.Select(slot =>
        {
            int? decorId = slotsValid && placed.FirstOrDefault(pair => pair.Value == slot.Id) is { Key: > 0 } entry ? entry.Key : null;
            var known = slotsValid && (decorId is null || catalog.Find(decorId.Value) is { } decor && decor.Category == slot.Category);
            return new PantheonSlotView(slot.Id, slot.Category, known, known ? decorId : null);
        }).ToArray();
        var effectsKnown = slotsValid && slots.All(slot => slot.IsKnown);
        var effects = effectsKnown ? placed.SelectMany(pair => catalog.Find(pair.Key)!.Effects)
            .GroupBy(effect => effect.StatId, StringComparer.Ordinal)
            .Select(group => new PantheonGodEffect(group.Key, checked(group.Sum(effect => effect.Amount))))
            .OrderBy(effect => effect.StatId, StringComparer.Ordinal).ToArray() : [];
        return new()
        {
            ProfileId = profileId, SourceKind = sourceKind, ObservedAt = at, IsOwnershipKnown = true,
            OwnedIds = decorations.Keys.ToImmutableHashSet(), Slots = Array.AsReadOnly(slots),
            EquippedEffects = Array.AsReadOnly(effects), AreEffectsKnown = effectsKnown
        };
    }

    private static bool Equivalent(PantheonSnapshot a, PantheonSnapshot b) => a.ProfileId == b.ProfileId
        && a.SourceKind == b.SourceKind && a.ObservedAt == b.ObservedAt && a.IsOwnershipKnown == b.IsOwnershipKnown
        && a.AreEffectsKnown == b.AreEffectsKnown && a.ActivePreset == b.ActivePreset && a.OwnedIds.SetEquals(b.OwnedIds)
        && a.Slots.SequenceEqual(b.Slots) && a.EquippedEffects.SequenceEqual(b.EquippedEffects)
        && a.KnownPresets.SequenceEqual(b.KnownPresets);

    public void Dispose()
    {
        _source.Changed -= Refresh; _own.Changed -= Refresh; _own.OwnCharacterChanged -= Refresh; _profiles.Changed -= Refresh;
        lock (_gate) { _disposed = true; _snapshot = PantheonSnapshot.Empty; _publication = new(PantheonSnapshot.Empty); }
    }
}
