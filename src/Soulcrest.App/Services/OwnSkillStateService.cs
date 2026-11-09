using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>No native character IDs or names; ProfileId is the existing bound local profile GUID.</summary>
public sealed record OwnSkillStateSnapshot(string? ProfileId, string? ClassId,
    ImmutableDictionary<int, OwnSkillLevel> Skills, bool IsKnown, bool IsFresh,
    DateTimeOffset? ObservedAt, long Generation, long Sequence, string Source,
    long? CurrentCombatScore, DateTimeOffset? CombatScoreObservedAt)
{
    public static OwnSkillStateSnapshot Empty { get; } = new(null, null,
        ImmutableDictionary<int, OwnSkillLevel>.Empty, false, false, null, 0, 0, "unknown", null, null);
}

/// <summary>RAM-only projection of qualified own skill evidence onto the existing local profile.</summary>
public sealed class OwnSkillStateService : IDisposable
{
    private readonly INetworkSkillSource _source;
    private readonly Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> _resolve;
    private readonly object _gate = new();
    private bool _disposed;
    private NetworkSkillSnapshot? _publishedEvidence;
    private bool _publishedRunning;
    public event Action? Changed;
    public OwnSkillStateSnapshot Snapshot
    {
        get
        {
            lock (_gate) { if (_disposed) return OwnSkillStateSnapshot.Empty; }
            var evidence = _source.SkillSnapshot;
            var own = evidence.Character;
            var profile = own is null ? null : _resolve(own);
            // No adapter lock is held while calling either producer. Source and local profile
            // must still belong to the same atomic evidence instance after resolution.
            if (!_source.Running || own is null || profile?.ProfileId is not { Length: > 0 } id
                || !ReferenceEquals(evidence, _source.SkillSnapshot)) return OwnSkillStateSnapshot.Empty;
            lock (_gate) { if (_disposed) return OwnSkillStateSnapshot.Empty; }
            return new(id, own.Identity.ClassId, evidence.Skills, evidence.IsKnown, evidence.IsFresh,
                evidence.ObservedAt, own.Generation, evidence.Sequence, evidence.Source,
                own.CombatScore, own.ObservedAt);
        }
    }

    public OwnSkillStateService(INetworkSkillSource source, ExplorationService exploration)
        : this(source, exploration.TryGetBoundCharacter) { }

    internal OwnSkillStateService(INetworkSkillSource source,
        Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> resolve)
    {
        _source = source;
        _resolve = resolve;
        source.Changed += Refresh;
        source.OwnCharacterChanged += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        var evidence = _source.SkillSnapshot;
        var running = _source.Running;
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(_publishedEvidence, evidence) && _publishedRunning == running) return;
            _publishedEvidence = evidence;
            _publishedRunning = running;
        }
        SafeEvent.Raise(Changed, "Eigene Skillstufen");
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _source.Changed -= Refresh;
        _source.OwnCharacterChanged -= Refresh;
    }
}
