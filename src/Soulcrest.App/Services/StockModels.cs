using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public enum StockFreshness { Unknown, Fresh, Historical }
public enum StockOrigin { Automatic, ManualOverride, LegacyManual }

public sealed record CraftingStockItem(int ItemId, long Quantity, StockFreshness Freshness,
    StockCompleteness Completeness, StockOrigin Origin)
{
    public DateTimeOffset? ObservedAt { get; init; }
}

public sealed record CraftingStockSnapshot
{
    public string? ProfileId { get; init; }
    public ImmutableDictionary<int, CraftingStockItem> EffectiveItems { get; init; } = ImmutableDictionary<int, CraftingStockItem>.Empty;
    public ImmutableDictionary<int, long> ObservedMinimumItems { get; init; } = ImmutableDictionary<int, long>.Empty;
    public ImmutableDictionary<int, CraftingStockItem> ObservedItems { get; init; } = ImmutableDictionary<int, CraftingStockItem>.Empty;
    public ImmutableDictionary<int, long> ManualItems { get; init; } = ImmutableDictionary<int, long>.Empty;
    public ImmutableArray<NetworkStockScope> Scopes { get; init; } =
        [new(StockScope.Bag, StockCompleteness.Unknown), new(StockScope.CharacterStorage, StockCompleteness.Unknown), new(StockScope.ServerStorage, StockCompleteness.Unknown)];
    public bool Available => ProfileId is not null;
    public bool IsFresh { get; init; }
    public bool IsComplete { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public string? StorageError { get; init; }
    public long Revision { get; init; }
    public static CraftingStockSnapshot Empty { get; } = new();
}

public sealed record StockStoredProfile
{
    public string ProfileId { get; init; } = "";
    public string? OwnerFingerprint { get; init; }
    public ImmutableDictionary<int, long> AutomaticItems { get; init; } = ImmutableDictionary<int, long>.Empty;
    public ImmutableDictionary<int, DateTimeOffset> ItemObservedAt { get; init; } = ImmutableDictionary<int, DateTimeOffset>.Empty;
    public ImmutableDictionary<int, long> Overrides { get; init; } = ImmutableDictionary<int, long>.Empty;
    public ImmutableHashSet<int> IgnoredLegacyItems { get; init; } = [];
    public DateTimeOffset? ObservedAt { get; init; }
    public bool Complete { get; init; }
}
