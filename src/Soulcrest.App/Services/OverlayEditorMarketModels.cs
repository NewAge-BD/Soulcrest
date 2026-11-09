namespace Soulcrest.App.Services;

/// <summary>A current asking-price estimate of the active session, separate from frozen loot receipts.</summary>
public sealed record OverlayEditorMarketSnapshot
{
    public string? SessionId { get; init; }
    public string? SourceProfileId { get; init; }
    public ExchangeMarketProfileSettings MarketProfile { get; init; } = new();
    public string? ServerName { get; init; }
    public bool Available { get; init; }
    public bool OnlineEnabled { get; init; }
    public long? NetKina { get; init; }
    public long? PartialNetKina { get; init; }
    public long? KnownEstimateNetKina { get; init; }
    public int HistoricalRows { get; init; }
    public long HistoricalQuantity { get; init; }
    public DateTimeOffset? OldestHistoricalSeenAt { get; init; }
    public DateTimeOffset? NewestHistoricalSeenAt { get; init; }
    public int PricedRows { get; init; }
    public int EligibleRows { get; init; }
    public int UnknownRows => EligibleRows - PricedRows;
    public long PricedQuantity { get; init; }
    public long EligibleQuantity { get; init; }
    public long UnknownQuantity => EligibleQuantity - PricedQuantity;
    public int UnvaluedRows => EligibleRows - PricedRows - HistoricalRows;
    public long UnvaluedQuantity => EligibleQuantity - PricedQuantity - HistoricalQuantity;
    public bool HasEquipmentReference { get; init; }
    public long? EquipmentReferenceNetKina { get; init; }
    public DateTimeOffset? OldestObservedAt { get; init; }
    public DateTimeOffset? NewestObservedAt { get; init; }
    public bool Stale { get; init; }
    public bool Loading { get; init; }
    public string Status { get; init; } = "Keine aktive Session.";
    public bool StatusIsError { get; init; }
}

internal readonly record struct OverlayEditorMarketItemFacts(bool Excluded, bool TradeKnown, bool Equipment)
{
    internal static OverlayEditorMarketItemFacts ForItem(int id)
    {
        var catalog = MonsterLootItemCatalog.Default;
        var trade = catalog.TradeFacts(id);
        return new(catalog.IsAutomaticallyConsumedSoul(id) || catalog.IsCurrency(id)
            || trade.HasTradeCategory == false
            || trade.ExchangeType is LootItemExchangeType.NonExchangeable or LootItemExchangeType.SoulBindOnAcquire,
            trade.HasTradeCategory == true && trade.ExchangeType != LootItemExchangeType.Unknown,
            catalog.IsGear(id));
    }
}

/// <summary>Pure current-price arithmetic: it never creates or changes a journal or valuation receipt.</summary>
internal static class OverlayEditorMarketProjection
{
    internal sealed record Item(int? Id, long Quantity, OverlayEditorMarketItemFacts Facts);

    internal static IReadOnlyList<Item> Items(LootTrackerSession session, Func<int, OverlayEditorMarketItemFacts> facts)
    {
        var quantities = new Dictionary<int, long>();
        var result = new List<Item>();
        foreach (var row in session.Loot)
        {
            if (row.Kind != LootTrackerLootKind.Item || row.Quantity <= 0) continue;
            if (!ExchangeLootValuation.TryItemId(row.Id, out var id))
            {
                result.Add(new(null, row.Quantity, new(false, false, false)));
                continue;
            }
            var classification = facts(id);
            if (classification.Excluded) continue;
            quantities[id] = checked(quantities.GetValueOrDefault(id) + row.Quantity);
        }
        result.AddRange(quantities.OrderBy(pair => pair.Key).Select(pair => new Item(pair.Key, pair.Value, facts(pair.Key))));
        return result;
    }

