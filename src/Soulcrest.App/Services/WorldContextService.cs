using Soulcrest.Core.Instances;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public enum WorldContextStatus { Unknown, Transitioning, Confirmed, Stale }
public enum WorldContextKind { Unknown, OpenWorld, Instance }

/// <summary>A transient own-world observation, independent of visual map selection and coordinates.</summary>
public sealed record WorldContextSnapshot(WorldContextStatus Status, WorldContextKind Kind,
    int? MapDataId, int? DungeonId, string? Category, string? Mode, string? NameDe, string? NameEn,
    long Generation, DateTimeOffset? ObservedAt, string Source)
{
    public string? SoulcrestMapId { get; init; }
    public bool? InstanceLayer { get; init; }
    /// <summary>Local own/transport authority revision, not a game instance or run identifier.</summary>
    public long? BindingGeneration { get; init; }
    public string? CategoryNameDe { get; init; }
    public string? CategoryNameEn { get; init; }
    public string? ModeNameDe { get; init; }
    public string? ModeNameEn { get; init; }
}

/// <summary>Only a complete Own can confirm location. No TTL, persisted run ID, or capture of its own.</summary>
public sealed class WorldContextService
{
    private readonly object _gate = new();
    private readonly InstanceCatalog _catalog;
    private long _sourceGeneration = -1;
    private WorldContextSnapshot _snapshot = new(WorldContextStatus.Unknown, WorldContextKind.Unknown,
        null, null, null, null, null, null, 0, null, "unobserved");

    public WorldContextService() : this(InstanceCatalog.Default) { }
    public WorldContextService(InstanceCatalog catalog) => _catalog = catalog;
    public WorldContextSnapshot Snapshot => Volatile.Read(ref _snapshot);
    /// <summary>
    /// False only without any world classification. Public builds embed the nameless map-kind table
    /// (user decision 2026-10-28) instead of the private catalogue; it classifies open world, instances
    /// and Abyss for map tracking, HUDs, loot and rank context. Area names then come from the map package.
    /// </summary>
    public bool HasLocationFacts => _catalog.HasData;
    public event Action? Changed;

    // Only callback-free target mutations may run here. Notifications must happen after the lease,
    // otherwise a subscriber could invert Network -> World into World -> Network.
    internal bool TryApplyWhileCurrent(WorldContextSnapshot expected, Func<bool> mutation)
    {
        lock (_gate)
            return ReferenceEquals(_snapshot, expected) && mutation();
    }

