using System.Collections.Immutable;

namespace Soulcrest.Core.Network;

/// <summary>A separately qualified own decoration observation; no preset selection is inferred.</summary>
public sealed record CharacterPantheonState(ImmutableDictionary<int, byte> Decorations);