    internal static OverlayEditorMarketSnapshot Project(LootTrackerSession session,
        ExchangeMarketProfileSettings model, bool online, ExchangeMarketServer? server,
        Func<int, ExchangeMarketQuote?> quoteFor, DateTimeOffset asOf, Func<int, OverlayEditorMarketItemFacts> facts,
        Func<int, ExchangeMarketHistoricalQuote?>? historicalFor = null)
    {
        var snapshot = new OverlayEditorMarketSnapshot
        {
            SessionId = session.Id, SourceProfileId = session.Character?.ProfileId,
            MarketProfile = model, ServerName = server?.Name, Available = true, OnlineEnabled = online,
        };
        try
        {
            var items = Items(session, facts);
            long eligibleQuantity = 0, pricedQuantity = 0, net = 0, equipmentNet = 0;
            var pricedRows = 0; var pricedEquipment = false; var stale = false;
            DateTimeOffset? oldest = null, newest = null;
            long estimate = 0, historicalQuantity = 0;
            var historicalRows = 0;
            DateTimeOffset? oldestHistorical = null, newestHistorical = null;
            var validModel = server is not null && model.HasServerSelection && model.HasValidFees
                && server.ServerId == model.ServerId && server.Region == model.Region && server.Faction == model.Faction;
            foreach (var item in items)
            {
                eligibleQuantity = checked(eligibleQuantity + item.Quantity);
                if (!validModel || item.Id is not { } id || !item.Facts.TradeKnown) continue;
                var quote = quoteFor(id);
                if (quote is null || quote.ItemId != id || quote.ServerId != model.ServerId
                    || !ExchangeMarketCache.IsUsable(quote, asOf))
                {
                    // User request 2026-11-08: explicitly historical estimates are
                    // separate from current quotes and never authorize a receipt.
                    var history = historicalFor?.Invoke(id);
                    if (history is null || history.ItemId != id || history.ServerId != model.ServerId
                        || !ExchangeMarketCache.HistoryIsUsable(history, asOf)) continue;
                    var historicalValue = checked((long)decimal.Floor(checked(history.UnitPrice * item.Quantity)
                        * (1m - model.SalesTaxPercent / 100m - model.RegistrationFeePercent / 100m)));
                    estimate = checked(estimate + historicalValue);
                    historicalQuantity = checked(historicalQuantity + item.Quantity); historicalRows++;
                    oldestHistorical = oldestHistorical is null || history.LastSeenAt < oldestHistorical ? history.LastSeenAt : oldestHistorical;
                    newestHistorical = newestHistorical is null || history.LastSeenAt > newestHistorical ? history.LastSeenAt : newestHistorical;
                    continue;
                }
                var value = checked((long)decimal.Floor(checked(quote.UnitPrice * item.Quantity)
                    * (1m - model.SalesTaxPercent / 100m - model.RegistrationFeePercent / 100m)));
                net = checked(net + value); pricedQuantity = checked(pricedQuantity + item.Quantity); pricedRows++;
                estimate = checked(estimate + value);
                if (item.Facts.Equipment) { equipmentNet = checked(equipmentNet + value); pricedEquipment = true; }
                stale |= !ExchangeMarketCache.IsFresh(quote, asOf);
                oldest = oldest is null || quote.CapturedAt < oldest ? quote.CapturedAt : oldest;
                newest = newest is null || quote.CapturedAt > newest ? quote.CapturedAt : newest;
            }
            var complete = items.Count == pricedRows;
            return snapshot with
            {
                NetKina = complete ? net : null, PartialNetKina = net,
                KnownEstimateNetKina = pricedRows + historicalRows > 0 || complete ? estimate : null,
                HistoricalRows = historicalRows, HistoricalQuantity = historicalQuantity,
                OldestHistoricalSeenAt = oldestHistorical, NewestHistoricalSeenAt = newestHistorical,
                EligibleRows = items.Count, PricedRows = pricedRows,
                EligibleQuantity = eligibleQuantity, PricedQuantity = pricedQuantity,
                HasEquipmentReference = items.Any(item => item.Facts.Equipment),
                EquipmentReferenceNetKina = pricedEquipment ? equipmentNet : null,
                OldestObservedAt = oldest, NewestObservedAt = newest, Stale = stale,
                Status = !online ? "Online-Marktpreise ausgeschaltet."
                    : historicalRows > 0 ? "Marktwert enthält historische Ersatzpreise."
                    : !complete ? "Marktpreise sind teilweise unbekannt." : "Marktpreise bereit.",
            };
        }
        catch (OverflowException)
        {
            return snapshot with { Status = "Marktpreise sind teilweise unbekannt.", StatusIsError = true };
        }
    }
}