    internal void ObserveOwn(int? mapDataId, long sourceGeneration, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (sourceGeneration < _sourceGeneration) return;
            _sourceGeneration = sourceGeneration;
            var map = mapDataId is > 0 ? _catalog.FindMap(mapDataId.Value) : null;
            var kind = map?.Kind switch
            {
                InstanceMapKind.OpenWorld => WorldContextKind.OpenWorld,
                InstanceMapKind.Instance => WorldContextKind.Instance,
                _ => WorldContextKind.Unknown
            };
            // A single catalog candidate does not prove exclusive map ownership or an active mode.
            Volatile.Write(ref _snapshot, new(mapDataId is > 0 ? WorldContextStatus.Confirmed : WorldContextStatus.Unknown,
                kind, mapDataId is > 0 ? mapDataId : null, null, map?.Category,
                null, map?.Names.De, map?.Names.En,
                _snapshot.Generation + 1, at, "own-world")
                { SoulcrestMapId = map?.SoulcrestMapId, BindingGeneration = sourceGeneration,
                  CategoryNameDe = map?.CategoryNames?.De, CategoryNameEn = map?.CategoryNames?.En });
        }
        SafeEvent.Raise(Changed, "Weltkontext");
    }

    internal void ObserveDungeon(DungeonStateSnapshot state, long sourceGeneration, DateTimeOffset at)
    {
        lock (_gate)
        {
            var current = _snapshot;
            if (!CanRefine(current, sourceGeneration, at) || current.MapDataId is not { } mapId) return;
            var map = _catalog.FindMap(mapId);
            var dungeon = _catalog.FindDungeon(state.DungeonId);
            // A known unrelated map state is not a world transition or a matching refinement.
            if (dungeon is not null && !dungeon.MapIds.Contains(mapId)) return;
            if (dungeon is null || dungeon.Active == false
                || map?.Kind == InstanceMapKind.OpenWorld && dungeon.InstanceLayer != true)
            {
                if (!ResetRefinementLocked(current, at)) return;
            }
            else
            {
                if (dungeon.InstanceLayer == true && (state.State is DungeonRunState.None or DungeonRunState.End)
                    && (current.Kind != WorldContextKind.Instance || current.DungeonId != dungeon.Id)) return;
                // End/Complete never imply exit. Open-world layers require the native positive layer flag.
                var categoryNames = dungeon.Category is not null ? dungeon.CategoryNames : map?.CategoryNames;
                var next = current with { Kind = WorldContextKind.Instance, DungeonId = dungeon.Id, InstanceLayer = dungeon.InstanceLayer,
                    Category = dungeon.Category ?? map?.Category, Mode = dungeon.Mode,
                    NameDe = dungeon.Names.De ?? map?.Names.De, NameEn = dungeon.Names.En ?? map?.Names.En,
                    CategoryNameDe = categoryNames?.De, CategoryNameEn = categoryNames?.En,
                    ModeNameDe = dungeon.Mode is not null ? dungeon.ModeNames?.De : null,
                    ModeNameEn = dungeon.Mode is not null ? dungeon.ModeNames?.En : null };
                if (next == current) return;
                Volatile.Write(ref _snapshot, next with { Generation = current.Generation + 1, ObservedAt = at, Source = "dungeon-state" });
            }
        }
        SafeEvent.Raise(Changed, "Weltkontext");
    }

    internal void RejectDungeonRefinement(long sourceGeneration, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!CanRefine(_snapshot, sourceGeneration, at) || !ResetRefinementLocked(_snapshot, at)) return;
        }
        SafeEvent.Raise(Changed, "Weltkontext");
    }

    private bool CanRefine(WorldContextSnapshot current, long sourceGeneration, DateTimeOffset at) =>
        sourceGeneration == _sourceGeneration && current.Status == WorldContextStatus.Confirmed
        && current.MapDataId is not null && (current.ObservedAt is null || at >= current.ObservedAt);

    private bool ResetRefinementLocked(WorldContextSnapshot current, DateTimeOffset at)
    {
        if (current.DungeonId is null) return false;
        var map = _catalog.FindMap(current.MapDataId!.Value);
        var kind = map?.Kind switch
        {
            InstanceMapKind.OpenWorld => WorldContextKind.OpenWorld,
            InstanceMapKind.Instance => WorldContextKind.Instance,
            _ => WorldContextKind.Unknown
        };
        Volatile.Write(ref _snapshot, new(WorldContextStatus.Confirmed, kind, current.MapDataId, null,
            map?.Category, null, map?.Names.De, map?.Names.En, current.Generation + 1, at, "unsupported-dungeon-state")
            { SoulcrestMapId = map?.SoulcrestMapId, BindingGeneration = current.BindingGeneration,
              CategoryNameDe = map?.CategoryNames?.De, CategoryNameEn = map?.CategoryNames?.En });
        return true;
    }

    internal void Invalidate(WorldContextStatus status, long sourceGeneration, string source, DateTimeOffset? at = null)
    {
        if (status == WorldContextStatus.Confirmed) throw new ArgumentException("Invalidation cannot confirm a world.", nameof(status));
        lock (_gate)
        {
            if (sourceGeneration < _sourceGeneration) return;
            if (sourceGeneration == _sourceGeneration && _snapshot.Status == status && _snapshot.Source == source) return;
            _sourceGeneration = sourceGeneration;
            Volatile.Write(ref _snapshot, new(status, WorldContextKind.Unknown, null, null, null, null, null, null,
                _snapshot.Generation + 1, at ?? DateTimeOffset.UtcNow, source) { BindingGeneration = sourceGeneration });
        }
        SafeEvent.Raise(Changed, "Weltkontext");
    }
}
