using System.Threading.Channels;

namespace Soulcrest.App.Services;

/// <summary>
/// Revalues the qualified current session using current public unit prices, with 24-hour means for missing prices.
/// A shared activation lease owns refreshes; capture is read-only and never writes frozen valuation sidecars.
/// </summary>
public sealed class OverlayEditorMarketService : IDisposable, IAsyncDisposable
{
    private readonly Func<UnifiedLootOverlayState> _readState;
    private readonly Func<string?, ExchangeMarketProfileSettings> _profile;
    private readonly Func<bool> _online;
    private readonly IExchangeMarketClient _client;
    private readonly ExchangeMarketCache _cache;
    private readonly TimeProvider _time;
    private readonly Func<int, OverlayEditorMarketItemFacts> _facts;
    private readonly List<Action> _detach = [];
    private readonly object _gate = new();
    private readonly Channel<Refresh> _commands = Channel.CreateUnbounded<Refresh>(new() { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<(int Server, int Item), DateTimeOffset> _nextAttempts = [];
    private readonly Dictionary<int, DateTimeOffset> _nextServerAttempts = [];
    private readonly Task _worker;
    private readonly ITimer _timer;
    private CancellationTokenSource? _request;
    private Context? _context;
    private long _generation;
    private int _consumers, _queued, _disposed;
    private volatile bool _loading, _requestError;
    private OverlayEditorMarketSnapshot? _published;

    private sealed record Context(string? SessionId, string? ProfileId, ExchangeMarketProfileSettings Model, bool Online);
    private sealed record Refresh(TaskCompletionSource? Completion = null, bool Coalesced = false);

    public OverlayEditorMarketService(SettingsService settings, UnifiedLootTrackerService source,
        IExchangeMarketClient client, ExchangeMarketCache cache, TimeProvider? time = null, Soulcrest.App.Network.INetworkFarmSource? ownSource = null)
        : this(() => source.OverlayState, profileId =>
        {
            var own = ownSource?.Running == true ? ownSource is Soulcrest.App.Network.NetworkLootService native
                ? native.OwnCharacterSnapshot : ownSource.OwnCharacter : null;
            return settings.GetExchangeMarketProfileForOwnServer(profileId, own?.Identity.ServerId);
        },
            () => settings.Current.OnlineMarketPricesEnabled, client, cache, time ?? TimeProvider.System)
    {
        source.Changed += SourcesChanged; settings.Changed += SourcesChanged;
        _detach.Add(() => source.Changed -= SourcesChanged);
        _detach.Add(() => settings.Changed -= SourcesChanged);
    }

    internal OverlayEditorMarketService(Func<UnifiedLootOverlayState> readState,
        Func<string?, ExchangeMarketProfileSettings> profile, Func<bool> online,
        IExchangeMarketClient client, ExchangeMarketCache cache, TimeProvider time,
        Func<int, OverlayEditorMarketItemFacts>? facts = null)
    {
        _readState = readState; _profile = profile; _online = online; _client = client; _cache = cache;
        _time = time; _facts = facts ?? OverlayEditorMarketItemFacts.ForItem;
        _worker = Task.Run(Run);
        _timer = time.CreateTimer(_ => SourcesChanged(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action? Changed;
    public OverlayEditorMarketSnapshot Snapshot => Capture(_readState(), _time.GetUtcNow());

    /// <summary>Reads only the supplied frame's items, after checking its session/profile against current qualification.</summary>
    public OverlayEditorMarketSnapshot Capture(LootTrackerSession session, DateTimeOffset asOf)
    {
        var current = _readState();
        // The source may have published between the caller's frame instant and
        // this read. Qualify against our clock after reading that state; the
        // caller's earlier instant still governs its price projection.
        var gateNow = _time.GetUtcNow();
        return Qualified(current, gateNow) is { } active && ValidSession(session, gateNow)
            && active.Id == session.Id && active.Character?.ProfileId == session.Character?.ProfileId
            ? Project(session, asOf) : Unavailable();
    }

    private OverlayEditorMarketSnapshot Capture(UnifiedLootOverlayState state, DateTimeOffset asOf) =>
        Qualified(state, asOf) is { } session ? Project(session, asOf) : Unavailable();

    private OverlayEditorMarketSnapshot Project(LootTrackerSession session, DateTimeOffset asOf)
    {
        if (Volatile.Read(ref _disposed) != 0) return Unavailable();
        var model = _profile(session.Character?.ProfileId);
        var value = OverlayEditorMarketProjection.Project(session, model, _online(), FindServer(model),
            id => model.ServerId is { } server ? _cache.GetQuote(server, id) : null,
            id => model.ServerId is { } server ? _cache.GetAveragePrice(server, id) : null, asOf, _facts);
        return value with
        {
            Loading = _loading, StatusIsError = value.StatusIsError || _requestError,
            Status = _loading && value.OnlineEnabled ? "Marktpreise werden geladen …" : value.Status,
        };
    }

    private OverlayEditorMarketSnapshot Unavailable() => new() { OnlineEnabled = _online(), AverageFallbackEnabled = true };
    private static LootTrackerSession? Qualified(UnifiedLootOverlayState state, DateTimeOffset now) =>
        state.TrackingAvailable && state.At != default && state.At <= now
            && state.Session is { } session && ValidSession(session, now) ? session : null;

    private static bool ValidSession(LootTrackerSession session, DateTimeOffset now) =>
        session.Status == LootTrackerSessionStatus.Live && session.EndedAt is null
            && !string.IsNullOrWhiteSpace(session.Id) && session.StartedAt != default && session.StartedAt <= now
            && session.Elapsed >= TimeSpan.Zero
            && Guid.TryParse(session.Character?.ProfileId, out var profile) && profile != Guid.Empty;

    public IDisposable Acquire()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (++_consumers == 1) _timer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
        SourcesChanged();
        return new Lease(this);
    }

    private sealed class Lease(OverlayEditorMarketService owner) : IDisposable
    {
        private OverlayEditorMarketService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private void Release()
    {
        CancellationTokenSource? request = null;
        lock (_gate)
        {
            if (_consumers == 0) return;
            if (--_consumers == 0)
            {
                _generation++; request = _request;
                if (_disposed == 0) _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
        TryCancel(request);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        UpdateContext();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new(completion), _lifetime.Token).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SourcesChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        UpdateContext();
        lock (_gate) if (_consumers == 0) return;
        if (Interlocked.Exchange(ref _queued, 1) == 0 && !_commands.Writer.TryWrite(new(Coalesced: true)))
            Interlocked.Exchange(ref _queued, 0);
    }

    private Context ReadContext()
    {
        var session = Qualified(_readState(), _time.GetUtcNow());
        var profile = session?.Character?.ProfileId;
        return new(session?.Id, profile, _profile(profile), _online());
    }

    private void UpdateContext()
    {
        var context = ReadContext();
        CancellationTokenSource? request = null;
        lock (_gate)
        {
            if (_context != context)
            {
                _context = context; _generation++; request = _request;
                _loading = false; _requestError = false;
            }
        }
        TryCancel(request);
    }

    private ExchangeMarketServer? FindServer(ExchangeMarketProfileSettings model) => _cache.GetServers().FirstOrDefault(server =>
        server.ServerId == model.ServerId && server.Region == model.Region && server.Faction == model.Faction
        && (!model.AutomaticServerSelection || model.GameServerKey is null || server.GameServerKey == model.GameServerKey));

    private bool NeedsCurrentPrice(int serverId, int itemId, DateTimeOffset now)
    {
        var quote = _cache.GetQuote(serverId, itemId);
        // A shared positive or negative fallback lookup also holds off widget polling for ten minutes.
        // The receipt cache keeps its independent one-minute missing-current-price lifetime.
        return !ExchangeMarketCache.IsFresh(quote, now) && !_cache.IsQuoteLookupFresh(serverId, itemId, now)
            && (quote is not null || !_cache.IsAveragePriceLookupFresh(serverId, itemId, now));
    }

    private bool NeedsAveragePrice(int serverId, int itemId, DateTimeOffset now) =>
        _cache.GetQuote(serverId, itemId) is null && !_cache.IsAveragePriceLookupFresh(serverId, itemId, now);

    private bool Current(Context context, long generation)
    {
        UpdateContext();
        lock (_gate) return _disposed == 0 && _consumers > 0 && generation == _generation && _context == context && context.Online;
    }

    private async Task Run()
    {
        try
        {
            await foreach (var refresh in _commands.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (refresh.Coalesced) Interlocked.Exchange(ref _queued, 0);
                try { await RefreshCore().ConfigureAwait(false); Publish(); refresh.Completion?.TrySetResult(); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                { refresh.Completion?.TrySetCanceled(); break; }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or OverflowException)
                { _requestError = true; Publish(); refresh.Completion?.TrySetResult(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            while (_commands.Reader.TryRead(out var refresh)) refresh.Completion?.TrySetCanceled();
        }
    }

    private async Task RefreshCore()
    {
        UpdateContext();
        Context context; long generation;
        lock (_gate)
        {
            if (_consumers == 0 || _disposed != 0 || _context is not { SessionId: not null, Online: true } active
                || !active.Model.HasServerSelection || !active.Model.HasValidFees) return;
            context = active; generation = _generation;
        }
        var session = Qualified(_readState(), _time.GetUtcNow());
        if (session is null || session.Id != context.SessionId || session.Character?.ProfileId != context.ProfileId) return;
        var items = OverlayEditorMarketProjection.Items(session, _facts)
            .Where(item => item.Id is not null && item.Facts.TradeKnown).Select(item => item.Id!.Value).ToArray();
        if (items.Length == 0) return;
        var serverId = context.Model.ServerId!.Value;
        var now = _time.GetUtcNow();
        var needsServer = FindServer(context.Model) is null;
        if (needsServer && now < _nextServerAttempts.GetValueOrDefault(serverId)) return;
        var needed = items.Where(id => (NeedsCurrentPrice(serverId, id, now) || NeedsAveragePrice(serverId, id, now))
            && _nextAttempts.GetValueOrDefault((serverId, id)) <= now).ToArray();
        if (!needsServer && needed.Length == 0)
        {
            // Another consumer can complete the shared lookup while this worker is backing off.
            if (items.All(id => !NeedsCurrentPrice(serverId, id, now) && !NeedsAveragePrice(serverId, id, now))) _requestError = false;
            return;
        }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_gate)
        {
            if (_consumers == 0 || generation != _generation) return;
            _request = request;
        }
        _loading = true; Publish();
        try
        {
            if (FindServer(context.Model) is null)
            {
                await _cache.GetServersAsync(_client, request.Token).ConfigureAwait(false);
                if (!Current(context, generation) || request.IsCancellationRequested) return;
                _nextServerAttempts[serverId] = _time.GetUtcNow() + ExchangeMarketCache.FetchTtl;
                if (FindServer(context.Model) is null) return;
            }
            now = _time.GetUtcNow();
            needed = items.Where(id => NeedsCurrentPrice(serverId, id, now)
                && _nextAttempts.GetValueOrDefault((serverId, id)) <= now).ToArray();
            foreach (var batch in needed.Chunk(100))
            {
                if (!Current(context, generation) || request.IsCancellationRequested) return;
                await _cache.GetQuotesAsync(_client, serverId, batch, request.Token).ConfigureAwait(false);
                if (!Current(context, generation) || request.IsCancellationRequested) return;
                _requestError = false;
                Publish();
            }
            now = _time.GetUtcNow();
            needed = items.Where(id => NeedsAveragePrice(serverId, id, now)
                && _nextAttempts.GetValueOrDefault((serverId, id)) <= now).ToArray();
            foreach (var id in needed)
            {
                if (!Current(context, generation) || request.IsCancellationRequested) return;
                // Only missing current prices need individual history requests; both paths
                // share the provider gate and publish each cached result incrementally.
                if (_client is not IExchangeMarketAverageClient averages)
                    throw new InvalidDataException("The market source does not provide 24-hour mean prices.");
                await _cache.GetAveragePricesAsync(averages, serverId, [id], request.Token).ConfigureAwait(false);
                if (!Current(context, generation) || request.IsCancellationRequested) return;
                _requestError = false;
                Publish();
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is ExchangeMarketApiException or HttpRequestException or TaskCanceledException
            or IOException or InvalidDataException or ArgumentException)
        {
            if (!Current(context, generation)) return;
            _requestError = true;
            var delay = error is ExchangeMarketApiException { RetryAfter: { } retry } ? retry : TimeSpan.FromMinutes(1);
            if (delay < TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
            var failedAt = _time.GetUtcNow();
            var next = delay.Ticks > DateTimeOffset.MaxValue.UtcTicks - failedAt.UtcTicks ? DateTimeOffset.MaxValue : failedAt + delay;
            foreach (var id in items) _nextAttempts[(context.Model.ServerId!.Value, id)] = next;
            _nextServerAttempts[serverId] = next;
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_request, request)) _request = null;
            _loading = false;
        }
    }

    private void Publish()
    {
        var snapshot = Snapshot;
        if (_published == snapshot) return;
        _published = snapshot;
        SafeEvent.Raise(Changed, "Overlay-Editor-Marktwerte");
    }

    private static void TryCancel(CancellationTokenSource? request)
    {
        try { request?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var detach in _detach) detach();
        _timer.Dispose(); _lifetime.Cancel(); _commands.Writer.TryComplete();
        lock (_gate) { _consumers = 0; TryCancel(_request); }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose(); await _worker.ConfigureAwait(false); _lifetime.Dispose();
    }
}
