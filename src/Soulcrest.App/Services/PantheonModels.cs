using System.Collections.Immutable;
using Soulcrest.Core.Pantheon;

namespace Soulcrest.App.Services;

public enum PantheonSourceKind { Unknown, Live, Saved }

public sealed record PantheonSlotView(int SlotId, PantheonDecorCategory Category, bool IsKnown, int? DecorId);

/// <summary>Presentation facts only. Saved observations never acquire live network authority.</summary>
public sealed record PantheonSnapshot
{
    public string? ProfileId { get; init; }
    public PantheonSourceKind SourceKind { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public bool IsOwnershipKnown { get; init; }
    public IReadOnlySet<int> OwnedIds { get; init; } = ImmutableHashSet<int>.Empty;
    public IReadOnlyList<PantheonSlotView> Slots { get; init; } = [];
    public IReadOnlyList<PantheonGodEffect> EquippedEffects { get; init; } = [];
    public bool AreEffectsKnown { get; init; }
    public byte? ActivePreset { get; init; }
    public IReadOnlyList<byte> KnownPresets { get; init; } = [];
    public static PantheonSnapshot Empty { get; } = new();
}
