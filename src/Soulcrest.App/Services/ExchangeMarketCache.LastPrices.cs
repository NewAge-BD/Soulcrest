using System.Text.Json;

namespace Soulcrest.App.Services;

public sealed record ExchangeMarketPriceArchiveSummary(bool Persistent, int Quotes, int Averages,
    DateTimeOffset? NewestQuoteObservedAt, DateTimeOffset? NewestAverageWindowEnd);

public sealed partial class ExchangeMarketCache
{
    // User request 2026-11-21: retain the last successful price independently of live-cache expiry.
    private readonly Dictionary<(int Server, int Item), ExchangeMarketQuote> _lastQuotes = [];
    private readonly Dictionary<(int Server, int Item), ExchangeMarketAveragePrice> _lastAveragePrices = [];

    /// <summary>Last observed offer with its original timestamps; not evidence of a current listing.</summary>
    public ExchangeMarketQuote? GetLastQuote(int serverId, int itemId)
    {
        lock (_gate) return _lastQuotes.GetValueOrDefault((serverId, itemId));
    }

    /// <summary>Last observed mean for its original window; not a current rolling 24-hour mean.</summary>
    public ExchangeMarketAveragePrice? GetLastAveragePrice(int serverId, int itemId)
    {
        lock (_gate) return _lastAveragePrices.GetValueOrDefault((serverId, itemId));
    }

    public ExchangeMarketPriceArchiveSummary LastPriceArchive
    {
        get
        {
            lock (_gate) return new(_root is not null, _lastQuotes.Count, _lastAveragePrices.Count,
                _lastQuotes.Values.Select(quote => (DateTimeOffset?)quote.CapturedAt).Max(),
                _lastAveragePrices.Values.Select(price => (DateTimeOffset?)price.RangeEnd).Max());
        }
    }

    private static bool RecordedQuoteValid(ExchangeMarketQuote? quote, DateTimeOffset now) => quote is not null
        && quote.ServerId > 0 && quote.ItemId > 0 && quote.UnitPrice > 0 && quote.Listings > 0
        && ValidTimestamp(quote.CapturedAt, now) && ValidTimestamp(quote.FetchedAt, now)
        && quote.CapturedAt - quote.FetchedAt <= FutureSkew;

    private static bool RecordedAverageValid(ExchangeMarketAveragePrice? price, DateTimeOffset now) => price is not null
        && AverageIsUsable(price, price.FetchedAt) && ValidTimestamp(price.FetchedAt, now)
        && ValidTimestamp(price.RangeEnd, now);

    private void RememberQuote(ExchangeMarketQuote quote, DateTimeOffset now)
    {
        if (!RecordedQuoteValid(quote, now)) return;
        var key = (quote.ServerId, quote.ItemId);
        if (!_lastQuotes.TryGetValue(key, out var prior) || quote.CapturedAt > prior.CapturedAt
            || quote.CapturedAt == prior.CapturedAt && quote.FetchedAt >= prior.FetchedAt) _lastQuotes[key] = quote;
    }

    private void RememberAverage(ExchangeMarketAveragePrice price, DateTimeOffset now)
    {
        if (!RecordedAverageValid(price, now)) return;
        var key = (price.ServerId, price.ItemId);
        if (!_lastAveragePrices.TryGetValue(key, out var prior) || price.RangeEnd > prior.RangeEnd
            || price.RangeEnd == prior.RangeEnd && price.FetchedAt >= prior.FetchedAt) _lastAveragePrices[key] = price;
    }

    private void PruneLastPrices()
    {
        // Bound cardinality, never discard a valid observation merely because it is old.
        foreach (var key in _lastQuotes.OrderByDescending(pair => pair.Value.CapturedAt).ThenByDescending(pair => pair.Value.FetchedAt)
                     .ThenBy(pair => pair.Key.Server).ThenBy(pair => pair.Key.Item).Skip(MaximumQuotes).Select(pair => pair.Key).ToArray())
            _lastQuotes.Remove(key);
        foreach (var key in _lastAveragePrices.OrderByDescending(pair => pair.Value.RangeEnd).ThenByDescending(pair => pair.Value.FetchedAt)
                     .ThenBy(pair => pair.Key.Server).ThenBy(pair => pair.Key.Item).Skip(MaximumQuotes).Select(pair => pair.Key).ToArray())
            _lastAveragePrices.Remove(key);
    }

