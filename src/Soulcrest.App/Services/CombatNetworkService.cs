using System.Buffers.Binary;
using Soulcrest.App.Network;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Soulcrest.Core.Progress;

namespace Soulcrest.App.Services;

/// <summary>Only decoded, currently own-group-related wire observations leave the background worker.</summary>
public sealed record CombatNetworkObservation(long Sequence, long ReceivedElapsedTicks, DateTimeOffset CapturedAtUtc,
    CombatSourceContext Context, long CaptureGeneration, CombatWireObservation Observation);
public sealed record CombatNetworkBoundary(long Sequence, CombatSourceContext? Context, string Code,
    long? ReceivedElapsedTicks = null, DateTimeOffset? CapturedAtUtc = null);

/// <summary>Anonymous diagnostic counters, never player data or a claim of complete combat measurement.</summary>
public sealed record CombatNetworkSnapshot(bool Observing, long Frames, long Supported, long Decoded, long Rejected,
    long Unbound, long Published, long Boundaries, long DroppedRecords, long SubscriberFailures,
    int ConfirmedOwnStreams, int ConfirmedRosters, ushort? LastRejectedOpcode);

/// <summary>
/// Background decoder on the one existing passive source. Subscribing does not start capture.
/// Transport generation and fresh Own + party membership are required before publishing group data.
/// This stage never equates a hit amount with damage, HP zero with a kill, or effect changes with casts.
/// No files, hidden statistics, network requests or endpoint identifiers are created here.
/// </summary>
public sealed class CombatNetworkService : IDisposable
{
    private sealed class Scope(long captureGeneration, long revision)
    {
        public long CaptureGeneration { get; } = captureGeneration;
        public long Revision { get; set; } = revision;
        public OwnCharacterIdentity? Own;
        public OwnWorldObservation? World;
        public CombatPartySnapshot? Party;
        public HashSet<int> BossTargets { get; } = [];
    }

    private readonly object _gate = new();
    private readonly NetworkCombatSubscription _subscription;
    private readonly NetworkCaptureCoordinator _capture;
    private readonly Dictionary<string, Scope> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _minimumGenerations = new(StringComparer.Ordinal);
    private int _disposed;
    private long _revision, _lastSequence, _lastElapsedTicks;
    private long _frames, _supported, _decoded, _rejected, _unbound, _published, _boundaries, _dropped, _subscriberFailures;
    private ushort? _lastRejectedOpcode;
    private readonly Task _completion;
    private long _lastChangedTicks;

    public CombatNetworkService(INetworkCombatSource source, NetworkCaptureCoordinator capture)
    {
        _capture = capture;
        _subscription = source.SubscribeCombat();
        _completion = Task.Run(ConsumeAsync);
    }

