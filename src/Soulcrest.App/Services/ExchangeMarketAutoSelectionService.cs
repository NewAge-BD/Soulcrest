using System.Threading.Channels;
using Soulcrest.App.Network;

namespace Soulcrest.App.Services;

public sealed record ExchangeMarketAutoSelectionSnapshot(string? ProfileId, bool Loading, string Status, bool IsError)
{
    public bool OwnCharacterKnown { get; init; }
    public bool Automatic { get; init; }
    public bool MappingKnown { get; init; }
}

internal sealed record ExchangeMarketAutoSelectionAuthority(string ProfileId, int ServerId, long Generation, long CharacterId);

/// <summary>Joins only the qualified own server to the existing anonymous provider catalogue. Never starts tracking.</summary>
public sealed class ExchangeMarketAutoSelectionService : IDisposable, IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly Func<ExchangeMarketAutoSelectionAuthority?> _authority;
    private readonly IExchangeMarketClient _client;
    private readonly ExchangeMarketCache _cache;
    private readonly TimeProvider _time;
    private readonly Channel<Refresh> _commands = Channel.CreateUnbounded<Refresh>(new() { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Task _worker;
    private readonly ITimer _timer;
    private Action? _detach;
    private CancellationTokenSource? _request;
    private ExchangeMarketAutoSelectionAuthority? _observed;
    private (ExchangeMarketAutoSelectionAuthority Authority, ExchangeMarketProfileSettings Model)? _failure;
    private DateTimeOffset _retryAt;
    private string? _failureStatus;
    private long _revision;
    private int _queued, _disposed;
    private ExchangeMarketAutoSelectionSnapshot _snapshot = new(null, false, "Warte auf den erkannten Spielcharakter.", false);
    private sealed record Refresh(TaskCompletionSource? Completion = null, bool Coalesced = false, bool Retry = false);

    public ExchangeMarketAutoSelectionService(SettingsService settings, INetworkFarmSource source, ExplorationService exploration,
        IExchangeMarketClient client, ExchangeMarketCache cache, TimeProvider? time = null)
        : this(settings, () =>
        {
            var own = source.Running ? source is NetworkLootService native ? native.OwnCharacterSnapshot : source.OwnCharacter : null;
            var profile = own is null ? null : exploration.TryGetBoundCharacter(own);
            return own is { Generation: > 0, Identity.ServerId: > 0, Identity.CharacterId: > 0 }
                && profile is not null && ExchangeMarketProfileSettings.IsProfileId(profile.ProfileId)
                ? new(profile.ProfileId!, own.Identity.ServerId, own.Generation, own.Identity.CharacterId) : null;
        }, client, cache, time ?? TimeProvider.System)
    {
        source.Changed += SourcesChanged; source.OwnCharacterChanged += SourcesChanged; exploration.Changed += SourcesChanged;
        _detach = () => { source.Changed -= SourcesChanged; source.OwnCharacterChanged -= SourcesChanged; exploration.Changed -= SourcesChanged; };
    }

    internal ExchangeMarketAutoSelectionService(SettingsService settings, Func<ExchangeMarketAutoSelectionAuthority?> authority,
        IExchangeMarketClient client, ExchangeMarketCache cache, TimeProvider time)
    {
        _settings = settings; _authority = authority; _client = client; _cache = cache; _time = time;
        settings.Changed += SourcesChanged;
        _worker = Task.Run(Run);
        _timer = time.CreateTimer(_ => SourcesChanged(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        SourcesChanged();
    }

    public ExchangeMarketAutoSelectionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event Action? Changed;

    private void SourcesChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var current = _authority();
        lock (_gate)
        {
            if (_observed != current) { _observed = current; Interlocked.Increment(ref _revision); _request?.Cancel(); }
            if (!_settings.Current.OnlineMarketPricesEnabled || current is not null
                && !_settings.GetExchangeMarketProfile(current.ProfileId).AutomaticServerSelection) _request?.Cancel();
        }
        if (Interlocked.Exchange(ref _queued, 1) == 0) _commands.Writer.TryWrite(new(Coalesced: true));
    }

    internal async Task FlushAsync()
    {
        SourcesChanged();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new(completion), _lifetime.Token).ConfigureAwait(false);
        await completion.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
    }

    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SourcesChanged();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new(completion, Retry: true), _lifetime.Token).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task Run()
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (command.Coalesced) Interlocked.Exchange(ref _queued, 0);
                if (command.Retry) { _failure = null; _failureStatus = null; _retryAt = default; }
                try { await UpdateAsync().ConfigureAwait(false); command.Completion?.TrySetResult(); }
                catch (OperationCanceledException) { command.Completion?.TrySetCanceled(); }
                catch (Exception error) { LogFile.Error("Automatic market selection", error); command.Completion?.TrySetException(error); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { while (_commands.Reader.TryRead(out var command)) command.Completion?.TrySetCanceled(); }
    }

    private async Task UpdateAsync()
    {
        var authority = _authority();
        if (!_settings.Current.OnlineMarketPricesEnabled) { Publish(authority, "Online-Marktpreise sind ausgeschaltet."); return; }
        if (authority is null) { Publish(null, "Warte auf den erkannten Spielcharakter."); return; }
        var model = _settings.GetExchangeMarketProfile(authority.ProfileId);
        if (!model.AutomaticServerSelection) { Publish(authority, "Manueller Marktserver bleibt ausgewählt."); return; }
        if (_failure is { } failed && failed == (authority, model) && _time.GetUtcNow() < _retryAt)
        { Publish(authority, _failureStatus!, error: true); return; }
        var revision = Volatile.Read(ref _revision);
        bool Current() => Volatile.Read(ref _disposed) == 0 && revision == Volatile.Read(ref _revision) && _authority() == authority;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_gate)
        {
            _request = request;
            if (!Current() || !_settings.Current.OnlineMarketPricesEnabled
                || !_settings.GetExchangeMarketProfile(authority.ProfileId).AutomaticServerSelection) request.Cancel();
        }
        try
        {
            request.Token.ThrowIfCancellationRequested();
            if (model.HasServerSelection && model.GameServerKey != authority.ServerId)
            {
                if (_authority() != authority || !Current()) return;
                _settings.TrySetDetectedExchangeMarketProfile(authority.ProfileId, null, Current);
            }
            var now = _time.GetUtcNow();
            var fresh = _cache.ServersFetchedAt is { } fetched && fetched > DateTimeOffset.UnixEpoch
                && fetched - now <= ExchangeMarketCache.FutureSkew && now - fetched <= ExchangeMarketCache.FetchTtl;
            if (!fresh) Publish(authority, "Marktserver wird automatisch erkannt …", loading: true);
            var servers = fresh ? _cache.GetServers()
                : await _cache.GetServersAsync(_client, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            if (_authority() != authority || !Current() || !_settings.Current.OnlineMarketPricesEnabled) return;
            var matching = servers.Where(server => server.GameServerKey == authority.ServerId).ToArray();
            var server = matching.Length == 1 && new ExchangeMarketProfileSettings
                { ServerId = matching[0].ServerId, Region = matching[0].Region, Faction = matching[0].Faction }.HasServerSelection ? matching[0] : null;
            if (!_settings.TrySetDetectedExchangeMarketProfile(authority.ProfileId, server, Current)) { SourcesChanged(); return; }
            _failure = null;
            Publish(authority, server is null ? "Erkannter Spielserver ist im Marktangebot nicht eindeutig verfügbar." : "Marktserver automatisch zugeordnet.", mapped: server is not null);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { SourcesChanged(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExchangeMarketApiException or HttpRequestException)
        {
            if (_authority() != authority || !Current()) return;
            var storage = error is IOException or UnauthorizedAccessException;
            _failure = (authority, _settings.GetExchangeMarketProfile(authority.ProfileId));
            _retryAt = storage ? DateTimeOffset.MaxValue : _time.GetUtcNow() + ExchangeMarketCache.FetchTtl;
            _failureStatus = storage ? "Automatische Marktzuordnung konnte nicht gespeichert werden." : "Automatische Marktzuordnung fehlgeschlagen. Später erneut versuchen.";
            Publish(authority, _failureStatus, error: true);
        }
        finally { lock (_gate) if (ReferenceEquals(_request, request)) _request = null; }
    }

    private void Publish(ExchangeMarketAutoSelectionAuthority? authority, string status, bool loading = false, bool error = false, bool mapped = false)
    {
        var next = new ExchangeMarketAutoSelectionSnapshot(authority?.ProfileId, loading, status, error)
        {
            OwnCharacterKnown = authority is not null,
            Automatic = authority is not null && _settings.GetExchangeMarketProfile(authority.ProfileId).AutomaticServerSelection,
            MappingKnown = mapped,
        };
        if (Volatile.Read(ref _disposed) != 0 || next == Snapshot) return;
        Volatile.Write(ref _snapshot, next);
        SafeEvent.Raise(Changed, "Automatic market selection");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _settings.Changed -= SourcesChanged; _detach?.Invoke(); _detach = null;
        _timer.Dispose(); _lifetime.Cancel(); _commands.Writer.TryComplete();
    }
    public async ValueTask DisposeAsync() { Dispose(); await _worker.ConfigureAwait(false); _lifetime.Dispose(); }
}
