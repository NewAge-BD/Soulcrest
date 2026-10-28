using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

/// <summary>
/// RAM-only PC evidence and death tombstones. Appearance is a type/identity observation,
/// never proof of a new life. No eviction or timeout can make a dead/reused actor eligible again.
/// Live PC entries are bounded; a tombstone keeps only one bit per entity key, so NPC churn and
/// context changes neither grow full entries nor disable kill evidence (review 2026-10-28).
/// </summary>
internal sealed class AbyssPlayerRegistry
{
    /// <summary>Simultaneously live, proven PCs. Tombstones are not limited by this budget.</summary>
    internal const int MaximumEntries = 4096;

    internal sealed record Context(OwnCharacterContext Own, string Stream, GameMessageStream Transport,
        long CaptureGeneration, long Revision, WorldContextSnapshot World)
    {
        public override string ToString() => nameof(Context);
    }

    private sealed class Entry(ushort server, string name, DateTimeOffset observedAt)
    {
        internal ushort Server { get; } = server;
        internal string Name { get; } = name;
        internal DateTimeOffset ObservedAt { get; } = observedAt;
        internal DateTimeOffset MetadataObservedAt { get; set; } = observedAt;
        internal string? ClassId { get; set; }
        internal int? AbyssRankGrade { get; set; }
    }

    private readonly Dictionary<int, Entry> _entries = [];
    private readonly TombstoneSet _tombstones = new();
    private long? _captureGeneration;
    private Context? _context;
    internal int Count => _entries.Count + _tombstones.Count;
    internal bool Overflowed { get; private set; }

    internal bool ObserveCapture(long generation)
    {
        if (generation < 0 || _captureGeneration is { } current && generation < current) return false;
        if (_captureGeneration != generation)
        {
            _entries.Clear();
            _tombstones.Clear();
            _context = null;
            Overflowed = false;
            _captureGeneration = generation;
        }
        return true;
    }

    internal void Invalidate()
    {
        foreach (var entity in _entries.Keys) _tombstones.Add(entity);
        _entries.Clear();
        _context = null;
    }

    internal bool RevokeActor(Context context, int entity, DateTimeOffset at, DateTimeOffset now)
    {
        if (!Bind(context, at, now) || entity <= 0) { Invalidate(); return false; }
        Kill(entity);
        return true;
    }

    internal bool ObserveAppearance(Context context, AbyssPlayerAppearanceObservation player,
        DateTimeOffset at, DateTimeOffset now)
    {
        if (!Bind(context, at, now) || !ValidPlayer(player.EntityKey, player.ServerId, player.Name)
            || player.EntityKey == context.Own.Identity.EntityKey
            || SameOwnIdentity(context.Own, player.ServerId, player.Name)) return false;
        if (_tombstones.Contains(player.EntityKey)) return false;
        if (_entries.TryGetValue(player.EntityKey, out var previous))
        {
            if (previous.Server != player.ServerId || previous.Name != player.Name) { Kill(player.EntityKey); return false; }
            if (at > previous.MetadataObservedAt) UpdateMetadata(previous, player, at);
            return true;
        }
        var entry = new Entry(player.ServerId, player.Name, at);
        UpdateMetadata(entry, player, at);
        return Add(player.EntityKey, entry);
    }

    /// <summary>
    /// A complete CharDie consumes the victim even for a different caster or an unclassified victim.
    /// Only a previously proven PC and an explicit current own caster produce a kill candidate.
    /// Calling this pure join does not enable any wire capability or emit a source event.
    /// </summary>
    internal bool TryConsumeOwnKill(Context context, AbyssPlayerCasterDeathObservation death,
        DateTimeOffset at, DateTimeOffset now, out string? opponentName)
        => TryConsumeOwnKillWithMetadata(context, death, at, now, out opponentName, out _, out _);

    internal bool TryConsumeOwnKillWithMetadata(Context context, AbyssPlayerCasterDeathObservation death,
        DateTimeOffset at, DateTimeOffset now, out string? opponentName,
        out string? opponentClassId, out int? opponentAbyssRankGrade)
    {
        opponentName = null;
        opponentClassId = null;
        opponentAbyssRankGrade = null;
        if (!Bind(context, at, now) || !ValidPlayer(death.CasterEntityKey, death.CasterServerId, death.CasterName)
            || death.VictimEntityKey <= 0 || death.VictimEntityKey == death.CasterEntityKey) return false;
        if (!_entries.TryGetValue(death.VictimEntityKey, out var victim))
        {
            Kill(death.VictimEntityKey);
            return false;
        }
        Kill(death.VictimEntityKey); // consume before publication, including foreign kills
        if (at < victim.ObservedAt || death.VictimEntityKey == context.Own.Identity.EntityKey
            || death.CasterEntityKey != context.Own.Identity.EntityKey
            || !SameOwnIdentity(context.Own, death.CasterServerId, death.CasterName)) return false;
        opponentName = victim.Name;
        // A later-arriving older death can still use the original PC identity, but must not
        // borrow class/rank metadata observed after its own event time. No history is inferred.
        if (victim.MetadataObservedAt <= at)
        {
            opponentClassId = victim.ClassId;
            opponentAbyssRankGrade = victim.AbyssRankGrade;
        }
        return true;
    }

