using System.Globalization;
using Soulcrest.Core.Combat;

namespace Soulcrest.App.Services;

/// <summary>Only reviewed roles become target rules. Names, data roles, end proofs and amount evidence remain separate.</summary>
public static class GlobalEuCombatBossCatalog
{
    public static CombatBossCatalog Load() => FromFacts(CombatEncounterCatalog.LoadEmbedded());

    public static CombatBossCatalog FromFacts(CombatEncounterCatalog facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!StringComparer.Ordinal.Equals(facts.Region, "global-eu"))
            throw new ArgumentException("An explicitly matching Global/EU catalog is required.", nameof(facts));
        return new(facts.Dungeons.SelectMany(dungeon => dungeon.Npcs
            .Where(npc => npc.Role != CombatCatalogNpcRole.Unknown)
            .Select(npc => new CombatBossRule(dungeon.DungeonDataId, dungeon.MapDataId, npc.NpcDataId,
                new CombatEncounterDefinition("npc:" + npc.NpcDataId.ToString(CultureInfo.InvariantCulture), null,
                    CombatAttemptKind.Boss, CombatCapabilities.None,
                    new("boss-damage-v1", DecoderVersion: "global-eu-wire-v2", CatalogVersion: facts.CatalogVersion)),
                npc.EndProof switch
                {
                    CombatCatalogEndProof.MiddleBossReward => CombatBossVictoryProof.MiddleBossReward,
                    CombatCatalogEndProof.DungeonSuccess => CombatBossVictoryProof.DungeonSuccess,
                    _ => CombatBossVictoryProof.None
                }, facts.CatalogVersion + ":npc-" + npc.NpcDataId.ToString(CultureInfo.InvariantCulture)))));
    }
}
