using System.Collections.Immutable;
using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>Provider-only cache: no profiles, character names, game state or credentials.</summary>
public sealed class ExchangeMarketCache
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(2);
    public static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FetchTtl = TimeSpan.FromMinutes(10);
    // Missing prices must be retryable within the existing ten-minute first-valuation window.
    public static readonly TimeSpan MissingQuoteTtl = TimeSpan.FromMinutes(1);
    private const int MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumQuotes = 20_000;
    private readonly TimeProvider _clock;
    private readonly string? _root;
    private readonly object _gate = new();
    private readonly object _persistenceGate = new();
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private ImmutableArray<ExchangeMarketServer> _servers = [];
    private readonly Dictionary<(int Server, int Item), ExchangeMarketQuote> _quotes = [];
    private readonly Dictionary<(int Server, int Item), ExchangeMarketHistoricalQuote> _historicalQuotes = [];
    private readonly Dictionary<(int Server, int Item), DateTimeOffset> _lookups = [];
    private DateTimeOffset? _serversFetchedAt;
    private string? _persistenceError;
    private DateTimeOffset? _retryAt;
    private System.Net.HttpStatusCode? _failureStatus;
    private int _consecutiveFailures;

    public ExchangeMarketCache(TimeProvider? clock = null, string? root = null)
    {
        _clock = clock ?? TimeProvider.System;
        if (root is not null) { _root = LocalRoot(root); Load(); }
    }

    public string? PersistenceError { get { lock (_gate) return _persistenceError; } }
    public DateTimeOffset? ServersFetchedAt { get { lock (_gate) return _serversFetchedAt; } }
    public ImmutableArray<ExchangeMarketServer> GetServers() { lock (_gate) return _servers; }
    public void SaveServers(IEnumerable<ExchangeMarketServer> servers)
    {
        var entries = servers.Take(2049).ToImmutableArray();
        if (entries.Length > 2048 || entries.Any(server => !ValidServer(server)) || entries.Select(server => server.ServerId).Distinct().Count() != entries.Length)
            throw new ArgumentException("Invalid Exchange server cache.", nameof(servers));
        lock (_gate) { _servers = entries; _serversFetchedAt = _clock.GetUtcNow(); }
        Save();
    }

    public ExchangeMarketQuote? GetQuote(int serverId, int itemId)
    {
        lock (_gate) return _quotes.TryGetValue((serverId, itemId), out var quote) && IsUsable(quote, _clock.GetUtcNow()) ? quote : null;
    }

    /// <summary>A previous observation, kept separate from a currently listed usable quote.</summary>
    public ExchangeMarketHistoricalQuote? GetHistoricalQuote(int serverId, int itemId)
    {
        lock (_gate) return _historicalQuotes.TryGetValue((serverId, itemId), out var quote) && HistoryIsUsable(quote, _clock.GetUtcNow()) ? quote : null;
    }

    /// <summary>A successful lookup also caches absence; an unknown price never becomes a zero price.</summary>
    public bool IsQuoteLookupFresh(int serverId, int itemId, DateTimeOffset now)
    {
        lock (_gate) return QuoteLookupFresh(serverId, itemId, now);
    }

    public async Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(IExchangeMarketClient client, int serverId,
        IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(itemIds);
        if (serverId <= 0) throw new ArgumentOutOfRangeException(nameof(serverId));
        if (itemIds.Count > MaximumQuotes) throw new ArgumentException("Exchange item ID input is too large.", nameof(itemIds));
        var ids = itemIds.Distinct().Take(MaximumQuotes + 1).Order().ToArray();
        if (ids.Length > MaximumQuotes || ids.Any(id => id <= 0))
            throw new ArgumentException("Invalid Exchange item IDs.", nameof(itemIds));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            if (ids.All(id => QuoteLookupFresh(serverId, id, _clock.GetUtcNow()))) return ReadQuotes(serverId, ids, _clock.GetUtcNow());

        await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int[] pending;
            lock (_gate) pending = ids.Where(id => !QuoteLookupFresh(serverId, id, _clock.GetUtcNow())).ToArray();
            foreach (var candidates in pending.Chunk(100))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int[] batch;
                lock (_gate)
                {
                    var now = _clock.GetUtcNow();
                    batch = candidates.Where(id => !QuoteLookupFresh(serverId, id, now)).ToArray();
                    if (batch.Length != 0) ThrowIfDeferred(now);
                }
                if (batch.Length == 0) continue;
                ImmutableArray<ExchangeMarketQuote> response;
                ImmutableArray<ExchangeMarketHistoricalQuote> historicalResponse = [];
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (client is IExchangeMarketDetailsClient detailsClient)
                    {
                        var details = await detailsClient.GetQuoteDetailsAsync(serverId, batch, cancellationToken).ConfigureAwait(false);
                        if (details is null) throw new ExchangeMarketApiException("Invalid Exchange quote response.");
                        response = details.CurrentQuotes;
                        historicalResponse = details.HistoricalQuotes;
                    }
                    else response = await client.GetQuotesAsync(serverId, batch, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (response.IsDefault || response.Length > batch.Length ||
                        response.Any(quote => quote is null || quote.ServerId != serverId || !batch.Contains(quote.ItemId)) ||
                        response.Select(quote => quote.ItemId).Distinct().Count() != response.Length ||
                        historicalResponse.IsDefault || historicalResponse.Length > batch.Length ||
                        historicalResponse.Any(quote => quote is null || quote.ServerId != serverId || !batch.Contains(quote.ItemId)) ||
                        historicalResponse.Select(quote => quote.ItemId).Distinct().Count() != historicalResponse.Length)
                        throw new ExchangeMarketApiException("Invalid Exchange quote response.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (FetchError(error))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw RecordFailure(error);
                }

                lock (_gate)
                {
                    var now = _clock.GetUtcNow();
                    var usable = response.Where(quote => IsUsable(quote, now)).ToDictionary(quote => quote.ItemId);
                    foreach (var id in batch)
                    {
                        if (usable.TryGetValue(id, out var quote)) PutQuote(quote);
                        else _quotes.Remove((serverId, id));
                        _lookups[(serverId, id)] = now;
                    }
                    foreach (var quote in historicalResponse)
                        if (HistoryIsUsable(quote, now)) PutHistoricalQuote(quote);
                    Prune(now);
                    ResetFailure();
                }
                Save();
            }
            lock (_gate) return ReadQuotes(serverId, ids, _clock.GetUtcNow());
        }
        finally { _fetchGate.Release(); }
    }

    public async Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(IExchangeMarketClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) if (ServersFresh(_clock.GetUtcNow())) return _servers;
        await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                if (ServersFresh(now)) return _servers;
                ThrowIfDeferred(now);
            }
            ImmutableArray<ExchangeMarketServer> response;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                response = await client.GetServersAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (response.IsDefault || response.Length > 2048 || response.Any(server => !ValidServer(server)) ||
                    response.Select(server => server.ServerId).Distinct().Count() != response.Length)
                    throw new ExchangeMarketApiException("Invalid Exchange server response.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (FetchError(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw RecordFailure(error);
            }
            lock (_gate)
            {
                _servers = response;
                _serversFetchedAt = _clock.GetUtcNow();
                ResetFailure();
            }
            Save();
            return response;
        }
        finally { _fetchGate.Release(); }
    }

    public void PutQuotes(IEnumerable<ExchangeMarketQuote> quotes)
    {
        var entries = quotes.Take(MaximumQuotes + 1).ToArray();
        if (entries.Length > MaximumQuotes) throw new ArgumentException("Exchange quote cache input is too large.", nameof(quotes));
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var quote in entries)
                if (IsUsable(quote, now)) PutQuote(quote);
            Prune(now);
        }
        Save();
    }

    public void RemoveQuotes(int serverId, IEnumerable<int> itemIds)
    {
        lock (_gate) { foreach (var id in itemIds.Take(MaximumQuotes)) _quotes.Remove((serverId, id)); }
        Save();
    }

    public static bool IsUsable(ExchangeMarketQuote? quote, DateTimeOffset now) => quote is not null &&
        quote.ServerId > 0 && quote.ItemId > 0 && quote.UnitPrice > 0 && quote.Listings > 0 &&
        quote.CapturedAt > DateTimeOffset.UnixEpoch && quote.FetchedAt > DateTimeOffset.UnixEpoch &&
        quote.CapturedAt - now <= FutureSkew && quote.FetchedAt - now <= FutureSkew &&
        quote.CapturedAt - quote.FetchedAt <= FutureSkew && now - quote.CapturedAt <= MaximumAge;

    public static bool IsFresh(ExchangeMarketQuote? quote, DateTimeOffset now) =>
        IsUsable(quote, now) && now - quote!.FetchedAt <= FetchTtl;

    public static bool HistoryIsUsable(ExchangeMarketHistoricalQuote? quote, DateTimeOffset now) => quote is not null &&
        quote.ServerId > 0 && quote.ItemId > 0 && quote.UnitPrice > 0 &&
        quote.LastSeenAt > DateTimeOffset.UnixEpoch && quote.FetchedAt > DateTimeOffset.UnixEpoch &&
        quote.LastSeenAt - now <= FutureSkew && quote.FetchedAt - now <= FutureSkew &&
        quote.LastSeenAt - quote.FetchedAt <= FutureSkew;

    private bool QuoteLookupFresh(int serverId, int itemId, DateTimeOffset now) =>
        _quotes.TryGetValue((serverId, itemId), out var quote) && IsFresh(quote, now) ||
        _lookups.TryGetValue((serverId, itemId), out var fetchedAt) && LookupTimestampFresh(fetchedAt, now);

    private bool ServersFresh(DateTimeOffset now) => _serversFetchedAt is { } fetchedAt && FreshTimestamp(fetchedAt, now);
    private static bool FreshTimestamp(DateTimeOffset fetchedAt, DateTimeOffset now) =>
        ValidTimestamp(fetchedAt, now) && now - fetchedAt <= FetchTtl;
    private static bool LookupTimestampFresh(DateTimeOffset fetchedAt, DateTimeOffset now) =>
        ValidTimestamp(fetchedAt, now) && now - fetchedAt < MissingQuoteTtl;
    private static bool ValidTimestamp(DateTimeOffset fetchedAt, DateTimeOffset now) =>
        fetchedAt > DateTimeOffset.UnixEpoch && fetchedAt - now <= FutureSkew;

    private ImmutableArray<ExchangeMarketQuote> ReadQuotes(int serverId, IEnumerable<int> ids, DateTimeOffset now) =>
        ids.Select(id => _quotes.GetValueOrDefault((serverId, id))).Where(quote => IsUsable(quote, now))
            .Select(quote => quote!).ToImmutableArray();

    private void PutQuote(ExchangeMarketQuote quote)
    {
        if (!_quotes.TryGetValue((quote.ServerId, quote.ItemId), out var prior) ||
            quote.FetchedAt >= prior.FetchedAt && quote.CapturedAt >= prior.CapturedAt)
            _quotes[(quote.ServerId, quote.ItemId)] = quote;
    }

    private void PutHistoricalQuote(ExchangeMarketHistoricalQuote quote)
    {
        if (!_historicalQuotes.TryGetValue((quote.ServerId, quote.ItemId), out var prior) ||
            quote.LastSeenAt > prior.LastSeenAt || quote.LastSeenAt == prior.LastSeenAt && quote.FetchedAt >= prior.FetchedAt)
            _historicalQuotes[(quote.ServerId, quote.ItemId)] = quote;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _historicalQuotes.Where(pair => !HistoryIsUsable(pair.Value, now)).Select(pair => pair.Key).ToArray()) _historicalQuotes.Remove(key);
        foreach (var key in _historicalQuotes.OrderByDescending(pair => pair.Value.LastSeenAt).ThenByDescending(pair => pair.Value.FetchedAt)
                     .ThenBy(pair => pair.Key.Server).ThenBy(pair => pair.Key.Item).Skip(MaximumQuotes).Select(pair => pair.Key).ToArray()) _historicalQuotes.Remove(key);
        foreach (var key in _quotes.Where(pair => !IsUsable(pair.Value, now)).Select(pair => pair.Key).ToArray()) _quotes.Remove(key);
        foreach (var key in _quotes.OrderByDescending(pair => pair.Value.FetchedAt).ThenBy(pair => pair.Key.Server)
                     .ThenBy(pair => pair.Key.Item).Skip(MaximumQuotes).Select(pair => pair.Key).ToArray()) _quotes.Remove(key);
        foreach (var key in _lookups.Where(pair => !LookupTimestampFresh(pair.Value, now)).Select(pair => pair.Key).ToArray()) _lookups.Remove(key);
        foreach (var key in _lookups.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key.Server)
                     .ThenBy(pair => pair.Key.Item).Skip(MaximumQuotes).Select(pair => pair.Key).ToArray()) _lookups.Remove(key);
    }

    private void ThrowIfDeferred(DateTimeOffset now)
    {
        if (_retryAt is { } retryAt && retryAt > now)
            throw new ExchangeMarketApiException("Exchange market requests are temporarily deferred.", _failureStatus, retryAt - now);
    }

    private ExchangeMarketApiException RecordFailure(Exception error)
    {
        lock (_gate)
        {
            _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 6);
            var retry = TimeSpan.FromSeconds(Math.Min(600, 30 * (1 << (_consecutiveFailures - 1))));
            if (error is ExchangeMarketApiException { RetryAfter: { } providerRetry } && providerRetry > retry) retry = providerRetry;
            var now = _clock.GetUtcNow();
            _retryAt = retry >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + retry;
            _failureStatus = (error as ExchangeMarketApiException)?.StatusCode;
            return new ExchangeMarketApiException("Exchange market request failed.", _failureStatus, _retryAt - now, error);
        }
    }

    private void ResetFailure() { _consecutiveFailures = 0; _retryAt = null; _failureStatus = null; }
    private static bool FetchError(Exception error) => error is ExchangeMarketApiException or HttpRequestException or
        IOException or TimeoutException or OperationCanceledException or JsonException;

    private void Load()
    {
        try
        {
            var path = CachePath();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaximumBytes) return;
            var file = JsonSerializer.Deserialize<CacheLoadFile>(stream, new JsonSerializerOptions { MaxDepth = 16 });
            var now = _clock.GetUtcNow();
            if (file is null || file.SchemaVersion != 1 || file.Provider != "aion2.exchange" ||
                file.Servers is null || file.Quotes is null || file.Servers.Length > 2048 || file.Quotes.Length > MaximumQuotes ||
                file.Servers.Any(server => !ValidServer(server)) || file.Servers.Select(server => server.ServerId).Distinct().Count() != file.Servers.Length ||
                file.Quotes.Any(quote => quote is null) || file.Quotes.Select(quote => (quote.ServerId, quote.ItemId)).Distinct().Count() != file.Quotes.Length ||
                file.ServersFetchedAt is { } serversAt && !ValidTimestamp(serversAt, now) ||
                file.Lookups is { } lookups && (lookups.Length > MaximumQuotes ||
                    lookups.Any(lookup => lookup is null || lookup.ServerId <= 0 || lookup.ItemId <= 0 || !ValidTimestamp(lookup.FetchedAt, now)) ||
                    lookups.Select(lookup => (lookup.ServerId, lookup.ItemId)).Distinct().Count() != lookups.Length)) return;
            _servers = file.Servers.ToImmutableArray();
            _serversFetchedAt = file.ServersFetchedAt;
            foreach (var quote in file.Quotes) if (IsUsable(quote, now)) _quotes.Add((quote.ServerId, quote.ItemId), quote);
            if (file.Lookups is { } entries)
                foreach (var lookup in entries) if (LookupTimestampFresh(lookup.FetchedAt, now)) _lookups.Add((lookup.ServerId, lookup.ItemId), lookup.FetchedAt);
            LoadHistoricalQuotes(file.HistoricalQuotes, now);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (StorageError(error)) { _persistenceError = "Exchange market cache could not be read."; }
    }

    private void LoadHistoricalQuotes(JsonElement? value, DateTimeOffset now)
    {
        // Optional provider history must not invalidate current prices or their request throttling.
        if (value is not { } element || element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaximumQuotes) return;
        try
        {
            var quotes = element.Deserialize<ExchangeMarketHistoricalQuote[]>();
            if (quotes is null || quotes.Any(quote => !HistoryIsUsable(quote, now)) ||
                quotes.Select(quote => (quote.ServerId, quote.ItemId)).Distinct().Count() != quotes.Length) return;
            foreach (var quote in quotes) _historicalQuotes.Add((quote.ServerId, quote.ItemId), quote);
        }
        catch (JsonException) { }
    }

    private void Save()
    {
        if (_root is null) return;
        lock (_persistenceGate) SaveSnapshot();
    }

    private void SaveSnapshot()
    {
        var root = _root!;
        string? temporary = null;
        try
        {
            var path = CachePath();
            Directory.CreateDirectory(root);
            CheckAncestors(root);
            CacheFile snapshot;
            lock (_gate)
                snapshot = new CacheFile(1, "aion2.exchange", _serversFetchedAt, _servers.ToArray(),
                    _quotes.Values.OrderBy(quote => quote.ServerId).ThenBy(quote => quote.ItemId).ToArray(),
                    _lookups.OrderBy(pair => pair.Key.Server).ThenBy(pair => pair.Key.Item)
                        .Select(pair => new QuoteLookup(pair.Key.Server, pair.Key.Item, pair.Value)).ToArray(),
                    _historicalQuotes.Values.OrderByDescending(quote => quote.LastSeenAt).ThenByDescending(quote => quote.FetchedAt)
                        .ThenBy(quote => quote.ServerId).ThenBy(quote => quote.ItemId).ToArray());
            var bytes = SerializeBoundedSnapshot(snapshot);
            if (bytes.Length > MaximumBytes) throw new IOException("Exchange cache exceeds its size bound.");
            temporary = Path.Combine(root, ".exchange-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            CheckAncestors(path);
            bool exists;
            try { File.GetAttributes(path); exists = true; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { exists = false; }
            if (exists) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            lock (_gate) _persistenceError = null;
        }
        catch (Exception error) when (StorageError(error)) { lock (_gate) _persistenceError = "Exchange market cache could not be saved."; }
        finally
        {
            if (temporary is not null)
                try { CheckAncestors(temporary); File.Delete(temporary); }
                catch (Exception error) when (StorageError(error)) { }
        }
    }

    private static byte[] SerializeBoundedSnapshot(CacheFile snapshot)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (bytes.Length <= MaximumBytes || snapshot.HistoricalQuotes is not { Length: > 0 } history) return bytes;
        // Current quotes and request throttling retain priority over optional historical observations.
        var withoutHistory = snapshot with { HistoricalQuotes = [] };
        var bounded = JsonSerializer.SerializeToUtf8Bytes(withoutHistory);
        if (bounded.Length > MaximumBytes) return bounded;
        var low = 0;
        var high = history.Length - 1;
        while (low <= high)
        {
            var count = low + (high - low) / 2;
            var candidate = JsonSerializer.SerializeToUtf8Bytes(snapshot with { HistoricalQuotes = history[..count] });
            if (candidate.Length <= MaximumBytes) { bounded = candidate; low = count + 1; }
            else high = count - 1;
        }
        return bounded;
    }

    private string CachePath()
    {
        CheckAncestors(_root!);
        var path = Path.Combine(_root!, "exchange-market-cache.json");
        CheckAncestors(path);
        return path;
    }
    private static string LocalRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.StartsWith('\\') || root.StartsWith('/') || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("Exchange cache requires a local absolute directory.", nameof(root));
        var full = Path.GetFullPath(root);
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new ArgumentException("Exchange cache requires a local drive.", nameof(root));
        CheckAncestors(full);
        return full;
    }
    private static void CheckAncestors(string path)
    {
        var ancestors = new Stack<string>();
        for (DirectoryInfo? part = new(path); part is not null; part = part.Parent) ancestors.Push(part.FullName);
        foreach (var ancestor in ancestors)
            try
            {
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Exchange cache reparse points are excluded.");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { break; }
    }
    private static bool ValidServer(ExchangeMarketServer? server) => server is not null && server.ServerId > 0 &&
        ValidText(server.Name, false) && ValidText(server.Region, true) && ValidText(server.RegionName, true) && ValidText(server.Faction, true);
    private static bool ValidText(string? value, bool empty) => value is not null && value.Length <= 256 && !value.Any(char.IsControl) && (empty || !string.IsNullOrWhiteSpace(value));
    private static bool StorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
    private sealed record CacheFile(int SchemaVersion, string Provider, DateTimeOffset? ServersFetchedAt,
        ExchangeMarketServer[] Servers, ExchangeMarketQuote[] Quotes, QuoteLookup[]? Lookups = null,
        ExchangeMarketHistoricalQuote[]? HistoricalQuotes = null);
    private sealed record CacheLoadFile(int SchemaVersion, string Provider, DateTimeOffset? ServersFetchedAt,
        ExchangeMarketServer[] Servers, ExchangeMarketQuote[] Quotes, QuoteLookup[]? Lookups = null,
        JsonElement? HistoricalQuotes = null);
    private sealed record QuoteLookup(int ServerId, int ItemId, DateTimeOffset FetchedAt);
}
