using System.Collections.Immutable;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

/// <summary>The existing passive own connection; this interface never starts a second capture.</summary>
public interface INetworkSkillSource
{
    bool Running { get; }
    NetworkSkillSnapshot SkillSnapshot { get; }
    event Action? Changed;
    event Action? OwnCharacterChanged;
}

/// <summary>
/// Immutable transient evidence. Skills are a full observed list only when IsKnown and IsFresh;
/// unknown/missing IDs and component blocks have no implicit level or bonus zero.
/// </summary>
public sealed record NetworkSkillSnapshot(OwnCharacterContext? Character,
    ImmutableDictionary<int, OwnSkillLevel> Skills, bool IsKnown, bool IsFresh,
    DateTimeOffset? ObservedAt, long Sequence, string Source)
{
    public bool FormatRejected { get; init; }
    public static NetworkSkillSnapshot Empty { get; } = new(null,
        ImmutableDictionary<int, OwnSkillLevel>.Empty, false, false, null, 0, "unknown");
}
