using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed record NetworkDailyMonsterKinaSnapshot(OwnCharacterContext? Character, long? Total,
    DateTimeOffset? At, long Sequence)
{
    public bool IsReady => Character is not null && Total is not null;
}

public sealed partial class NetworkLootService
{
    private OwnCharacterContext? _dailyKinaCharacter;
    private long? _dailyKinaTotal;
    private DateTimeOffset? _dailyKinaAt;
    private long _dailyKinaSequence, _dailyKinaMessageRevision;
    private readonly Queue<NetworkKinaGain> _dailyKinaGains = [];
    private bool _dispatchingDailyKina;

    /// <summary>Canonical monster income from consecutive daily totals, never added to wallet income.</summary>
    public event Action<NetworkKinaGain>? MonsterKinaGained;

    public NetworkDailyMonsterKinaSnapshot DailyMonsterKinaSnapshot
    {
        get
        {
            lock (_gate)
                return Running && _dailyKinaCharacter is not null && ReferenceEquals(_dailyKinaCharacter, _ownCharacter)
                    ? new(_dailyKinaCharacter, _dailyKinaTotal, _dailyKinaAt, _dailyKinaSequence)
                    : new(null, null, null, 0);
        }
    }

    private void ProcessDailyKinaMessage(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        if (!Running) return;
        _dailyKinaMessageRevision++;
        var observation = opcode == 0xFFA5 ? DailyMonsterKinaMessage.TryParseInitial(message)
            : DailyMonsterKinaMessage.TryParseUpdate(message);
        if (_ownCharacter is null)
        {
            if (_entryCandidate is not { } candidate || candidate.Stream != stream) return;
            if (opcode != 0xFFA5 || observation is null || candidate.Prepared || candidate.DailyKinaSeen)
            {
                candidate.DailyKinaRejected = true;
                candidate.DailyKina = null;
                return;
            }
            candidate.DailyKinaSeen = true;
            candidate.DailyKina = observation;
            candidate.DailyKinaAt = at;
            return;
        }
        if (_identityStream != stream) return;
        if (observation is null || opcode != 0xFFA6)
        {
            ResetDailyKinaContext();
            SafeEvent.Raise(Changed, "Monster-Kina-Tagesstand");
            return;
        }
        var character = _ownCharacter;
        var previous = ReferenceEquals(_dailyKinaCharacter, character) ? _dailyKinaTotal : null;
        _dailyKinaCharacter = character;
        _dailyKinaTotal = observation.Total;
        _dailyKinaAt = at;
        _dailyKinaSequence++;
        // An absolute update can initialize the display after a load. Without a continuous
        // previous observation it cannot establish income. A lower total is authoritative,
        // but no local-day or reset-time meaning is invented from the opaque message tail.
        if (previous is { } before && observation.Total > before)
        {
            if (_dailyKinaGains.Count >= MaximumPendingKinaGains) ResetDailyKinaContext();
            else
            {
                var world = WorldContext.Snapshot;
                _dailyKinaGains.Enqueue(new(at, _dailyKinaSequence, character, observation.Total - before)
                {
                    InInstance = world.Status == WorldContextStatus.Confirmed && world.Kind == WorldContextKind.Instance
                        && world.BindingGeneration == character.Generation
                });
            }
        }
        DispatchDailyKinaGains();
        SafeEvent.Raise(Changed, "Monster-Kina-Tagesstand");
    }

    private void BindDailyKinaBaseline(EntryCandidate? candidate)
    {
        if (candidate is not { DailyKinaRejected: false, DailyKina: { } value } || _ownCharacter is null) return;
        _dailyKinaCharacter = _ownCharacter;
        _dailyKinaTotal = value.Total;
        _dailyKinaAt = candidate.DailyKinaAt;
        _dailyKinaSequence++;
    }

    private void DispatchDailyKinaGains()
    {
        if (_dispatchingDailyKina) return;
        _dispatchingDailyKina = true;
        try
        {
            while (_dailyKinaGains.TryDequeue(out var gain))
                foreach (var subscriber in MonsterKinaGained?.GetInvocationList().Cast<Action<NetworkKinaGain>>() ?? [])
                {
                    if (!Running || !ReferenceEquals(gain.Character, _ownCharacter)
                        || !ReferenceEquals(_dailyKinaCharacter, _ownCharacter)) break;
                    try { subscriber(gain); }
                    catch (Exception error) { LogFile.Error("Monster-Kina-Gewinn", error); }
                }
        }
        finally { _dispatchingDailyKina = false; }
    }

    private void ResetDailyKinaContext()
    {
        _dailyKinaCharacter = null;
        _dailyKinaTotal = null;
        _dailyKinaAt = null;
        _dailyKinaGains.Clear();
    }
}
