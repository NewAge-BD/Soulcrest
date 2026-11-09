using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private const int MaximumAchievementNotifications = 128;
    private const long MaximumAchievementNotificationBytes = 8 * 1024 * 1024;
    private readonly Guid _achievementSourceInstance = Guid.NewGuid();
    private readonly Queue<NetworkAchievementObservation> _achievementNotifications = new();
    private NetworkAchievementObservation? _achievementSnapshot;
    private long _achievementSequence;
    private long _achievementNotificationBytes;
    private bool _achievementDispatchActive;
    private TaskCompletionSource _achievementDispatchIdle = CompletedAchievementDispatch();
    private AchievementIngressDiagnostics _achievementDiagnostics = new();

    public event Action<NetworkAchievementObservation>? AchievementObserved;

    public NetworkAchievementObservation AchievementSnapshot
    {
        get
        {
            lock (_gate)
                return _achievementSnapshot ??= new(0, _progressGeneration, DateTimeOffset.MinValue, null,
                    NetworkCombatDomain<CharacterAchievementState>.Unknown, NetworkAchievementObservationKind.Invalidated)
                { SourceInstanceId = _achievementSourceInstance };
        }
    }

    public AchievementIngressDiagnostics AchievementDiagnostics
    {
        get
        {
            lock (_gate)
                return _achievementDiagnostics with
                {
                    PendingNotifications = _achievementNotifications.Count,
                    FramingResyncs = Resyncs,
                    CharacterBound = _combatOwnQualified && _combatSnapshot.Character is not null,
                    SourceFresh = _combatSnapshot.Achievements.IsFresh,
                    SourceStatus = _combatSnapshot.Achievements.Status
                };
        }
    }

    // An idle barrier for shutdown/replay tests. Await only outside the network gate; subscribers
    // must never synchronously wait on their own notification dispatcher.
    internal Task WaitForAchievementNotificationsAsync()
    {
        lock (_gate) return _achievementDispatchIdle.Task;
    }

    private static TaskCompletionSource CompletedAchievementDispatch()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    private void CountAchievementIngress(ushort opcode, byte[] message)
    {
        _achievementDiagnostics = opcode switch
        {
            0x3620 => _achievementDiagnostics with
            {
                Entry3620Framed = _achievementDiagnostics.Entry3620Framed + 1,
                Entry3620Parsed = _achievementDiagnostics.Entry3620Parsed +
                    (OwnIdentityMessage.TryParseEntryCandidate(message) is not null ? 1 : 0)
            },
            // 0x3621 is the qualified preparation shape, never a complete Own binding.
            0x3621 => _achievementDiagnostics with
            {
                Own3621Framed = _achievementDiagnostics.Own3621Framed + 1,
                Own3621Parsed = _achievementDiagnostics.Own3621Parsed +
                    (OwnIdentityMessage.IsWorldEntryPreparation(message) ? 1 : 0)
            },
            0x3633 => _achievementDiagnostics with
            {
                Own3633Framed = _achievementDiagnostics.Own3633Framed + 1,
                Own3633Parsed = _achievementDiagnostics.Own3633Parsed +
                    (OwnIdentityMessage.TryParseWorld(message) is not null ? 1 : 0)
            },
            0x5683 => _achievementDiagnostics with { Lists5683Framed = _achievementDiagnostics.Lists5683Framed + 1 },
            0x5684 => _achievementDiagnostics with { Updates5684Framed = _achievementDiagnostics.Updates5684Framed + 1 },
            0x5686 => _achievementDiagnostics with { Rewards5686Framed = _achievementDiagnostics.Rewards5686Framed + 1 },
            _ => _achievementDiagnostics
        };
    }

    private void ObserveAchievementBaseline(DateTimeOffset at)
    {
        _achievementDiagnostics = _achievementDiagnostics with { OwnBindings = _achievementDiagnostics.OwnBindings + 1 };
        PublishAchievementObservation(_combatSnapshot.Character, _combatSnapshot.Achievements,
            NetworkAchievementObservationKind.Baseline, at);
    }

    private void ObserveAchievementChange(NetworkCombatDomain<CharacterAchievementState> previous,
        ushort opcode, DateTimeOffset at)
    {
        var current = _combatSnapshot.Achievements;
        if (ReferenceEquals(previous, current)) return;
        var kind = !current.IsFresh ? NetworkAchievementObservationKind.Invalidated : opcode switch
        {
            0x5683 => NetworkAchievementObservationKind.Baseline,
            // The first absolute patch imports current facts without inventing earlier values
            // or live completions. Subsequent patches compare against this fresh partial source.
            0x5684 when !previous.IsFresh || previous.Value is null => NetworkAchievementObservationKind.RecoveryBaseline,
            0x5684 => NetworkAchievementObservationKind.Update,
            0x5686 => NetworkAchievementObservationKind.Reward,
            _ => NetworkAchievementObservationKind.Invalidated
        };
        PublishAchievementObservation(_combatSnapshot.Character, current, kind, at);
    }

    private void PublishAchievementObservation(OwnCharacterContext? character,
        NetworkCombatDomain<CharacterAchievementState> domain, NetworkAchievementObservationKind kind,
        DateTimeOffset at)
    {
        // Called under _gate. Enqueueing does no I/O and never invokes an external subscriber.
        var observation = CreateAchievementObservation(character, domain, kind, at);
        _achievementDiagnostics = kind switch
        {
            NetworkAchievementObservationKind.Baseline or NetworkAchievementObservationKind.RecoveryBaseline when domain.IsFresh => _achievementDiagnostics with
                { AcceptedBaselines = _achievementDiagnostics.AcceptedBaselines + 1 },
            NetworkAchievementObservationKind.Update => _achievementDiagnostics with
                { AcceptedUpdates = _achievementDiagnostics.AcceptedUpdates + 1 },
            NetworkAchievementObservationKind.Reward => _achievementDiagnostics with
                { AcceptedRewards = _achievementDiagnostics.AcceptedRewards + 1 },
            NetworkAchievementObservationKind.Invalidated => _achievementDiagnostics with
                { Invalidations = _achievementDiagnostics.Invalidations + 1 },
            _ => _achievementDiagnostics
        };
        var bytes = AchievementObservationBytes(observation);
        if (_achievementNotifications.Count >= MaximumAchievementNotifications
            || _achievementNotificationBytes + bytes > MaximumAchievementNotificationBytes)
        {
            // No guessed intermediate milestones after overload. Preserve a marked gap followed
            // by the actual current immutable RAM state as a recovery baseline, in this order.
            _achievementNotifications.Clear();
            _achievementNotificationBytes = 0;
            _achievementDiagnostics = _achievementDiagnostics with { QueueOverflows = _achievementDiagnostics.QueueOverflows + 1 };
            EnqueueAchievementNotification(CreateAchievementObservation(character, Stale(domain),
                NetworkAchievementObservationKind.Invalidated, at) with { HistoryGap = true });
            observation = CreateAchievementObservation(character, domain,
                domain.IsFresh ? NetworkAchievementObservationKind.RecoveryBaseline : NetworkAchievementObservationKind.Invalidated,
                at) with { HistoryGap = true };
        }
        EnqueueAchievementNotification(observation);
        _achievementSnapshot = observation;
        if (_achievementDispatchActive) return;
        _achievementDispatchActive = true;
        _achievementDispatchIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.QueueUserWorkItem(static (NetworkLootService source) => source.DispatchAchievementNotifications(),
            this, preferLocal: false);
    }

    private NetworkAchievementObservation CreateAchievementObservation(OwnCharacterContext? character,
        NetworkCombatDomain<CharacterAchievementState> domain, NetworkAchievementObservationKind kind, DateTimeOffset at) =>
        new(++_achievementSequence, _progressGeneration, at, character, domain, kind)
        { SourceInstanceId = _achievementSourceInstance };

    private static long AchievementObservationBytes(NetworkAchievementObservation observation) =>
        observation.Domain.Value is { } value ? 192L + value.Achievements.Count * 48L
            + value.RewardGroups.Sum(group => 32L + group.Value.Length) : 192;

    private void EnqueueAchievementNotification(NetworkAchievementObservation observation)
    {
        _achievementNotifications.Enqueue(observation);
        _achievementNotificationBytes += AchievementObservationBytes(observation);
    }

    private void DispatchAchievementNotifications()
    {
        while (true)
        {
            NetworkAchievementObservation observation;
            Action<NetworkAchievementObservation>? subscribers;
            lock (_gate)
            {
                if (!_achievementNotifications.TryDequeue(out observation!))
                {
                    _achievementDispatchActive = false;
                    _achievementDispatchIdle.TrySetResult();
                    return;
                }
                _achievementNotificationBytes -= AchievementObservationBytes(observation);
                subscribers = AchievementObserved;
            }
            if (subscribers is null) continue;
            foreach (var subscriber in subscribers.GetInvocationList().Cast<Action<NetworkAchievementObservation>>())
            {
                try { subscriber(observation); }
                catch
                {
                    // Keep the ordered stream alive without writing private exception details or
                    // doing file I/O. A broken consumer cannot suppress another consumer's event.
                    lock (_gate) _achievementDiagnostics = _achievementDiagnostics with
                        { SubscriberFailures = _achievementDiagnostics.SubscriberFailures + 1 };
                }
            }
        }
    }
}