    private void LoadLastPrices(JsonElement? quotes, JsonElement? averages, DateTimeOffset now)
    {
        LoadArchive<ExchangeMarketQuote>(quotes, quote => RecordedQuoteValid(quote, now),
            quote => (quote.ServerId, quote.ItemId), quote => RememberQuote(quote, now));
        LoadArchive<ExchangeMarketAveragePrice>(averages, price => RecordedAverageValid(price, now),
            price => (price.ServerId, price.ItemId), price => RememberAverage(price, now));
    }

    private static void LoadArchive<T>(JsonElement? value, Func<T, bool> valid,
        Func<T, (int Server, int Item)> key, Action<T> remember)
    {
        // Optional archives are isolated: corruption must not invalidate the existing live cache.
        if (value is not { ValueKind: JsonValueKind.Array } element || element.GetArrayLength() > MaximumQuotes) return;
        try
        {
            var rows = element.Deserialize<T[]>();
            if (rows is null || rows.Any(row => !valid(row)) || rows.Select(key).Distinct().Count() != rows.Length) return;
            foreach (var row in rows) remember(row);
        }
        catch (JsonException) { }
    }

    private static byte[] SerializeBoundedSnapshot(CacheFile snapshot)
    {
        // Preserve existing live/history storage priority and the same total file-size bound.
        var bytes = SerializeLegacyBoundedSnapshot(snapshot with { LastQuotes = [], LastAveragePrices = [] });
        if (bytes.Length > MaximumBytes || snapshot.LastQuotes is not { Length: > 0 } && snapshot.LastAveragePrices is not { Length: > 0 }) return bytes;
        var baseline = JsonSerializer.Deserialize<CacheFile>(bytes)!;
        // Live rows already migrate into the archive on load; store only additional evidence.
        var currentQuotes = baseline.Quotes.ToDictionary(quote => (quote.ServerId, quote.ItemId));
        var currentAverages = (baseline.AveragePrices ?? []).ToDictionary(price => (price.ServerId, price.ItemId));
        snapshot = snapshot with
        {
            LastQuotes = (snapshot.LastQuotes ?? []).Where(quote => currentQuotes.GetValueOrDefault((quote.ServerId, quote.ItemId)) != quote).ToArray(),
            LastAveragePrices = (snapshot.LastAveragePrices ?? []).Where(price => currentAverages.GetValueOrDefault((price.ServerId, price.ItemId)) != price).ToArray(),
        };
        var keys = (snapshot.LastQuotes ?? []).Select(quote => (quote.ServerId, quote.ItemId, Average: false, At: quote.CapturedAt))
            .Concat((snapshot.LastAveragePrices ?? []).Select(price => (price.ServerId, price.ItemId, Average: true, At: price.RangeEnd)))
            .OrderByDescending(row => row.At).ThenBy(row => row.ServerId).ThenBy(row => row.ItemId).ThenBy(row => row.Average)
            .Select(row => (row.ServerId, row.ItemId, row.Average)).ToArray();
        var low = 0;
        var high = keys.Length;
        while (low <= high)
        {
            var count = low + (high - low) / 2;
            var retained = keys.Take(count).ToHashSet();
            var candidate = JsonSerializer.SerializeToUtf8Bytes(baseline with
            {
                LastQuotes = (snapshot.LastQuotes ?? []).Where(quote => retained.Contains((quote.ServerId, quote.ItemId, false))).ToArray(),
                LastAveragePrices = (snapshot.LastAveragePrices ?? []).Where(price => retained.Contains((price.ServerId, price.ItemId, true))).ToArray(),
            });
            if (candidate.Length <= MaximumBytes) { bytes = candidate; low = count + 1; }
            else high = count - 1;
        }
        return bytes;
    }
}