    public event Action<CombatNetworkObservation>? ObservationReceived;
    public event Action<CombatNetworkBoundary>? BoundaryReceived;
    public event Action? Changed;
    public Task Completion => _completion;
    public CombatNetworkSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new(Volatile.Read(ref _disposed) == 0 && !_completion.IsCompleted,
                _frames, _supported, _decoded, _rejected, _unbound, _published, _boundaries, _dropped,
                _subscriberFailures, _scopes.Values.Count(s => s.Own is not null),
                _scopes.Values.Count(s => s.Party is not null), _lastRejectedOpcode);
        }
    }

    /// <summary>Independent capture demand for the future meter controls. Observation alone requests no recorder.</summary>
    public void SetCaptureRequired(bool required)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _capture.SetDemand(NetworkCaptureConsumer.Combat, required);
        if (Volatile.Read(ref _disposed) != 0) _capture.SetDemand(NetworkCaptureConsumer.Combat, false);
    }

    /// <summary>Fresh Own map fields for exactly this logical context; no inferred instance or dungeon identity.</summary>
    public OwnWorldObservation? GetConfirmedWorld(CombatSourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate) return Volatile.Read(ref _disposed) == 0 && _scopes.TryGetValue(context.StreamId, out var scope)
            && scope.Revision == context.Generation && scope.Own is not null ? scope.World : null;
    }

    /// <summary>Called only by an encounter adapter with an independently confirmed boss catalog/instance join.</summary>
    public bool SetConfirmedBossTargets(CombatSourceContext context, IEnumerable<int> entityKeys)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entityKeys);
        var keys = entityKeys.Take(17).ToArray();
        if (keys.Length > 16 || keys.Any(key => key <= 0)) throw new ArgumentException("Invalid boss targets.", nameof(entityKeys));
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_scopes.TryGetValue(context.StreamId, out var scope) ||
                scope.Revision != context.Generation || scope.Own is null || scope.Party is null) return false;
            scope.BossTargets.Clear();
            scope.BossTargets.UnionWith(keys);
            return true;
        }
    }

    private async Task ConsumeAsync()
    {
        while (await _subscription.ReadAsync().ConfigureAwait(false) is { } envelope)
        {
            if (Volatile.Read(ref _disposed) != 0) break;
            CombatNetworkObservation? observation;
            CombatNetworkBoundary? boundary;
            lock (_gate) (observation, boundary) = Process(envelope);
            if (boundary is not null) Raise(BoundaryReceived, boundary with
            {
                ReceivedElapsedTicks = Math.Max(envelope.ReceivedElapsedTicks, _lastElapsedTicks),
                CapturedAtUtc = envelope.CapturedAtUtc
            });
            if (observation is not null) Raise(ObservationReceived, observation);
            if (boundary is not null || envelope.ReceivedElapsedTicks - _lastChangedTicks >= TimeSpan.TicksPerMillisecond * 250)
            {
                _lastChangedTicks = envelope.ReceivedElapsedTicks;
                foreach (var handler in Changed?.GetInvocationList().Cast<Action>() ?? [])
                {
                    if (Volatile.Read(ref _disposed) != 0) break;
                    try { handler(); } catch { lock (_gate) _subscriberFailures++; }
                }
            }
        }
    }

    private (CombatNetworkObservation?, CombatNetworkBoundary?) Process(CombatCaptureEnvelope input)
    {
        if (input.Sequence <= _lastSequence || input.ReceivedElapsedTicks < _lastElapsedTicks)
        {
            _rejected++;
            _scopes.Clear();
            _boundaries++;
            return (null, new(input.Sequence, null, "invalid-observation-order"));
        }
        _lastSequence = input.Sequence;
        _lastElapsedTicks = input.ReceivedElapsedTicks;
        if (input.Kind != CombatCaptureKind.Message)
        {
            _boundaries++;
            if (input.Kind == CombatCaptureKind.QueueOverflow) _dropped += input.DroppedRecords;
            CombatSourceContext? previous = null;
            if (input.StreamToken is { } token)
            {
                if (!_minimumGenerations.ContainsKey(token) && _minimumGenerations.Count >= 128)
                {
                    _minimumGenerations.Clear();
                    _scopes.Clear();
                    return (null, new(input.Sequence, null, "worker-context-limit"));
                }
                if (_scopes.Remove(token, out var scope)) previous = new(token, scope.Revision);
                _minimumGenerations[token] = Math.Max(_minimumGenerations.GetValueOrDefault(token), input.Generation + 1);
            }
            else _scopes.Clear();
            return (null, new(input.Sequence, previous, input.Kind.ToString()));
        }

        _frames++;
        if (input.StreamToken is null || input.Generation < 0 || input.CapturedAtUtc is null || input.Message.Length < 2)
        {
            _rejected++;
            _scopes.Clear();
            _boundaries++;
            return (null, new(input.Sequence, null, "invalid-message-context"));
        }
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(input.Message.Span);
        if (_minimumGenerations.TryGetValue(input.StreamToken, out var minimum) && input.Generation < minimum)
        { _rejected++; _lastRejectedOpcode = opcode; return (null, null); }
        if (_scopes.TryGetValue(input.StreamToken, out var oldState) && oldState.CaptureGeneration != input.Generation)
        {
            // A missing boundary is itself a loss of authority. Never let a stale Own restore an older epoch.
            if (oldState.CaptureGeneration > input.Generation)
            { _rejected++; _lastRejectedOpcode = opcode; return (null, null); }
            _scopes.Remove(input.StreamToken);
            _minimumGenerations[input.StreamToken] = input.Generation;
            _boundaries++;
            return (null, new(input.Sequence, new(input.StreamToken, oldState.Revision), "unannounced-generation-change"));
        }
        // Static party/raid opcode hints are not proven decoders. They may revoke authority only.
        var unsupportedPartyUpdate = opcode is 0x920D or 0x9222 or 0x921F or 0x9219 or
            0x9602 or 0x960A or 0x961A or 0x961B or 0x9622 or 0x962E;
        if (!CombatProtocolDecoder.IsSupported(opcode) && opcode is not (0x3633 or 0x3620 or 0x3621) && !unsupportedPartyUpdate)
            return (null, null);
        _supported++;
        if (!_scopes.ContainsKey(input.StreamToken) && (_scopes.Count >= 64 || _minimumGenerations.Count >= 128))
        {
            _scopes.Clear();
            _minimumGenerations.Clear();
            _boundaries++;
            return (null, new(input.Sequence, null, "worker-context-limit"));
        }
        var state = GetScope(input.StreamToken, input.Generation);
        CombatSourceContext Context() => new(input.StreamToken, state.Revision);

        if (unsupportedPartyUpdate)
        {
            var previous = Context();
            state.Party = null;
            state.BossTargets.Clear();
            state.Revision = ++_revision;
            _rejected++;
            _lastRejectedOpcode = opcode;
            _boundaries++;
            return (null, new(input.Sequence, previous, "unsupported-party-update"));
        }

        // World entry revokes the old character/roster before any new snapshots or reused entity IDs.
        if (opcode is 0x3620 or 0x3621)
        {
            var previous = Context();
            state.Own = null;
            state.World = null;
            state.Party = null;
            state.BossTargets.Clear();
            state.Revision = ++_revision;
            _boundaries++;
            return (null, new(input.Sequence, previous, "world-entry"));
        }
        if (opcode == 0x3633)
        {
            var previous = Context();
            state.World = OwnIdentityMessage.TryParseWorld(input.Message.Span);
            state.Own = state.World?.Identity;
            state.Party = null;
            state.BossTargets.Clear();
            state.Revision = ++_revision;
            if (state.Own is null) { _rejected++; _lastRejectedOpcode = opcode; }
            else _decoded++;
            _boundaries++;
            return (null, new(input.Sequence, previous, state.Own is null ? "own-rejected" : "own-confirmed"));
        }

        var decoded = CombatProtocolDecoder.TryParse(input.Message.Span);
        if (decoded is null)
        {
            var previous = Context();
            _rejected++;
            _lastRejectedOpcode = opcode;
            state.Party = null;
            state.BossTargets.Clear();
            state.Revision = ++_revision;
            _boundaries++;
            return (null, new(input.Sequence, previous, "combat-format-rejected"));
        }
        _decoded++;
        if (decoded is CombatWireNpcAppearance conflictingNpc && state.Party?.Members.Any(
            member => member.EntityKey == conflictingNpc.Value.EntityKey) == true)
        {
            // A reused/contradictory entity cannot inherit the old player's attribution.
            var previous = Context();
            state.Party = null;
            state.BossTargets.Clear();
            state.Revision = ++_revision;
            _unbound++;
            _boundaries++;
            return (null, new(input.Sequence, previous, "npc-roster-entity-conflict"));
        }
        CombatNetworkBoundary? rosterBoundary = null;
        if (decoded is CombatWireParty snapshot)
        {
            var own = state.Own;
            // Server-scoped identity is required. The snapshot already includes own; never append it by guess.
            if (own is null || snapshot.Value.Members.Count(m => m.CharacterId == own.CharacterId &&
                (m.BirthServerId == own.ServerId || m.CurrentServerId == own.ServerId) && m.EntityKey == own.EntityKey) != 1)
            {
                var previous = Context();
                var hadParty = state.Party is not null;
                state.Party = null;
                state.BossTargets.Clear();
                _unbound++;
                if (!hadParty) return (null, null);
                state.Revision = ++_revision;
                _boundaries++;
                return (null, new(input.Sequence, previous, "party-membership-unconfirmed"));
            }
            if (snapshot.Value.Members.Any(member => state.BossTargets.Contains(member.EntityKey)))
            {
                var previous = Context();
                state.Party = null;
                state.BossTargets.Clear();
                state.Revision = ++_revision;
                _unbound++;
                _boundaries++;
                return (null, new(input.Sequence, previous, "roster-boss-entity-conflict"));
            }
            if (state.Party?.PartyKey != snapshot.Value.PartyKey)
            {
                state.BossTargets.Clear();
                if (state.Party is not null)
                {
                    var previous = Context();
                    state.Revision = ++_revision;
                    _boundaries++;
                    rosterBoundary = new(input.Sequence, previous, "party-changed");
                }
            }
            state.Party = snapshot.Value with { Members = Array.AsReadOnly(snapshot.Value.Members.ToArray()) };
            decoded = new CombatWireParty(state.Party);
        }
        else if (state.Own is null || state.Party is null || !BelongsToParty(decoded, state.Party, state.BossTargets))
        {
            _unbound++;
            return (null, null);
        }
        else if (decoded is CombatWirePartyDeparture)
        {
            var previous = Context();
            state.Party = null;
            state.BossTargets.Clear();
            state.Revision = ++_revision;
            _boundaries++;
            return (null, new(input.Sequence, previous, "party-departure"));
        }
        else if (decoded is CombatWireMemberStatus status)
        {
            state.Party = state.Party! with { Members = state.Party!.Members.Select(m =>
                m.CharacterId == status.Value.CharacterId
                    ? m with { ClassDataId = status.Value.ClassDataId, CombatPower = status.Value.CombatPower } : m).ToArray() };
        }
        // Discovery/reappearance and disappearance revoke old entity bindings even in one TCP epoch.
        // The encounter adapter must independently confirm a fresh appearance before binding again.
        if (decoded is CombatWireNpcAppearance appeared) state.BossTargets.Remove(appeared.Value.EntityKey);
        if (decoded is CombatWireNpcDeparture departed) state.BossTargets.Remove(departed.Value.EntityKey);
        _published++;
        return (new(input.Sequence, input.ReceivedElapsedTicks, input.CapturedAtUtc.Value, Context(), input.Generation, decoded), rosterBoundary);
    }

    private Scope GetScope(string token, long generation)
    {
        if (_scopes.TryGetValue(token, out var existing) && existing.CaptureGeneration == generation) return existing;
        _minimumGenerations[token] = generation;
        return _scopes[token] = new(generation, ++_revision);
    }

    private static bool BelongsToParty(CombatWireObservation observation, CombatPartySnapshot party, IReadOnlySet<int> bosses)
    {
        bool Entity(int id) => party.Members.Any(m => m.EntityKey == id);
        return observation switch
        {
            CombatWireMemberStatus status => party.Members.Count(m => m.CharacterId == status.Value.CharacterId) == 1,
            CombatWireMemberVitals vitals => Entity(vitals.Value.EntityKey),
            CombatWireHit hit => Entity(hit.Value.SourceEntityId) || Entity(hit.Value.TargetEntityId),
            CombatWireHp hp => Entity(hp.Value.EntityId) || bosses.Contains(hp.Value.EntityId),
            CombatWireEffect effect => Entity(effect.Value.TargetEntityKey) || bosses.Contains(effect.Value.TargetEntityKey),
            // NPC metadata permits discovery only. It does not classify a boss or count its damage.
            CombatWireNpcAppearance => true,
            CombatWireNpcDeparture gone => bosses.Contains(gone.Value.EntityKey),
            CombatWireDeath death => Entity(death.Value.EntityKey) || bosses.Contains(death.Value.EntityKey),
            CombatWireMiddleBossReward or CombatWireDungeonState or CombatWireDungeonResult => true,
            CombatWirePartyDeparture departure => departure.Value.PartyKey == party.PartyKey,
            _ => false
        };
    }

    private void Raise<T>(Action<T>? handlers, T value)
    {
        foreach (var handler in handlers?.GetInvocationList().Cast<Action<T>>() ?? [])
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { handler(value); }
            catch { lock (_gate) _subscriberFailures++; }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _subscription.Dispose();
        _capture.SetDemand(NetworkCaptureConsumer.Combat, false);
        lock (_gate) { _scopes.Clear(); _minimumGenerations.Clear(); }
    }
}
