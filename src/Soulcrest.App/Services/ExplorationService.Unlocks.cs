namespace Soulcrest.App.Services;

/// <summary>
/// Sealed dungeons that open only after others (user information 2026-10-07, after aion2maps): on each
/// map three open one after the other, and the Ruins of the Ancient City of Ru need every other sealed
/// dungeon of their map. The progression mode skips a locked dungeon; the map popup says what is missing.
/// </summary>
public sealed partial class ExplorationService
{
    // Order of the chained dungeons per map (English names of the entrances in the map data).
    private static readonly Dictionary<string, string[]> Chains = new(StringComparer.Ordinal)
    {
        ["verteron"] = ["Distorted Cave Entrance", "Fissure Cave Entrance", "Rift Cave Entrance"], // Elyos
        ["altgard"] = ["Lost Ruin Entrance", "Twisted Pit Entrance", "Rift Fissure Entrance"],     // Asmodians
    };
    private const string AncientCity = "Ruins of the Ancient City of Ru";

    /// <summary>The sealed dungeons that must be done before <paramref name="place"/> opens (empty for all others).</summary>
    internal static IReadOnlyList<ExplorationPlace> Prerequisites(ExplorationPlace place, IReadOnlyList<ExplorationPlace> places)
    {
        if (place.Kind != "dungeon")
            return [];
        var dungeons = places.Where(p => p.Map == place.Map && p.Kind == "dungeon" && p.Id != place.Id);
        if (place.En == AncientCity)
            return [.. dungeons.Where(p => p.En != AncientCity)];
        if (!Chains.TryGetValue(place.Map, out var chain) || Array.IndexOf(chain, place.En) is not (var position and > 0))
            return [];
        var earlier = chain[..position].ToHashSet(StringComparer.Ordinal);
        return [.. dungeons.Where(p => earlier.Contains(p.En))];
    }

    /// <summary>What still blocks <paramref name="place"/> for the active character (empty when it is open).</summary>
    public IReadOnlyList<ExplorationPlace> MissingFor(ExplorationPlace place, IReadOnlySet<string> done) =>
        [.. Prerequisites(place, Places).Where(p => !done.Contains(p.Id))];
}
