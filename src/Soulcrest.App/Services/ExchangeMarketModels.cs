using System.Collections.Immutable;
using System.Net;

namespace Soulcrest.App.Services;

public sealed record ExchangeMarketServer(int ServerId, string Name, string Region, string RegionName,
    string Faction, bool HasData);

/// <summary>A captured lowest listed unit price, not a sale, variant appraisal or wallet transaction.</summary>
public sealed record ExchangeMarketQuote(int ServerId, int ItemId, decimal UnitPrice, int Listings,
    DateTimeOffset CapturedAt, DateTimeOffset FetchedAt);

/// <summary>A past listed unit price. It never represents a currently available offer.</summary>
public sealed record ExchangeMarketHistoricalQuote(int ServerId, int ItemId, decimal UnitPrice,
    DateTimeOffset LastSeenAt, DateTimeOffset FetchedAt);

public sealed record ExchangeMarketQuoteDetails(ImmutableArray<ExchangeMarketQuote> CurrentQuotes,
    ImmutableArray<ExchangeMarketHistoricalQuote> HistoricalQuotes);

public interface IExchangeMarketClient
{
    Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken);
    Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds,
        CancellationToken cancellationToken);
}

public interface IExchangeMarketDetailsClient : IExchangeMarketClient
{
    Task<ExchangeMarketQuoteDetails> GetQuoteDetailsAsync(int serverId, IReadOnlyCollection<int> itemIds,
        CancellationToken cancellationToken);
}

public sealed class ExchangeMarketApiException(string message, HttpStatusCode? statusCode = null,
    TimeSpan? retryAfter = null, Exception? innerException = null) : Exception(message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
