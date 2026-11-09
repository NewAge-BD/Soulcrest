namespace Soulcrest.App.Components;

/// <summary>Pet-owned effects requested by the user request 2026-11-09. Each level is a complete vector, not an increment.</summary>
public static class CombatPetOwnedEffectCatalog
{
    public const int MaximumLevel = 3;

    private static readonly IReadOnlyList<CombatTitleDisplayEffect> Empty = Row();
    private static readonly IReadOnlyList<CombatTitleDisplayEffect>[] Cogni =
    [
        Empty,
        Row(Hp(10)),
        Row(Hp(20), Critical(1)),
        Row(Hp(30), Critical(2), new("STR", "Macht", "Might", 1m, "Points"))
    ];
    private static readonly IReadOnlyList<CombatTitleDisplayEffect>[] Fera =
    [
        Empty,
        Row(MountSpeed(1)),
        Row(MountSpeed(2), Accuracy(1)),
        Row(MountSpeed(3), Accuracy(2), new("DEX", "Agilität", "Dexterity", 1m, "Points"))
    ];
    private static readonly IReadOnlyList<CombatTitleDisplayEffect>[] Natura =
    [
        Empty,
        Row(Stamina(3)),
        Row(Stamina(6), CriticalResist(1)),
        Row(Stamina(9), CriticalResist(2), new("AGI", "Präzision", "Precision", 1m, "Points"))
    ];
    private static readonly IReadOnlyList<CombatTitleDisplayEffect>[] Varian =
    [
        Empty,
        Row(SprintReduction(0.1m)),
        Row(SprintReduction(0.2m), Evasion(1)),
        Row(SprintReduction(0.3m), Evasion(2), new("CON", "Vitalität", "Constitution", 1m, "Points"))
    ];

    /// <summary>Null means unsupported metadata. A known level-zero or Special pet has an explicitly empty effect list.</summary>
    public static IReadOnlyList<CombatTitleDisplayEffect>? Effects(string genus, int level)
    {
        if (level is < 0 or > MaximumLevel) return null;
        return genus switch
        {
            "cogni" => Cogni[level],
            "fera" => Fera[level],
            "natura" => Natura[level],
            "varian" => Varian[level],
            "special" => Empty,
            _ => null
        };
    }

    private static IReadOnlyList<CombatTitleDisplayEffect> Row(params CombatTitleDisplayEffect[] effects) =>
        Array.AsReadOnly(effects);

    private static CombatTitleDisplayEffect Hp(decimal value) => new("HPMax", "LP", "HP", value, "Points");
    private static CombatTitleDisplayEffect Critical(decimal value) => new("Critical", "Kritischer Treffer", "Critical Hit", value, "Points");
    private static CombatTitleDisplayEffect MountSpeed(decimal value) => new("GroundVehicleSpeed", "Boden-Tempo (Reittier)", "Mount Ground Move Speed", value, "Points");
    private static CombatTitleDisplayEffect Accuracy(decimal value) => new("Accuracy", "Zusatzpräzision", "Accuracy Bonus", value, "Points");
    private static CombatTitleDisplayEffect Stamina(decimal value) => new("SPMax", "SP", "Stamina", value, "Points");
    private static CombatTitleDisplayEffect CriticalResist(decimal value) => new("CriticalResist", "Krit.-Widerstand", "Critical Hit Resist", value, "Points");
    private static CombatTitleDisplayEffect SprintReduction(decimal value) => new("VehicleSprintCost", "SP-Kosten (Reittier): Sprint", "Mount Sprint Stamina Cost Reduction Rate", value, "Percent");
    private static CombatTitleDisplayEffect Evasion(decimal value) => new("Evasion", "Zusatzausweichen", "Evasion Bonus", value, "Points");
}
