using System.Collections.Immutable;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private const int MaximumGatheringObjects = 4096, MaximumPendingGatherings = 64;
    private static readonly TimeSpan GatheringPairWindow = TimeSpan.FromSeconds(2);
    private readonly Dictionary<int, GatheringObjectObservation> _gatheringObjects = [];
    private readonly Dictionary<GatheringPairKey, PendingGathering> _gatheringCompletions = [];
    private readonly Dictionary<GatheringPairKey, DateTimeOffset> _gatheringWitnesses = [];
    private readonly Dictionary<GatheringPairKey, DateTimeOffset> _ambiguousGatherings = [];
    private readonly Queue<(string Stream, NetworkGathering Gathering)> _gatheringDispatch = [];
    private bool _dispatchingGatherings;
    private long _gatheringSequence, _gatheringWarningVersion;
    private string? _gatheringWarning;

    // Controlled empty-inventory captures qualify own primary rewards, including Basic+Bonus.
    // Additional-item fields remain unsupported. Outcome bytes are compared, never interpreted.
    // A complete own no-item outcome records an unsuccessful attempt, not a guessed result enum.
    public bool GatheringVerified => true;
    public string? GatheringWarning { get { lock (_gate) return _gatheringWarning; } }
    public long GatheringWarningVersion { get { lock (_gate) return _gatheringWarningVersion; } }
    public string GatheringStatus => !Running ? "Netzwerk-Tracking ist ausgeschaltet."
        : OwnCharacter is null ? "Warte auf die eigene Charaktererkennung."
        : GatheringWarning ?? "Sammelbeute wird über das Netzwerk erfasst.";

    public event Action<NetworkGathering>? GatheringCompleted;

    private readonly record struct GatheringPairKey(int Target, byte First, byte Second);
    private sealed record PendingGathering(GatheringCompletionObservation Completion,
        DateTimeOffset At, NetworkGatheringObject? Object, bool InInstance);

    // Shares capture, framing, transport deduplication and the own-generation gate. Raw entity
    // keys remain in bounded RAM caches; foreign actors and their actions are never saved.
    private void ProcessGatheringMessage(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        if (_ownCharacter is not { } character || _identityStream != stream || !Running
            || character.ObservedAt is { } entered && at < entered) return;
        if (opcode == 0x3634)
        {
            if (GatheringMessage.TryParseObject(message) is not { } item) { _gatheringObjects.Clear(); return; }
            if (_gatheringObjects.Count >= MaximumGatheringObjects && !_gatheringObjects.ContainsKey(item.EntityKey))
                _gatheringObjects.Clear();
            _gatheringObjects[item.EntityKey] = item;
            return;
        }
        if (opcode == 0x3635)
        {
            if (GatheringMessage.TryParseRemoval(message) is { } removed) _gatheringObjects.Remove(removed.EntityKey);
            else _gatheringObjects.Clear();
            return;
        }
        if (opcode == 0x8D34)
        {
            if (GatheringMessage.TryParseOwnershipWitness(message) is not { } witness)
            { RejectGatheringFormat(); return; }
            if (witness.ActorKey != character.Identity.EntityKey) return;
            var key = new GatheringPairKey(witness.TargetKey, witness.OpaqueA, witness.OpaqueB);
            if (_ambiguousGatherings.ContainsKey(key)) return;
            if (_gatheringWitnesses.ContainsKey(key)) { RejectAmbiguousGathering(key, at); return; }
            if (_gatheringCompletions.Remove(key, out var completion))
            { PublishGathering(stream, character, completion); return; }
            if (!GatheringPairCapacityAvailable()) return;
            _gatheringWitnesses.Add(key, at);
            return;
        }
        if (GatheringMessage.TryParseCompletion(message) is not { TargetKey: > 0 } candidate)
        { RejectGatheringFormat(); return; }
        var completionKey = new GatheringPairKey(candidate.TargetKey, candidate.ResultCandidate, candidate.Unknown);
        if (_ambiguousGatherings.ContainsKey(completionKey)) return;
        if (_gatheringCompletions.ContainsKey(completionKey)) { RejectAmbiguousGathering(completionKey, at); return; }
        NetworkGatheringObject? resource = _gatheringObjects.TryGetValue(candidate.TargetKey, out var target)
            ? new(target.DataId, target.OriginSpawnKey, target.SpawnUniqueKey) : null;
        var world = WorldContext.Snapshot;
        var inInstance = world.Status == WorldContextStatus.Confirmed && world.Kind == WorldContextKind.Instance
            && world.BindingGeneration == character.Generation && (world.ObservedAt is null || world.ObservedAt <= at);
        var pending = new PendingGathering(candidate, at, resource, inInstance);
        if (_gatheringWitnesses.Remove(completionKey)) { PublishGathering(stream, character, pending); return; }
        if (!GatheringPairCapacityAvailable()) return;
        _gatheringCompletions.Add(completionKey, pending);
    }

    private void PublishGathering(string stream, OwnCharacterContext character, PendingGathering pending)
    {
        var reward = GatheringMessage.QualifyPrimaryReward(pending.Completion);
        var failed = reward.Status == GatheringRewardQualificationStatus.NoReward;
        if (!failed && reward.Status != GatheringRewardQualificationStatus.Qualified)
        {
            WarnGathering(reward.Status == GatheringRewardQualificationStatus.UnsupportedAdditional
                ? "Sammelbeleg mit ungeklärter Zusatzbeute wurde nicht gezählt."
                : "Ungültiger Sammelbeleg wurde nicht gezählt.");
            return;
        }
        var observation = new NetworkGathering(pending.At, ++_gatheringSequence, character,
            pending.Completion.TargetKey, failed ? [] : ImmutableArray.Create(new NetworkGatheringItem(reward.ItemId!.Value, reward.Quantity!.Value)))
            { Object = pending.Object, Outcome = failed ? NetworkGatheringOutcome.Failed : NetworkGatheringOutcome.Harvested,
                InInstance = pending.InInstance };
        if (_gatheringDispatch.Count >= MaximumPendingGatherings)
        {
            WarnGathering("Unvollständiger Sammelbeleg wurde nicht gezählt.");
            return;
        }
        _gatheringDispatch.Enqueue((stream, observation));
        // A subscriber can synchronously reenter the common source. Every subscriber must see
        // sequence N before N+1, otherwise the recorder's duplicate guard would lose a real gain.
        if (_dispatchingGatherings) return;
        _dispatchingGatherings = true;
        try
        {
            while (_gatheringDispatch.TryDequeue(out var queued))
                foreach (var subscriber in GatheringCompleted?.GetInvocationList().Cast<Action<NetworkGathering>>() ?? [])
                {
                    if (!ReferenceEquals(queued.Gathering.Character, _ownCharacter) || _identityStream != queued.Stream || !Running) break;
                    try { subscriber(queued.Gathering); }
                    catch (Exception error) { LogFile.Error("Sammelbeute-Netzwerkereignis", error); }
                }
        }
        finally { _dispatchingGatherings = false; }
    }

    private void ExpireGatheringPairs(DateTimeOffset at)
    {
        var expiredCompletions = _gatheringCompletions.Where(pair => (at - pair.Value.At).Duration() > GatheringPairWindow).Select(pair => pair.Key).ToArray();
        var expiredWitnesses = _gatheringWitnesses.Where(pair => (at - pair.Value).Duration() > GatheringPairWindow).Select(pair => pair.Key).ToArray();
        foreach (var key in expiredCompletions) _gatheringCompletions.Remove(key);
        foreach (var key in expiredWitnesses) _gatheringWitnesses.Remove(key);
        foreach (var key in _ambiguousGatherings.Where(pair => (at - pair.Value).Duration() > GatheringPairWindow).Select(pair => pair.Key).ToArray())
            _ambiguousGatherings.Remove(key);
        if (expiredCompletions.Length + expiredWitnesses.Length > 0)
            WarnGathering("Unvollständiger Sammelbeleg wurde nicht gezählt.");
    }

    private bool GatheringPairCapacityAvailable()
    {
        if (_gatheringCompletions.Count + _gatheringWitnesses.Count + _ambiguousGatherings.Count < MaximumPendingGatherings) return true;
        ClearGatheringPairs();
        WarnGathering("Unvollständiger Sammelbeleg wurde nicht gezählt.");
        return false;
    }

    private void RejectAmbiguousGathering(GatheringPairKey key, DateTimeOffset at)
    {
        _gatheringCompletions.Remove(key); _gatheringWitnesses.Remove(key);
        _ambiguousGatherings[key] = at;
        WarnGathering("Mehrdeutiger Sammelbeleg wurde nicht gezählt.");
    }

    private void RejectGatheringFormat()
    {
        ClearGatheringPairs();
        WarnGathering("Ungültiger Sammelbeleg wurde nicht gezählt.");
    }

    private void WarnGathering(string warning)
    {
        _gatheringWarning = warning;
        _gatheringWarningVersion++;
        SafeEvent.Raise(Changed, "Sammelbeute-Netzwerkstatus");
    }

    private void ClearGatheringPairs()
    { _gatheringCompletions.Clear(); _gatheringWitnesses.Clear(); _ambiguousGatherings.Clear(); }

    private void ResetGatheringContext()
    { _gatheringObjects.Clear(); ClearGatheringPairs(); _gatheringDispatch.Clear(); _gatheringWarning = null; }
}
