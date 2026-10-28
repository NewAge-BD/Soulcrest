using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private readonly AbyssOwnDeathLife _abyssDeathLife = new();
    private readonly AbyssPlayerRegistry _abyssPlayers = new();
    private GameMessageStream? _abyssCombatOwnTransport;
    private const int MaximumPendingCombatEvents = 64;
    private readonly Queue<PendingCombatEvent> _abyssCombatEvents = new();
    private bool _dispatchingAbyssCombatEvents;
    private sealed record PendingCombatEvent(NetworkAbyssCombatEvent Observation, string Stream,
        long CaptureGeneration, long Revision, WorldContextSnapshot World, GameMessageStream? Transport);
    private long _abyssCombatRevision, _abyssCombatSequence;
    private static readonly NetworkAbyssCombatCapabilities WireCombatCapabilities = new(true, true, false);

    // Controlled 2026-10-27 Own -> complete PlayerAppear -> own-caster CharDie qualifies Kill;
    // the independent full Own -> own PC-caster death comparison qualifies Death separately.
    // The bounded victim-entity gate deliberately cannot rearm the same entity on a soft Own.
    // Assist still needs explicit own participation; nearby deaths or rewards cannot supply it.
    // No user setting or AP/rank delta can promote these missing proofs into a capability.
    public NetworkAbyssCombatCapabilities CombatCapabilities
    {
        get
        {
            lock (_gate)
            {
                _abyssDeathLife.ObserveCapture(_generation);
                _abyssPlayers.ObserveCapture(_generation);
                return WireCombatCapabilities with
                {
                    DeathQualified = WireCombatCapabilities.DeathQualified && !_abyssDeathLife.Overflowed,
                    KillQualified = WireCombatCapabilities.KillQualified && !_abyssPlayers.Overflowed
                };
            }
        }
    }

    public event Action<NetworkAbyssCombatEvent>? CombatEvent;

    private GameMessageStream? AbyssCombatTransport(string stream) =>
        _streams.FirstOrDefault(pair => pair.Key.StreamKey == stream).Value.Messages;

    private void BindAbyssCombatOwnTransport(string stream) => _abyssCombatOwnTransport = AbyssCombatTransport(stream);

    private void ProcessAbyssActorRevocation(string stream, byte[] message, DateTimeOffset at) =>
        RevokeAbyssActorEvidence(stream, AbyssActorRevocationPrefix.TryRead(message), at);

    private void RevokeAbyssActorEvidence(string stream, int? entity, DateTimeOffset at)
    {
        // Other connections have no authority over this own stream's player observations.
        if (_identityStream is not null && _identityStream != stream) return;
        if (_ownCharacter is not { } own || !AbyssCombatOwnBindingMatches(own, stream, _generation, _abyssCombatRevision))
        { _abyssPlayers.Invalidate(); return; }
        var context = CombatPlayerContext(own, stream, WorldContext.Snapshot, at);
        if (context is null) return;
        // Known NPC and narrowly qualified CharDisappear forms revoke only their explicit actor.
        // A malformed/unsupported form still revokes all PC evidence; no guessed target survives.
        if (entity is not { } target)
        { _abyssPlayers.Invalidate(); return; }
        var overflowed = _abyssPlayers.Overflowed;
        _abyssPlayers.RevokeActor(context, target, at, DateTimeOffset.UtcNow);
        PublishPlayerRegistryOverflow(overflowed);
    }

    private void ProcessAbyssPlayerAppearance(string stream, byte[] message, DateTimeOffset at)
    {
        if (_ownCharacter is not { } own || !AbyssCombatOwnBindingMatches(own, stream, _generation, _abyssCombatRevision)) return;
        var context = CombatPlayerContext(own, stream, WorldContext.Snapshot, at);
        if (context is null) return;
        if (AbyssPlayerAppearanceMessage.TryParse(message) is not { } player)
        {
            // Unsupported appearance could conceal actor reuse. Existing classifications cannot
            // survive this uncertainty, and another appearance is not an alive-state proof.
            _abyssPlayers.Invalidate();
            return;
        }
        var overflowed = _abyssPlayers.Overflowed;
        _abyssPlayers.ObserveAppearance(context, player, at, DateTimeOffset.UtcNow);
        PublishPlayerRegistryOverflow(overflowed);
    }

    private void ProcessAbyssCombatMessage(string stream, byte[] message, DateTimeOffset at)
    {
        if (_ownCharacter is not { } own || !AbyssCombatOwnBindingMatches(own, stream, _generation, _abyssCombatRevision)) return;
        if (AbyssPlayerCasterDeathMessage.TryParse(message) is not { } death)
        {
            // A fully read neutral empty-caster death only consumes its explicit victim. It is
            // not a PC-caster result, nor proof of NPC type, a new life, or any reward.
            RevokeAbyssActorEvidence(stream, AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(message), at);
            return;
        }
        var world = WorldContext.Snapshot;
        if (!CombatAbyssWorld(world, own, at)) { _abyssPlayers.Invalidate(); return; }
        var context = CombatPlayerContext(own, stream, world, at);
        string? opponent = null;
        string? opponentClass = null;
        int? opponentGrade = null;
        var overflowed = _abyssPlayers.Overflowed;
        var ownKill = context is not null && _abyssPlayers.TryConsumeOwnKillWithMetadata(context, death, at,
            DateTimeOffset.UtcNow, out opponent, out opponentClass, out opponentGrade);
        PublishPlayerRegistryOverflow(overflowed);
        var kind = death.VictimEntityKey == own.Identity.EntityKey ? NetworkAbyssCombatKind.Death : NetworkAbyssCombatKind.Kill;
        if (!CombatCapabilities.Supports(kind) || !AbyssCombatOwnBindingMatches(own, stream, _generation, _abyssCombatRevision)
            || !ReferenceEquals(WorldContext.Snapshot, world)) return;
        if (kind == NetworkAbyssCombatKind.Death
            && !_abyssDeathLife.TryConsume(own, death, at, DateTimeOffset.UtcNow, _generation))
        {
            if (_abyssDeathLife.Overflowed) SafeEvent.Raise(Changed, "Abyss-Kampfergebnis-Netzwerkstatus");
            return;
        }
        if (kind == NetworkAbyssCombatKind.Kill && !ownKill) return;
        if (kind == NetworkAbyssCombatKind.Death && context is not null)
            _abyssPlayers.TryGetOwnDeathCasterMetadata(context, death, at, DateTimeOffset.UtcNow,
                out opponentClass, out opponentGrade);

        // Consume before callbacks. FIFO publication also prevents a different nested kill from
        // reaching later subscribers ahead of the outer event. RP amounts remain unattributed.
        var observation = new NetworkAbyssCombatEvent(at, ++_abyssCombatSequence, own,
            kind, kind == NetworkAbyssCombatKind.Death ? death.CasterName : opponent)
        {
            OpponentClassId = opponentClass,
            OpponentAbyssRankGrade = opponentGrade
        };
        if (_abyssCombatEvents.Count >= MaximumPendingCombatEvents)
        {
            // Lost ordered observations invalidate authority instead of silently resuming a feed.
            ProcessProgressGap(stream, "combat-dispatch-overflow", at);
            return;
        }
        _abyssCombatEvents.Enqueue(new(observation, stream, _generation, _abyssCombatRevision, world, context?.Transport));
        if (_dispatchingAbyssCombatEvents) return;
        _dispatchingAbyssCombatEvents = true;
        try
        {
            while (_abyssCombatEvents.TryDequeue(out var pending))
            {
                foreach (var subscriber in CombatEvent?.GetInvocationList().Cast<Action<NetworkAbyssCombatEvent>>() ?? [])
                {
                    if (!CombatPublicationCurrent(pending)) break;
                    try { subscriber(pending.Observation); }
                    // Subscriber exceptions can contain the live opponent name; never log their body.
                    catch (Exception) { LogFile.Append("errors.log", "Abyss combat subscriber failed." + Environment.NewLine); }
                }
            }
        }
        finally
        {
            _dispatchingAbyssCombatEvents = false;
            _abyssCombatEvents.Clear();
        }
    }

    private bool CombatPublicationCurrent(PendingCombatEvent pending) =>
        AbyssCombatBindingMatches(pending.Observation.Character, pending.Stream, pending.CaptureGeneration,
            pending.Revision, pending.Observation.Kind) && ReferenceEquals(WorldContext.Snapshot, pending.World)
        && (pending.Observation.Kind != NetworkAbyssCombatKind.Kill || pending.Transport is not null
            && ReferenceEquals(pending.Transport, _abyssCombatOwnTransport)
            && ReferenceEquals(pending.Transport, AbyssCombatTransport(pending.Stream)));

    private bool AbyssCombatOwnBindingMatches(OwnCharacterContext own, string stream, long generation, long revision) =>
        Running && _generation == generation && _abyssCombatRevision == revision
        && _progressGeneration == own.Generation && ReferenceEquals(_ownCharacter, own)
        && ReferenceEquals(_identity, own.Identity) && _identityStream == stream;

    private bool AbyssCombatBindingMatches(OwnCharacterContext own, string stream, long generation, long revision,
        NetworkAbyssCombatKind kind) => CombatCapabilities.Supports(kind)
        && AbyssCombatOwnBindingMatches(own, stream, generation, revision);

    private AbyssPlayerRegistry.Context? CombatPlayerContext(OwnCharacterContext own, string stream,
        WorldContextSnapshot world, DateTimeOffset at)
    {
        if (!CombatAbyssWorld(world, own, at) || _abyssCombatOwnTransport is not { } transport
            || !ReferenceEquals(transport, AbyssCombatTransport(stream)))
        { _abyssPlayers.Invalidate(); return null; }
        return new(own, stream, transport, _generation, _abyssCombatRevision, world);
    }

    private void PublishPlayerRegistryOverflow(bool alreadyOverflowed)
    {
        if (!alreadyOverflowed && _abyssPlayers.Overflowed && WireCombatCapabilities.KillQualified)
            SafeEvent.Raise(Changed, "Abyss-Kampfergebnis-Netzwerkstatus");
    }

    private static bool CombatAbyssWorld(WorldContextSnapshot world, OwnCharacterContext own, DateTimeOffset at) =>
        world.Status == WorldContextStatus.Confirmed && world.BindingGeneration == own.Generation
        && world.Kind == WorldContextKind.Instance && world.Category == "Abyss"
        && world.ObservedAt is { } entered && entered != default && entered <= at;

    private void ResetAbyssCombatContext()
    {
        _abyssCombatRevision++;
        _abyssCombatEvents.Clear();
        _abyssDeathLife.ObserveCapture(_generation);
        _abyssPlayers.ObserveCapture(_generation);
        _abyssPlayers.Invalidate();
        _abyssCombatOwnTransport = null;
    }
}
