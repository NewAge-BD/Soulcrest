namespace Soulcrest.App.Services;

public enum CraftingFailureStage { MarketServers, MarketQuotes, MarketAverages, PriceProjection, Calculation }
public enum CraftingFailureKind { Provider, Transport, Timeout, Data, Input, Overflow, State }

/// <summary>Safe failure facts: never retains exception messages, endpoints or character context.</summary>
public sealed record CraftingCalculationFailure(CraftingFailureStage Stage, CraftingFailureKind Kind,
    int? HttpStatus = null, DateTimeOffset? RetryAt = null)
{
    internal static CraftingCalculationFailure From(Exception error, CraftingFailureStage stage, DateTimeOffset now)
    {
        var api = error as ExchangeMarketApiException;
        var origin = error;
        for (var depth = 0; depth < 8 && origin is ExchangeMarketApiException { InnerException: { } inner }; depth++) origin = inner;
        var status = api?.StatusCode ?? (origin as ExchangeMarketApiException)?.StatusCode ?? (origin as HttpRequestException)?.StatusCode;
        var kind = origin switch
        {
            ExchangeMarketApiException => CraftingFailureKind.Provider,
            HttpRequestException => CraftingFailureKind.Transport,
            OperationCanceledException or TimeoutException => CraftingFailureKind.Timeout,
            InvalidDataException or IOException or System.Text.Json.JsonException => CraftingFailureKind.Data,
            ArgumentException => CraftingFailureKind.Input,
            OverflowException => CraftingFailureKind.Overflow,
            _ => CraftingFailureKind.State
        };
        // The provider controls retry timing; retain only a bounded UTC timestamp.
        var retry = api?.RetryAfter is { } delay && delay >= TimeSpan.Zero && delay <= TimeSpan.FromDays(1)
            ? now + delay : (DateTimeOffset?)null;
        return new(stage, kind, status is { } code ? (int)code : null, retry);
    }
}