    private static void UpdateMetadata(Entry entry, AbyssPlayerAppearanceObservation player, DateTimeOffset at)
    {
        entry.MetadataObservedAt = at;
        entry.ClassId = player.ClassId is "Gladiator" or "Templar" or "Ranger" or "Assassin"
            or "Elementalist" or "Sorcerer" or "Cleric" or "Chanter" ? player.ClassId : null;
        entry.AbyssRankGrade = player.AbyssRankGrade is >= 1 and <= 19 ? player.AbyssRankGrade : null;
    }

    /// <summary>
    /// Read-only metadata join for an independently qualified own death. This does not bind a
    /// context, add an actor, consume a life, or repair a tombstone. The explicit PC caster must
    /// also match a complete current appearance by entity, server and name.
    /// </summary>
    internal bool TryGetOwnDeathCasterMetadata(Context context, AbyssPlayerCasterDeathObservation death,
        DateTimeOffset at, DateTimeOffset now, out string? classId, out int? abyssRankGrade)
    {
        classId = null;
        abyssRankGrade = null;
        if (Overflowed || _captureGeneration != context.CaptureGeneration || _context is not { } current
            || !ReferenceEquals(current.Own, context.Own) || current.Stream != context.Stream
            || !ReferenceEquals(current.Transport, context.Transport) || current.Revision != context.Revision
            || !ReferenceEquals(current.World, context.World)
            || context.Own.ObservedAt is not { } ownAt || ownAt == default
            || context.World.ObservedAt is not { } worldAt || worldAt == default
            || at == default || at < ownAt || at < worldAt || at > now
            || death.VictimEntityKey != context.Own.Identity.EntityKey
            || death.CasterEntityKey == death.VictimEntityKey
            || !ValidPlayer(death.CasterEntityKey, death.CasterServerId, death.CasterName)
            || !_entries.TryGetValue(death.CasterEntityKey, out var caster)
            || caster.Server != death.CasterServerId || caster.Name != death.CasterName
            || caster.ObservedAt > at || caster.MetadataObservedAt > at) return false;
        classId = caster.ClassId;
        abyssRankGrade = caster.AbyssRankGrade;
        return true;
    }

    private bool Bind(Context context, DateTimeOffset at, DateTimeOffset now)
    {
        var own = context.Own;
        if (context.Transport is null || string.IsNullOrEmpty(context.Stream) || context.Revision < 0
            || !ObserveCapture(context.CaptureGeneration) || Overflowed
            || own.Generation <= 0 || own.Identity.CharacterId <= 0 || own.Identity.ServerId <= 0
            || own.Identity.EntityKey <= 0 || own.ObservedAt is not { } ownAt || ownAt == default
            || context.World is not { Status: WorldContextStatus.Confirmed, Kind: WorldContextKind.Instance,
                Category: "Abyss", ObservedAt: { } worldAt }
            || context.World.BindingGeneration != own.Generation || worldAt == default
            || at == default || at < ownAt || at < worldAt || at > now) return false;
        if (_context is not { } previous || !ReferenceEquals(previous.Own, own)
            || previous.Stream != context.Stream || !ReferenceEquals(previous.Transport, context.Transport)
            || previous.CaptureGeneration != context.CaptureGeneration || previous.Revision != context.Revision
            || !ReferenceEquals(previous.World, context.World))
        {
            Invalidate();
            _context = context;
        }
        return true;
    }

    private bool Add(int entity, Entry entry)
    {
        if (_entries.Count >= MaximumEntries) { Overflowed = true; return false; }
        _entries.Add(entity, entry);
        return true;
    }

    // The full PC entry is released; only the permanent "never eligible again" bit remains.
    private void Kill(int entity)
    {
        _entries.Remove(entity);
        _tombstones.Add(entity);
    }

    /// <summary>Paged bitset over positive entity keys: one bit per tombstone, nothing is ever forgotten.</summary>
    private sealed class TombstoneSet
    {
        private const int PageBits = 12; // 4096 keys per 512-byte page
        private readonly Dictionary<int, ulong[]> _pages = [];
        internal int Count { get; private set; }

        internal bool Contains(int entity) => entity > 0 && _pages.TryGetValue(entity >> PageBits, out var page)
            && (page[(entity & ((1 << PageBits) - 1)) >> 6] & (1UL << (entity & 63))) != 0;

        internal void Add(int entity)
        {
            if (entity <= 0) return;
            if (!_pages.TryGetValue(entity >> PageBits, out var page))
                _pages.Add(entity >> PageBits, page = new ulong[(1 << PageBits) / 64]);
            ref var word = ref page[(entity & ((1 << PageBits) - 1)) >> 6];
            var bit = 1UL << (entity & 63);
            if ((word & bit) != 0) return;
            word |= bit;
            Count++;
        }

        internal void Clear() { _pages.Clear(); Count = 0; }
    }

    private static bool SameOwnIdentity(OwnCharacterContext own, ushort server, string name) =>
        server == own.Identity.ServerId && string.Equals(name, own.Identity.Name, StringComparison.Ordinal);

    private static bool ValidPlayer(int entity, ushort server, string name) =>
        entity > 0 && server > 0 && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);
}
