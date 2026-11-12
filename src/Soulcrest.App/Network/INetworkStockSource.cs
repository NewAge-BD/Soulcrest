using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed record NetworkStockScope(StockScope Scope, StockCompleteness Completeness);

public sealed record NetworkStockSnapshot([property: JsonIgnore] OwnCharacterContext? Character, ImmutableDictionary<int, long> Items,
    bool IsFresh, DateTimeOffset? ObservedAt, long Sequence)
{
    public ImmutableArray<NetworkStockScope> Scopes { get; init; } =
        [new(StockScope.Bag, StockCompleteness.Unknown), new(StockScope.CharacterStorage, StockCompleteness.Unknown), new(StockScope.ServerStorage, StockCompleteness.Unknown)];
    public bool Available => Character is not null && ObservedAt is not null;
    public bool IsComplete => Available && Scopes.Length == 3 && Scopes.Select(scope => scope.Scope).Distinct().Count() == 3
        && Scopes.All(scope => Enum.IsDefined(scope.Scope) && scope.Completeness == StockCompleteness.Complete);
    public static NetworkStockSnapshot Empty { get; } = new(null, ImmutableDictionary<int, long>.Empty, false, null, 0);
    public override string ToString() => nameof(NetworkStockSnapshot);
}

/// <summary>Receipt identity is synthetic; native stack/entity identifiers never leave the reducer.</summary>
public sealed record ItemReceipt(string Id, long Sequence, [property: JsonIgnore] OwnCharacterContext Character, int ItemId, long Quantity,
    ItemReceiptKind Kind, DateTimeOffset At, bool CoveredByCommittedLoot = false)
{
    public override string ToString() => nameof(ItemReceipt);
}

public interface INetworkStockSource
{
    bool Running { get; }
    NetworkStockSnapshot StockSnapshot { get; }
    event Action? Changed;
    event Action? OwnCharacterChanged;
    event Action<ItemReceipt>? ItemReceived;
}
