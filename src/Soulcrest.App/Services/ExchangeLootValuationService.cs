using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;

namespace Soulcrest.App.Services;

/// <summary>
/// Prices only newly committed own facts. All HTTP and sidecars are independent of loot journals,
/// session clocks, wallet totals and rare-drop notifications. Frozen entries never reprice.
/// </summary>
public sealed class ExchangeLootValuationService : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan ReceiptWindow = TimeSpan.FromMinutes(10);
    private readonly Func<bool> _online;
    private readonly ICommittedLootFactSource _source;
    private readonly IExchangeMarketClient _client;
    private readonly ExchangeMarketCache _cache;
    private readonly ExchangeLootValuationStore _store;
    private readonly TimeProvider _time;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new() { SingleReader = true });
    private readonly Dictionary<string, ExchangeLootValuationEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _deleted = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _requestGate = new();
    private readonly HashSet<CancellationTokenSource> _requests = [];
    private readonly Task _worker;
    private readonly ITimer _timer;
    private readonly SettingsService? _settings;
    private ExchangeLootValuationSnapshot _snapshot = new([], "Marktpreise bereit.", false, true, 0);
    private int _refreshQueued, _disposed;
    private bool _loading, _requestError, _storageError;
    private long _entriesVersion, _publishedEntriesVersion = -1, _serversVersion, _publishedServersVersion = -1;

    private abstract record Command;
    private sealed record Add(CommittedLootFact Fact) : Command;
    private sealed record Refresh(TaskCompletionSource? Completion = null) : Command;
    private sealed record Removed(string SessionId) : Command;

    public ExchangeLootValuationService(SettingsService settings, UnifiedLootTrackerService source,
        IExchangeMarketClient client, ExchangeMarketCache cache, ExchangeLootValuationStore? store = null,
        TimeProvider? time = null)
        : this(() => settings.Current.OnlineMarketPricesEnabled, source, client, cache,
            store ?? new(), time ?? TimeProvider.System)
    {
        _settings = settings; settings.Changed += SettingsChanged;
    }

    internal ExchangeLootValuationService(Func<bool> online, ICommittedLootFactSource source,
        IExchangeMarketClient client, ExchangeMarketCache cache, ExchangeLootValuationStore store, TimeProvider time)
    {
        _online = online; _source = source; _client = client; _cache = cache; _store = store; _time = time;
        source.LootFactCommitted += Committed; source.SessionDeleted += Deleted; source.Changed += SourceChanged;
        _worker = Task.Run(Run);
        _timer = time.CreateTimer(_ => { if (Snapshot.PendingCount > 0) SourceChanged(); }, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        SourceChanged();
    }

    public ExchangeLootValuationSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public ImmutableArray<ExchangeMarketServer> Servers => _cache.GetServers();
    public event Action? Changed;

    public async Task LoadServersAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_online()) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        lock (_requestGate) { _requests.Add(linked); if (!_online()) linked.Cancel(); }
        try
        {
            await _cache.GetServersAsync(_client, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (!_online()) return;
            Interlocked.Increment(ref _serversVersion); SourceChanged();
        }
        finally { lock (_requestGate) _requests.Remove(linked); }
    }

    public Task RefreshPendingAsync() => FlushAsync();
    public async Task FlushAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new Refresh(completion), _lifetime.Token).ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }

    private void Committed(CommittedLootFact fact)
    {
        if (Volatile.Read(ref _disposed) == 0) _commands.Writer.TryWrite(new Add(fact));
    }
    private void Deleted(string sessionId)
    {
        _deleted.TryAdd(sessionId, 0);
        CancelRequests();
        try { _store.Tombstone(sessionId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { _storageError = true; }
        if (Volatile.Read(ref _disposed) == 0) _commands.Writer.TryWrite(new Removed(sessionId));
    }
    private void SettingsChanged()
    {
        if (!_online()) CancelRequests();
        SourceChanged();
    }
    private void SourceChanged()
    {
        if (!_online()) CancelRequests();
        if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            _commands.Writer.TryWrite(new Refresh());
    }
    private void CancelRequests()
    { lock (_requestGate) foreach (var request in _requests) request.Cancel(); }

    private async Task Run()
    {
        try
        {
            try { foreach (var entry in _store.ReadAll()) _entries[FullKey(entry)] = entry; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { _storageError = true; }
            _entriesVersion++;
            Publish();
            await foreach (var command in _commands.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    if (command is Add add) Register(add.Fact);
                    if (command is Removed removed)
                    {
                        foreach (var key in _entries.Where(p => p.Value.SessionId == removed.SessionId).Select(p => p.Key).ToArray()) _entries.Remove(key);
                        _entriesVersion++;
                    }
                    if (command is Refresh) Interlocked.Exchange(ref _refreshQueued, 0);
                    await ProcessPending().ConfigureAwait(false);
                    Publish();
                    if (command is Refresh { Completion: { } done }) done.TrySetResult();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                    or OverflowException or InvalidOperationException or ArgumentException)
                {
                    _storageError = true; Publish();
                    if (command is Refresh { Completion: { } done }) done.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            while (_commands.Reader.TryRead(out var command))
                if (command is Refresh { Completion: { } done }) done.TrySetCanceled();
        }
    }

    private void Register(CommittedLootFact committed)
    {
        var fact = committed.Fact;
        if (_deleted.ContainsKey(committed.CanonicalSessionId) || _store.IsDeleted(committed.CanonicalSessionId)
            || _store.IsBlocked(committed.CanonicalSessionId)
            || fact.Kind is not (LootTrackerTimelineEventKind.Drop or LootTrackerTimelineEventKind.HarvestSuccess)
            || fact.At == default || fact.Loot.IsDefaultOrEmpty) return;
        foreach (var item in fact.Loot.Where(ExchangeLootValuation.Eligible))
        {
            if (!ExchangeLootValuation.TryItemId(item.Id, out var id)) continue;
            var entry = new ExchangeLootValuationEntry(committed.CanonicalSessionId, committed.ProfileId,
                fact.Id, id, item.Quantity, fact.At, committed.MarketProfile with { }, ExchangeLootValueStatus.Pending);
            if (_entries.ContainsKey(FullKey(entry))) continue;
            var facts = MonsterLootItemCatalog.Default.TradeFacts(id);
            if (!entry.Model.HasServerSelection) entry = Unknown(entry, "market-not-selected");
            else if (!entry.Model.HasValidFees) entry = Unknown(entry, "invalid-fee-model");
            else if (facts.HasTradeCategory is null || facts.ExchangeType == LootItemExchangeType.Unknown)
                entry = Unknown(entry, "unknown-item-trade-category");
            Put(entry);
        }
    }

    private bool MatchesCurrentHistory(ExchangeLootValuationEntry entry)
    {
        var session = _source.Snapshot.Sessions.FirstOrDefault(s => s.Id == entry.SessionId);
        var fact = session?.TimelineEvents.FirstOrDefault(f => f.Id == entry.FactId);
        return session?.Character?.ProfileId == entry.ProfileId && fact?.At == entry.DroppedAt
            && fact.Loot.Any(row => ExchangeLootValuation.TryItemId(row.Id, out var id)
                && id == entry.ItemId && row.Quantity == entry.Quantity && ExchangeLootValuation.Eligible(row));
    }

    private async Task ProcessPending()
    {
        var now = _time.GetUtcNow();
        var pending = _entries.Values.Where(e => e.Status == ExchangeLootValueStatus.Pending
            && !_deleted.ContainsKey(e.SessionId) && !_store.IsBlocked(e.SessionId)).ToArray();
        var requests = new List<ExchangeLootValuationEntry>();
        foreach (var entry in pending)
        {
            if (now < entry.DroppedAt || now - entry.DroppedAt > ReceiptWindow)
            { Put(Unknown(entry, "first-price-window-expired")); continue; }
            if (!MatchesCurrentHistory(entry)) continue;
            var server = Servers.FirstOrDefault(s => s.ServerId == entry.Model.ServerId
                && s.Region == entry.Model.Region && s.Faction == entry.Model.Faction);
            if (server is null) continue;
            var quote = _cache.GetQuote(entry.Model.ServerId!.Value, entry.ItemId);
            if (quote is not null && (!_online() || ExchangeMarketCache.IsFresh(quote, now)))
            { Freeze(entry, quote, now); continue; }
            if (!_online() || entry.NextAttemptAt > now
                || _cache.IsQuoteLookupFresh(entry.Model.ServerId!.Value, entry.ItemId, now)) continue;
            requests.Add(entry);
        }
        // A different consumer may have repaired the shared cache after our own request failed.
        if (_requestError && pending.All(entry => !_entries.TryGetValue(FullKey(entry), out var current)
            || current.Status != ExchangeLootValueStatus.Pending
            || _cache.IsQuoteLookupFresh(entry.Model.ServerId!.Value, entry.ItemId, now))) _requestError = false;
        foreach (var group in requests.GroupBy(e => e.Model.ServerId!.Value))
        {
            if (!_online() || _lifetime.IsCancellationRequested) break;
            var batch = group.Where(e => !_deleted.ContainsKey(e.SessionId)).ToArray();
            if (batch.Length == 0) continue;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_requestGate) { _requests.Add(cts); if (!_online()) cts.Cancel(); }
            _loading = true; Publish();
            try
            {
                var ids = batch.Select(e => e.ItemId).Distinct().ToArray();
                var quotes = await _cache.GetQuotesAsync(_client, group.Key, ids, cts.Token).ConfigureAwait(false);
                if (!_online() || cts.IsCancellationRequested) continue;
                now = _time.GetUtcNow();
                _requestError = false; now = _time.GetUtcNow();
                foreach (var entry in batch)
                {
                    if (_deleted.ContainsKey(entry.SessionId)) continue;
                    if (now - entry.DroppedAt > ReceiptWindow) { Put(Unknown(entry, "first-price-window-expired")); continue; }
                    var quote = quotes.FirstOrDefault(q => q.ServerId == group.Key && q.ItemId == entry.ItemId);
                    if (quote is null || !Freeze(entry, quote, now)) Put(entry with { NextAttemptAt = now + TimeSpan.FromMinutes(1) });
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception error) when (error is ExchangeMarketApiException or HttpRequestException or TaskCanceledException)
            {
                _requestError = true; now = _time.GetUtcNow();
                var delay = error is ExchangeMarketApiException { RetryAfter: { } retry } ? retry : TimeSpan.FromMinutes(1);
                if (delay < TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
                var nextAttempt = delay >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
                foreach (var entry in batch)
                {
                    if (_deleted.ContainsKey(entry.SessionId)) continue;
                    var cached = _cache.GetQuote(group.Key, entry.ItemId);
                    if (cached is not null && now - entry.DroppedAt <= ReceiptWindow && Freeze(entry, cached, now)) continue;
                    Put(now - entry.DroppedAt > ReceiptWindow ? Unknown(entry, "first-price-window-expired")
                        : entry with { NextAttemptAt = nextAttempt });
                }
            }
            finally
            {
                lock (_requestGate) _requests.Remove(cts);
                _loading = false;
            }
        }
    }

    private bool Freeze(ExchangeLootValuationEntry entry, ExchangeMarketQuote quote, DateTimeOffset now)
    {
        if (_deleted.ContainsKey(entry.SessionId) || !_entries.TryGetValue(FullKey(entry), out var current)
            || current.Status != ExchangeLootValueStatus.Pending) return false;
        var value = ExchangeLootValuation.Evaluate(current, quote, now,
            Servers.FirstOrDefault(s => s.ServerId == quote.ServerId)?.Name);
        return value is not null && Put(value);
    }
    private bool Put(ExchangeLootValuationEntry entry)
    {
        if (_deleted.ContainsKey(entry.SessionId) || !_store.Put(entry))
        { _storageError |= _store.HasIssues; _entriesVersion++; return false; }
        if (!_entries.TryGetValue(FullKey(entry), out var previous) || previous != entry) _entriesVersion++;
        _entries[FullKey(entry)] = entry; return true;
    }
    private static ExchangeLootValuationEntry Unknown(ExchangeLootValuationEntry entry, string reason) =>
        entry with { Status = ExchangeLootValueStatus.Unknown, UnknownReason = reason, NextAttemptAt = null };
    private static string FullKey(ExchangeLootValuationEntry e) => e.SessionId + ":" + e.Key;

    private void Publish()
    {
        var old = Snapshot;
        var entries = _entriesVersion == _publishedEntriesVersion ? old.Entries
            : _entries.Values.Where(e => !_deleted.ContainsKey(e.SessionId) && !_store.IsBlocked(e.SessionId))
                .OrderBy(e => e.SessionId, StringComparer.Ordinal).ThenBy(e => e.FactId, StringComparer.Ordinal).ThenBy(e => e.ItemId).ToImmutableArray();
        var pending = entries.Count(e => e.Status == ExchangeLootValueStatus.Pending);
        var damaged = _storageError || _store.HasIssues;
        var status = damaged ? "Marktwertspeicher ist beschädigt; Werte bleiben unbekannt."
            : !_online() ? "Online-Marktpreise ausgeschaltet." : _loading ? "Marktpreise werden geladen …"
            : _requestError || entries.Any(e => e.Status == ExchangeLootValueStatus.Unknown) ? "Marktpreise sind teilweise unbekannt."
            : "Marktpreise bereit.";
        var serverVersion = Interlocked.Read(ref _serversVersion);
        if (entries == old.Entries && old.Status == status && old.StatusIsError == (damaged || _requestError)
            && old.OnlineEnabled == _online() && old.PendingCount == pending && _publishedServersVersion == serverVersion) return;
        _publishedEntriesVersion = _entriesVersion; _publishedServersVersion = serverVersion;
        Volatile.Write(ref _snapshot, new(entries, status, damaged || _requestError, _online(), pending));
        SafeEvent.Raise(Changed, "Exchange-Marktwerte");
    }

    public ExchangeLootValuationSummary GetSessionSummary(LootTrackerSession session) => Summarize([session], null);
    public ExchangeLootValuationSummary GetRowSummary(LootTrackerSession session, LootTrackerLoot row) => Summarize([session], row.Id);
    public ExchangeLootValuationSummary GetDaySummary(IEnumerable<LootTrackerSession> sessions) => Summarize(sessions, null);

    private ExchangeLootValuationSummary Summarize(IEnumerable<LootTrackerSession> sessions, string? itemKey)
    {
        var values = Snapshot.Entries.ToDictionary(FullKey, StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        int eligible = 0, priced = 0, pending = 0, reference = 0; long gross = 0, net = 0, referenceGross = 0, referenceNet = 0;
        long eligibleQuantity = 0, pricedQuantity = 0, pendingQuantity = 0;
        var bases = new HashSet<ExchangeLootValuationBasis>();
        DateTimeOffset? oldest = null, newest = null;
        try
        {
            foreach (var session in sessions.Where(s => !_deleted.ContainsKey(s.Id)))
            {
                var exact = session.TimelineVersion > 0;
                var facts = exact ? session.TimelineEvents.Where(f => f.Kind is LootTrackerTimelineEventKind.Drop or LootTrackerTimelineEventKind.HarvestSuccess)
                    .SelectMany(f => f.Loot.Select(row => (FactId: f.Id, At: f.At, Row: row)))
                    : session.Loot.Select(row => (FactId: "legacy", At: session.StartedAt, Row: row));
                foreach (var (factId, at, row) in facts)
                {
                    if (!ExchangeLootValuation.Eligible(row) || itemKey is not null
                        && (!ExchangeLootValuation.TryItemId(itemKey, out var wanted)
                            || !ExchangeLootValuation.TryItemId(row.Id, out var actual) || actual != wanted)) continue;
                    ExchangeLootValuation.TryItemId(row.Id, out var id);
                    var key = session.Id + ":" + factId + ":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!keys.Add(key)) continue;
                    eligible++; eligibleQuantity = checked(eligibleQuantity + row.Quantity);
                    if (!values.TryGetValue(key, out var entry) || entry.Quantity != row.Quantity
                        || entry.ProfileId != session.Character?.ProfileId || entry.DroppedAt != at) continue;
                    if (entry.Model.HasServerSelection)
                        bases.Add(new(entry.Model.ServerId!.Value, entry.ServerName, entry.Model.Region!, entry.Model.Faction!,
                            entry.Model.SalesTaxPercent, entry.Model.RegistrationFeePercent));
                    if (entry.Status == ExchangeLootValueStatus.Pending)
                    { pending++; pendingQuantity = checked(pendingQuantity + row.Quantity); continue; }
                    if (entry.Status is not (ExchangeLootValueStatus.Estimated or ExchangeLootValueStatus.ReferenceOnly)) continue;
                    net = checked(net + entry.NetKina!.Value); gross = checked(gross + entry.GrossKina!.Value); priced++;
                    pricedQuantity = checked(pricedQuantity + row.Quantity);
                    if (entry.Status == ExchangeLootValueStatus.ReferenceOnly)
                    { referenceGross = checked(referenceGross + entry.GrossKina.Value); referenceNet = checked(referenceNet + entry.NetKina.Value); reference++; }
                    oldest = oldest is null || entry.SourceCapturedAt < oldest ? entry.SourceCapturedAt : oldest;
                    newest = newest is null || entry.SourceCapturedAt > newest ? entry.SourceCapturedAt : newest;
                }
            }
            return new(priced > 0 ? net : null, priced > 0 ? gross : null, priced, eligible,
                eligible - priced, eligible == priced, pending, reference > 0, oldest, newest)
                { ReferenceRows = reference, ReferenceGrossKina = reference > 0 ? referenceGross : null,
                    ReferenceNetKina = reference > 0 ? referenceNet : null, EligibleQuantity = eligibleQuantity,
                    PricedQuantity = pricedQuantity, UnknownQuantity = eligibleQuantity - pricedQuantity,
                    PendingQuantity = pendingQuantity, Bases = bases.OrderBy(b => b.ServerId).ThenBy(b => b.SalesTaxPercent)
                        .ThenBy(b => b.RegistrationFeePercent).ThenBy(b => b.ServerName, StringComparer.Ordinal).ToImmutableArray() };
        }
        catch (OverflowException)
        { return new(null, null, priced, eligible, Math.Max(0, eligible - priced), false, pending, reference > 0, oldest, newest); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _source.LootFactCommitted -= Committed; _source.SessionDeleted -= Deleted; _source.Changed -= SourceChanged;
        if (_settings is not null) _settings.Changed -= SettingsChanged;
        _timer.Dispose(); _lifetime.Cancel(); _commands.Writer.TryComplete();
        CancelRequests();
    }
    public async ValueTask DisposeAsync() { Dispose(); await _worker.ConfigureAwait(false); _lifetime.Dispose(); }
}
