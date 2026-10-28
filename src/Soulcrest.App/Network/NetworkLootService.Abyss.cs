using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private const int MaximumPendingAbyssObservations = 64;
    private OwnCharacterContext? _abyssRankCharacter;
    private long? _abyssRankPoints;
    private DateTimeOffset? _abyssRankAt;
    private bool _abyssRankChangesQualified;
    private long _abyssRankRevision;
    private bool _dispatchingAbyssObservations;
    private readonly Queue<PendingAbyssObservation> _abyssObservations = [];
    private long _abyssSequence;

    private sealed record PendingAbyssRankUpdate(string Stream, OwnCharacterContext Character,
        long CaptureGeneration, long Revision, DateTimeOffset At, long? Total);
    private sealed record PendingAbyssObservation(OwnCharacterContext? Character, long CaptureGeneration,
        long ProgressGeneration, bool IsRank, bool IsFarm, Action Apply);

    // AP income requires successive qualified own Mod1/11/0/0 wallet observations.
    // The independent absolute farm counter never emits this acquisition event.
    public event Action<NetworkAbyssPointGain>? AbyssPointsGained;
    public event Action<NetworkAbyssRankChange>? RankPointsChanged;

    public NetworkAbyssSnapshot AbyssSnapshot
    {
        get
        {
            lock (_gate)
            {
                var own = Running ? _ownCharacter : null;
                var walletCurrent = own is not null && ReferenceEquals(_abyssCharacter, own);
                var rankCurrent = own is not null && ReferenceEquals(_abyssRankCharacter, own);
                var farmCurrent = own is not null && ReferenceEquals(_abyssFarmCharacter, own);
                var kinaCurrent = own is not null && ReferenceEquals(_abyssKinaCharacter, own);
                var current = walletCurrent || rankCurrent || farmCurrent || kinaCurrent;
                var at = walletCurrent ? _abyssAt : null;
                if (rankCurrent && (at is null || _abyssRankAt > at)) at = _abyssRankAt;
                if (farmCurrent && (at is null || _abyssFarmAt > at)) at = _abyssFarmAt;
                if (kinaCurrent && (at is null || _abyssKinaAt > at)) at = _abyssKinaAt;
                return new(current ? own : null, walletCurrent ? _abyssWallet?.Count : null,
                    farmCurrent ? _abyssFarmTotal : null, rankCurrent ? _abyssRankPoints : null, at, current ? _abyssSequence : 0)
                {
                    AbyssPointBalanceObservedAt = walletCurrent ? _abyssAt : null,
                    FarmedAbyssPointsObservedAt = farmCurrent ? _abyssFarmAt : null,
                    KinaGainsObservedAt = kinaCurrent ? _abyssKinaAt : null,
                    KinaGainsQualified = kinaCurrent && _abyssKinaGainsQualified,
                    AbyssPointGainsQualified = walletCurrent && _abyssPointGainsQualified,
                    RankPointsObservedAt = rankCurrent ? _abyssRankAt : null,
                    RankPointChangesQualified = rankCurrent && _abyssRankChangesQualified,
                    Status = rankCurrent
                        ? _abyssRankChangesQualified ? "Rangpunktestand und Änderungen bestätigt."
                            : "Rangpunktestand bestätigt. Rangpunktänderungen sind noch nicht qualifiziert."
                        : "Abyss-Punktequelle ist noch nicht qualifiziert.",
                };
            }
        }
    }

    private void BindAbyssRankBaseline(long? rankPoints, DateTimeOffset at)
    {
        // Only the fully validated own world observation supplies this absolute value.
        // It remains independent of the optional inventory transaction and never emits a delta.
        if (_ownCharacter is null || rankPoints is null or < 0) return;
        _abyssRankCharacter = _ownCharacter;
        _abyssRankPoints = rankPoints;
        _abyssRankAt = at;
        _abyssSequence++;
    }

    private void ProcessAbyssRankMessage(string stream, byte[] message, DateTimeOffset at)
    {
        if (!Running || _ownCharacter is not { } own || _identity is null || _identityStream != stream
            || own.Generation != _progressGeneration || !ReferenceEquals(_abyssRankCharacter, own)) return;
        var observation = new PendingAbyssRankUpdate(stream, own, _generation, _abyssRankRevision, at,
            RankPointUpdateMessage.TryParse(message)?.Total);
        EnqueueAbyssObservation(stream, isRank: true, () => ApplyAbyssRankObservation(observation));
    }

    private void EnqueueAbyssObservation(string stream, bool isRank, Action apply, bool isFarm = false)
    {
        if (!Running || _ownCharacter is not null && _identityStream != stream) return;
        if (_abyssObservations.Count >= MaximumPendingAbyssObservations)
        {
            if (isFarm) ResetAbyssFarmContext(rejected: true);
            else if (isRank) ResetAbyssRankContext();
            else ProcessProgressGap(stream, "inventory-dispatch-overflow");
            SafeEvent.Raise(Changed, "Rangpunkte-Netzwerkstatus");
            return;
        }
        _abyssObservations.Enqueue(new(_ownCharacter, _generation, _progressGeneration, isRank, isFarm, apply));
        // Serialize complete rank, farm and inventory observations together. Nested callbacks cannot
        // publish a newer AP/rank fact while another observation's subscribers are still running.
        if (_dispatchingAbyssObservations) return;
        _dispatchingAbyssObservations = true;
        try
        {
            while (_abyssObservations.TryDequeue(out var observation))
                if (Running && _generation == observation.CaptureGeneration
                    && _progressGeneration == observation.ProgressGeneration
                    && ReferenceEquals(_ownCharacter, observation.Character)) observation.Apply();
        }
        finally { _dispatchingAbyssObservations = false; }
    }

    private void ApplyAbyssRankObservation(PendingAbyssRankUpdate observation)
    {
        if (!AbyssRankBindingMatches(observation)) return;
        if (observation.Total is not { } total || _abyssRankPoints is not { } before
                    || _abyssRankAt is not { } previousAt || observation.At == default
                    || observation.Character.ObservedAt is not { } ownAt || ownAt == default
                    || observation.At < previousAt || observation.At < ownAt
                    || observation.At > DateTimeOffset.UtcNow)
        {
            ResetAbyssRankContext();
            SafeEvent.Raise(Changed, "Rangpunkte-Netzwerkstatus");
            return;
        }
        // Both operands are nonnegative I64 totals; their signed difference fits I64.
        // This describes only a net point change, never a kill, death or other cause.
        var delta = total - before;
        _abyssRankPoints = total;
        _abyssRankAt = observation.At;
        _abyssRankChangesQualified = true;
        var sequence = ++_abyssSequence;
        if (delta != 0)
        {
            var change = new NetworkAbyssRankChange(observation.At, sequence, observation.Character, delta);
            foreach (var subscriber in RankPointsChanged?.GetInvocationList().Cast<Action<NetworkAbyssRankChange>>() ?? [])
            {
                if (!AbyssRankBindingMatches(observation)) break;
                try { subscriber(change); }
                catch (Exception error) { LogFile.Error("Rangpunkte-Netzwerkereignis", error); }
            }
        }
        if (AbyssRankBindingMatches(observation))
            SafeEvent.Raise(Changed, "Rangpunkte-Netzwerkstatus");
    }

    private bool AbyssRankBindingMatches(PendingAbyssRankUpdate observation) => Running
        && _generation == observation.CaptureGeneration && _abyssRankRevision == observation.Revision
        && observation.Character.Generation == _progressGeneration && _identityStream == observation.Stream
        && ReferenceEquals(_ownCharacter, observation.Character) && ReferenceEquals(_abyssRankCharacter, observation.Character);

    private void ResetAbyssRankContext()
    {
        _abyssRankCharacter = null;
        _abyssRankPoints = null;
        _abyssRankAt = null;
        _abyssRankChangesQualified = false;
        _abyssRankRevision++;
        var inventory = _abyssObservations.Where(observation => !observation.IsRank).ToArray();
        _abyssObservations.Clear();
        foreach (var observation in inventory) _abyssObservations.Enqueue(observation);
        _abyssSequence++;
    }

    private void ResetAbyssContext()
    {
        _abyssObservations.Clear();
        ResetAbyssPointContext();
        ResetAbyssKinaContext();
        ResetAbyssRankContext();
        ResetAbyssFarmContext();
    }
}
