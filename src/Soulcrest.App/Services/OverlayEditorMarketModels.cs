namespace Soulcrest.App.Services;

/// <summary>Current lowest-unit-price estimate with 24-hour averages for missing offers, separate from frozen receipts.</summary>
public sealed record OverlayEditorMarketSnapshot
{
    public string? SessionId { get; init; }
    public string? SourceProfileId { get; init; }
    public ExchangeMarketProfileSettings MarketProfile { get; init; } = new();
    public string? ServerName { get; init; }
    public bool Available { get; init; }
    public bool OnlineEnabled { get; init; }
    public bool AverageFallbackEnabled { get; init; }
    public int CurrentRows { get; init; }
    public int AverageRows { get; init; }
    public bool Uses24HourAverage { get; init; }
    public DateTimeOffset? AverageWindowStart { get; init; }
    public DateTimeOffset? AverageWindowEnd { get; init; }
    public long AverageSamples { get; init; }
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

/// <summary>Pure current/fallback price arithmetic: it never creates or changes a journal or valuation receipt.</summary>
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
        Func<int, ExchangeMarketAveragePrice?> averageFor, DateTimeOffset asOf, Func<int, OverlayEditorMarketItemFacts> facts) =>
        Project(session, model, online, server, _ => null, averageFor, asOf, facts);

    internal static OverlayEditorMarketSnapshot Project(LootTrackerSession session,
        ExchangeMarketProfileSettings model, bool online, ExchangeMarketServer? server,
        Func<int, ExchangeMarketQuote?> quoteFor, Func<int, ExchangeMarketAveragePrice?> averageFor,
        DateTimeOffset asOf, Func<int, OverlayEditorMarketItemFacts> facts)
    {
        var snapshot = new OverlayEditorMarketSnapshot
        {
            SessionId = session.Id, SourceProfileId = session.Character?.ProfileId,
            MarketProfile = model, ServerName = server?.Name, Available = true, OnlineEnabled = online,
            AverageFallbackEnabled = true,
        };
        try
        {
            var items = Items(session, facts);
            long eligibleQuantity = 0, pricedQuantity = 0, net = 0, equipmentNet = 0, samples = 0;
            var pricedRows = 0; var currentRows = 0; var averageRows = 0; var pricedEquipment = false; var stale = false;
            DateTimeOffset? oldest = null, newest = null, windowStart = null, windowEnd = null;
            var validModel = server is not null && model.HasServerSelection && model.HasValidFees
                && server.ServerId == model.ServerId && server.Region == model.Region && server.Faction == model.Faction
                && (!model.AutomaticServerSelection || model.GameServerKey is null || server.GameServerKey == model.GameServerKey);
            foreach (var item in items)
            {
                eligibleQuantity = checked(eligibleQuantity + item.Quantity);
                if (!validModel || item.Id is not { } id || !item.Facts.TradeKnown) continue;
                var quote = quoteFor(id);
                decimal unitPrice; DateTimeOffset observedAt;
                // User refinement 2026-11-10: current offers take priority. History
                // is queried and used only when no valid current offer is available.
                if (quote is not null && quote.ItemId == id && quote.ServerId == model.ServerId
                    && ExchangeMarketCache.IsUsable(quote, asOf))
                {
                    unitPrice = quote.UnitPrice; observedAt = quote.CapturedAt; currentRows++;
                    stale |= !ExchangeMarketCache.IsFresh(quote, asOf);
                }
                else
                {
                    var average = averageFor(id);
                    if (average is null || average.ItemId != id || average.ServerId != model.ServerId
                        || !ExchangeMarketCache.AverageIsUsable(average, asOf)) continue;
                    unitPrice = average.UnitPrice; observedAt = average.RangeEnd; averageRows++;
                    samples = checked(samples + average.Samples);
                    stale |= !ExchangeMarketCache.AverageIsFresh(average, asOf);
                    windowStart = windowStart is null || average.RangeStart < windowStart ? average.RangeStart : windowStart;
                    windowEnd = windowEnd is null || average.RangeEnd > windowEnd ? average.RangeEnd : windowEnd;
                }
                var value = checked((long)decimal.Floor(checked(unitPrice * item.Quantity)
                    * (1m - model.SalesTaxPercent / 100m - model.RegistrationFeePercent / 100m)));
                net = checked(net + value); pricedQuantity = checked(pricedQuantity + item.Quantity); pricedRows++;
                if (item.Facts.Equipment) { equipmentNet = checked(equipmentNet + value); pricedEquipment = true; }
                oldest = oldest is null || observedAt < oldest ? observedAt : oldest;
                newest = newest is null || observedAt > newest ? observedAt : newest;
            }
            var complete = items.Count == pricedRows;
            return snapshot with
            {
                NetKina = complete ? net : null, PartialNetKina = net,
                KnownEstimateNetKina = pricedRows > 0 || complete ? net : null,
                AverageWindowStart = windowStart, AverageWindowEnd = windowEnd, AverageSamples = samples,
                Uses24HourAverage = averageRows > 0, CurrentRows = currentRows, AverageRows = averageRows,
                EligibleRows = items.Count, PricedRows = pricedRows,
                EligibleQuantity = eligibleQuantity, PricedQuantity = pricedQuantity,
                HasEquipmentReference = items.Any(item => item.Facts.Equipment),
                EquipmentReferenceNetKina = pricedEquipment ? equipmentNet : null,
                OldestObservedAt = oldest, NewestObservedAt = newest, Stale = stale,
                Status = !online ? "Online-Marktpreise ausgeschaltet."
                    : !complete ? "Marktpreise sind teilweise unbekannt." : "Marktpreise bereit.",
            };
        }
        catch (OverflowException)
        {
            return snapshot with { Status = "Marktpreise sind teilweise unbekannt.", StatusIsError = true };
        }
    }
}
