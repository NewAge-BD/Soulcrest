using System.Collections.Immutable;

namespace Soulcrest.App.Services;

/// <summary>A local committed timeline fact and the market assumptions captured with its own activity.</summary>
public sealed record CommittedLootFact(string CanonicalSessionId, string ProfileId,
    LootTrackerTimelineEvent Fact, ExchangeMarketProfileSettings MarketProfile);

public interface ICommittedLootFactSource
{
    LootTrackerSnapshot Snapshot { get; }
    event Action? Changed;
    event Action<CommittedLootFact>? LootFactCommitted;
    event Action<string>? SessionDeleted;
}

public enum ExchangeLootValueStatus { Pending, Estimated, ReferenceOnly, Unknown }

/// <summary>External item-template estimate; never an own observed sale or wallet transaction.</summary>
public sealed record ExchangeLootValuationEntry(string SessionId, string ProfileId, string FactId,
    int ItemId, long Quantity, DateTimeOffset DroppedAt, ExchangeMarketProfileSettings Model,
    ExchangeLootValueStatus Status)
{
    public const string Provider = "aion2.exchange";
    public string Source { get; init; } = Provider;
    public string? ServerName { get; init; }
    public decimal? UnitPrice { get; init; }
    public long? GrossKina { get; init; }
    public long? NetKina { get; init; }
    public int? Listings { get; init; }
    public DateTimeOffset? SourceCapturedAt { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public DateTimeOffset? ValuedAt { get; init; }
    public string? UnknownReason { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => FactId + ":" + ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record ExchangeLootValuationSnapshot(ImmutableArray<ExchangeLootValuationEntry> Entries,
    string Status, bool StatusIsError, bool OnlineEnabled, int PendingCount);

public sealed record ExchangeLootValuationBasis(int ServerId, string? ServerName, string Region,
    string Faction, decimal SalesTaxPercent, decimal RegistrationFeePercent);

public sealed record ExchangeLootValuationSummary(long? NetKina, long? GrossKina,
    int PricedRows, int EligibleRows, int UnknownRows, bool Complete, int PendingRows,
    bool ReferenceOnly, DateTimeOffset? OldestObservedAt, DateTimeOffset? NewestObservedAt)
{
    public long? ReferenceGrossKina { get; init; }
    public long? ReferenceNetKina { get; init; }
    public int ReferenceRows { get; init; }
    public long? PricedQuantity { get; init; }
    public long? EligibleQuantity { get; init; }
    public long? UnknownQuantity { get; init; }
    public long? PendingQuantity { get; init; }
    public ImmutableArray<ExchangeLootValuationBasis> Bases { get; init; } = [];
}

public static class ExchangeLootValuation
{
    public static bool TryItemId(string key, out int itemId)
    {
        itemId = 0;
        return (key.StartsWith("Item:", StringComparison.Ordinal) || key.StartsWith("item:", StringComparison.Ordinal))
            && int.TryParse(key.AsSpan(5), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out itemId) && itemId > 0;
    }

    public static bool Eligible(LootTrackerLoot row)
    {
        if (row.Kind != LootTrackerLootKind.Item || row.Quantity <= 0 || !TryItemId(row.Id, out var id)) return false;
        var catalog = MonsterLootItemCatalog.Default;
        var trade = catalog.TradeFacts(id);
        return !catalog.IsAutomaticallyConsumedSoul(id) && !catalog.IsCurrency(id)
            && trade.HasTradeCategory != false
            && trade.ExchangeType is not (LootItemExchangeType.NonExchangeable or LootItemExchangeType.SoulBindOnAcquire);
    }

    public static ExchangeLootValuationEntry? Evaluate(ExchangeLootValuationEntry entry,
        ExchangeMarketQuote quote, DateTimeOffset now, string? serverName = null)
    {
        if (entry.Status != ExchangeLootValueStatus.Pending || !entry.Model.HasServerSelection || !entry.Model.HasValidFees
            || quote.ServerId != entry.Model.ServerId || quote.ItemId != entry.ItemId || quote.UnitPrice <= 0
            || quote.Listings <= 0 || !ExchangeMarketCache.IsUsable(quote, now) || entry.Quantity <= 0) return null;
        try
        {
            var grossDecimal = checked(quote.UnitPrice * entry.Quantity);
            var gross = checked((long)decimal.Floor(grossDecimal));
            var reference = MonsterLootItemCatalog.Default.IsGear(entry.ItemId);
            var net = checked((long)decimal.Floor(grossDecimal
                * (1m - entry.Model.SalesTaxPercent / 100m - entry.Model.RegistrationFeePercent / 100m)));
            return entry with
            {
                Status = reference ? ExchangeLootValueStatus.ReferenceOnly : ExchangeLootValueStatus.Estimated,
                ServerName = serverName, UnitPrice = quote.UnitPrice, GrossKina = gross, NetKina = net,
                Listings = quote.Listings, SourceCapturedAt = quote.CapturedAt, FetchedAt = quote.FetchedAt,
                ValuedAt = now, UnknownReason = null, NextAttemptAt = null,
            };
        }
        catch (OverflowException) { return null; }
    }
}
