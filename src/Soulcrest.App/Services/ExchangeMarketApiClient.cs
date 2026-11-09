using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>
/// User request 2026-11-01: optional anonymous GET access to the documented Exchange API only.
/// One shared application instance serializes requests; it never accesses a game/session API.
/// </summary>
public sealed class ExchangeMarketApiClient : IExchangeMarketDetailsClient, IDisposable
{
    public const string ApiBase = "https://aion2.exchange/api/";
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private long? _lastRequest;
    private DateTimeOffset _blockedUntil;
    private HttpStatusCode? _blockedStatus;
    private int _failures;

    public ExchangeMarketApiClient(TimeProvider? clock = null) : this(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }, clock) { }

    /// <summary>Injected handlers are for isolated tests; the production handler disables redirects/cookies.</summary>
    internal ExchangeMarketApiClient(HttpMessageHandler handler, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("servers", cancellationToken).ConfigureAwait(false);
        try
        {
            var root = document.RootElement;
            var rows = Required(root, "servers", JsonValueKind.Array);
            if (rows.GetArrayLength() > 2048) throw Invalid();
            var result = ImmutableArray.CreateBuilder<ExchangeMarketServer>();
            var seen = new HashSet<int>();
            foreach (var row in rows.EnumerateArray())
            {
                var id = PositiveInt(row, "server_id");
                if (!seen.Add(id)) throw Invalid();
                result.Add(new(id, Text(row, "name", false), Text(row, "region", true),
                    Text(row, "region_name", true), Text(row, "faction", true), Boolean(row, "has_data")));
            }
            return result.ToImmutable();
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or KeyNotFoundException)
        { throw Invalid(error); }
    }

    public async Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId,
        IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken) =>
        (await GetQuoteDetailsAsync(serverId, itemIds, cancellationToken).ConfigureAwait(false)).CurrentQuotes;

    public async Task<ExchangeMarketQuoteDetails> GetQuoteDetailsAsync(int serverId,
        IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (serverId <= 0) throw new ArgumentOutOfRangeException(nameof(serverId));
        if (itemIds.Count > 20_000 || itemIds.Any(id => id <= 0)) throw new ArgumentOutOfRangeException(nameof(itemIds));
        cancellationToken.ThrowIfCancellationRequested();
        var result = ImmutableArray.CreateBuilder<ExchangeMarketQuote>();
        var historical = ImmutableArray.CreateBuilder<ExchangeMarketHistoricalQuote>();
        foreach (var batch in itemIds.Distinct().Order().Chunk(100))
        {
            var keys = string.Join(',', batch.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            if (keys.Length > 1500) throw new ArgumentOutOfRangeException(nameof(itemIds));
            using var document = await GetAsync($"items?server={serverId.ToString(CultureInfo.InvariantCulture)}&keys={keys}&limit=100", cancellationToken).ConfigureAwait(false);
            var fetchedAt = _clock.GetUtcNow();
            try
            {
                var rows = Required(document.RootElement, "items", JsonValueKind.Array);
                if (rows.GetArrayLength() > batch.Length) throw Invalid();
                var requested = batch.ToHashSet();
                var seen = new HashSet<int>();
                foreach (var row in rows.EnumerateArray())
                {
                    var id = PositiveInt(row, "item_key");
                    if (!requested.Contains(id) || !seen.Add(id)) throw Invalid();
                    var listed = Boolean(row, "listed");
                    var listings = Required(row, "listings", JsonValueKind.Number).GetInt32();
                    if (listings < 0) throw Invalid();
                    var priceElement = row.GetProperty("price");
                    decimal? price = priceElement.ValueKind == JsonValueKind.Null ? null : priceElement.GetDecimal();
                    if (price < 0) throw Invalid();
                    // Optional history is a separate basis, never a substitute current quote.
                    if (HistoricalQuote(row, serverId, id, fetchedAt) is { } previous) historical.Add(previous);
                    // A valid absence is not a zero quote; unobserved/unlisted rows may have no time.
                    if (!listed || listings == 0 || price is null or 0) continue;
                    var capturedAt = Timestamp(row, "captured_at");
                    if (capturedAt > fetchedAt + ExchangeMarketCache.FutureSkew) throw Invalid();
                    result.Add(new(serverId, id, price.Value, listings, capturedAt, fetchedAt));
                }
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or KeyNotFoundException or OverflowException)
            { throw Invalid(error); }
        }
        return new(result.ToImmutable(), historical.ToImmutable());
    }

    private async Task<JsonDocument> GetAsync(string relativePath, CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            if (now < _blockedUntil)
                throw new ExchangeMarketApiException("Exchange API retry is deferred.", _blockedStatus, _blockedUntil - now);
            if (_lastRequest is { } previous)
            {
                var remaining = MinimumInterval - _clock.GetElapsedTime(previous);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, _clock, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _lastRequest = _clock.GetTimestamp();
            using var deadline = new CancellationTokenSource(RequestTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(ApiBase), relativePath));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Soulcrest", "1.0"));
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } finalUri &&
                    (finalUri.Scheme != "https" || finalUri.Host != "aion2.exchange" || !finalUri.IsDefaultPort)) throw Invalid();
                if (!response.IsSuccessStatusCode)
                {
                    TimeSpan? retry = null;
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
                    {
                        _failures = Math.Min(_failures + 1, 5);
                        var header = response.Headers.RetryAfter;
                        retry = header?.Delta ?? (header?.Date is { } at ? at - _clock.GetUtcNow() : null);
                        // The provider's Retry-After is a minimum, even when it exceeds our own backoff cap.
                        retry = retry is { } providerDelay && providerDelay >= TimeSpan.Zero
                            ? TimeSpan.FromSeconds(Math.Max(5, providerDelay.TotalSeconds))
                            : TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, _failures - 1)));
                        now = _clock.GetUtcNow();
                        _blockedUntil = retry.Value.Ticks > DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks
                            ? DateTimeOffset.MaxValue : now + retry.Value;
                        _blockedStatus = response.StatusCode;
                    }
                    throw new ExchangeMarketApiException($"Exchange API returned HTTP {(int)response.StatusCode}.", response.StatusCode, retry);
                }
                var media = response.Content.Headers.ContentType?.MediaType;
                if (media is null || !(media.Equals("application/json", StringComparison.OrdinalIgnoreCase) || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase))) throw Invalid();
                if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw Invalid();
                await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int length;
                while ((length = await stream.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + length > MaximumResponseBytes) throw Invalid();
                    buffer.Write(chunk, 0, length);
                }
                var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                try { NoDuplicates(document.RootElement); }
                catch { document.Dispose(); throw; }
                _failures = 0;
                return document;
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            { throw new ExchangeMarketApiException("Exchange API request timed out.", innerException: error); }
            catch (HttpRequestException error)
            { throw new ExchangeMarketApiException("Exchange API is unavailable.", innerException: error); }
            catch (IOException error)
            { throw new ExchangeMarketApiException("Exchange API response could not be read.", innerException: error); }
            catch (JsonException error) { throw Invalid(error); }
        }
        finally { _requests.Release(); }
    }

    private static void NoDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw Invalid();
                NoDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) NoDuplicates(item);
    }

    private static JsonElement Required(JsonElement value, string key, JsonValueKind kind)
    {
        var result = value.GetProperty(key);
        if (result.ValueKind != kind) throw Invalid();
        return result;
    }
    private static int PositiveInt(JsonElement value, string key)
    {
        var result = Required(value, key, JsonValueKind.Number).GetInt32();
        return result > 0 ? result : throw Invalid();
    }
    private static bool Boolean(JsonElement value, string key) => value.GetProperty(key).GetBoolean();
    private static string Text(JsonElement value, string key, bool nullable)
    {
        var element = value.GetProperty(key);
        if (nullable && element.ValueKind == JsonValueKind.Null) return "";
        var text = element.GetString();
        if (text is null || text.Length > 256 || text.Any(char.IsControl) || (!nullable && string.IsNullOrWhiteSpace(text))) throw Invalid();
        return text;
    }
    private static DateTimeOffset Timestamp(JsonElement value, string key)
    {
        var element = Required(value, key, JsonValueKind.String);
        return TryTimestamp(element, out var time) ? time : throw Invalid();
    }
    private static bool TryTimestamp(JsonElement element, out DateTimeOffset time)
    {
        time = default;
        if (element.ValueKind != JsonValueKind.String) return false;
        var text = element.GetString()!;
        if (text.Length > 40 || !(text.EndsWith('Z') || (text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':')) ||
            !element.TryGetDateTimeOffset(out time) || time <= DateTimeOffset.UnixEpoch) return false;
        time = time.ToUniversalTime();
        return true;
    }
    private static ExchangeMarketHistoricalQuote? HistoricalQuote(JsonElement row, int serverId, int itemId, DateTimeOffset fetchedAt)
    {
        if (!row.TryGetProperty("last_price", out var price) || price.ValueKind != JsonValueKind.Number
            || !price.TryGetDecimal(out var amount) || amount <= 0
            || !row.TryGetProperty("last_seen_at", out var seen) || !TryTimestamp(seen, out var lastSeenAt)
            || lastSeenAt - fetchedAt > ExchangeMarketCache.FutureSkew) return null;
        // User request 2026-11-08: old historical observations stay explicitly dated;
        // the current quote's two-hour source limit does not apply to this DTO.
        return new(serverId, itemId, amount, lastSeenAt, fetchedAt);
    }
    private static ExchangeMarketApiException Invalid(Exception? error = null) =>
        new("Exchange API response is invalid.", innerException: error);

    public void Dispose() => _http.Dispose();
}
