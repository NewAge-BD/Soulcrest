using Soulcrest.Core.Combat;

namespace Soulcrest.App.Components;

public enum CombatStatOverviewGroup
{
    PrimaryAttributes, GodAttributes, MainStats,
    Combat, HitDetection, StatusEffects, PvP, PvE, Genus, Elements, Special, Resources
}

/// <summary>Display identity only. A nullable output binding never establishes a native stat mapping.</summary>
public sealed record CombatStatOverviewRowDefinition(string Id, string LabelDe, string LabelEn,
    CombatStatUnit Unit, string? OutputId)
{
    public CharacterDisplayStatCategory Category { get; init; }
    public string? IconKey { get; init; }
    /// <summary>Unavailable rows retain their metadata but do not enter the active display roster.</summary>
    public bool IsVisible { get; init; } = true;
}

public sealed record CombatStatOverviewGroupDefinition(CombatStatOverviewGroup Group,
    string LabelDe, string LabelEn, IReadOnlyList<CombatStatOverviewRowDefinition> Rows)
{
    public string Key => Group switch
    {
        CombatStatOverviewGroup.PrimaryAttributes => "primary-attributes",
        CombatStatOverviewGroup.GodAttributes => "god-attributes",
        CombatStatOverviewGroup.MainStats => "main-stats",
        CombatStatOverviewGroup.Combat => "combat",
        CombatStatOverviewGroup.HitDetection => "hit-detection",
        CombatStatOverviewGroup.StatusEffects => "status-effects",
        CombatStatOverviewGroup.PvP => "pvp",
        CombatStatOverviewGroup.PvE => "pve",
        CombatStatOverviewGroup.Genus => "genus",
        CombatStatOverviewGroup.Elements => "elements",
        CombatStatOverviewGroup.Special => "special",
        CombatStatOverviewGroup.Resources => "resources",
        _ => throw new ArgumentOutOfRangeException(nameof(Group))
    };

    public CharacterDisplayStatCategory Category => Group is CombatStatOverviewGroup.PrimaryAttributes
        or CombatStatOverviewGroup.GodAttributes or CombatStatOverviewGroup.MainStats
        ? CharacterDisplayStatCategory.Main : CharacterDisplayStatCategory.Sub;
}

/// <summary>
/// Complete display roster from the named metadata snapshot. It does not read raw packets,
/// grant build/class/effect applicability, or infer zero from an absent projection.
/// </summary>
public static class CombatStatOverviewCatalog
{
    public const int MetadataSnapshotBuild = 26425393;

    public static IReadOnlyList<CombatStatOverviewGroupDefinition> MainGroups { get; } = Array.AsReadOnly(new[]
    {
        G(CombatStatOverviewGroup.PrimaryAttributes, "Grundattribute", "Primary Attributes",
            R("Might", "Macht", "Might", CombatStatUnit.Points, "Might"),
            R("Dexterity", "Agilität", "Dexterity", CombatStatUnit.Points, "Dexterity"),
            R("Precision", "Präzision", "Precision", CombatStatUnit.Points, "Precision"),
            R("Willpower", "Wille", "Willpower", CombatStatUnit.Points, "Willpower"),
            R("Intelligence", "Intelligenz", "Intelligence", CombatStatUnit.Points, "Intelligence"),
            R("Constitution", "Vitalität", "Constitution", CombatStatUnit.Points, "Constitution")),
        G(CombatStatOverviewGroup.GodAttributes, "Gottheitsattribute", "God Attributes",
            R("Justice", "Gerechtigkeit", "Justice", CombatStatUnit.Points, "Justice"),
            R("Freedom", "Freiheit", "Freedom", CombatStatUnit.Points, "Freedom"),
            R("Illusion", "Illusion", "Illusion", CombatStatUnit.Points, "Illusion"),
            R("Life", "Leben", "Life", CombatStatUnit.Points, "Life"),
            R("Time", "Zeit", "Time", CombatStatUnit.Points, "Time"),
            R("Light", "Licht", "Light", CombatStatUnit.Points, null) with { IsVisible = false },
            R("Destruction", "Zerstörung", "Destruction", CombatStatUnit.Points, "Destruction"),
            R("Death", "Tod", "Death", CombatStatUnit.Points, "Death"),
            R("Wisdom", "Weisheit", "Wisdom", CombatStatUnit.Points, "Wisdom"),
            R("Destiny", "Schicksal", "Destiny", CombatStatUnit.Points, "Destiny"),
            R("Space", "Raum", "Space", CombatStatUnit.Points, "Space"),
            R("Dark", "Dunkel", "Darkness", CombatStatUnit.Points, null) with { IsVisible = false }),
        G(CombatStatOverviewGroup.MainStats, "Hauptwerte", "Main Stats",
            R("Attack", "Angriffskraft", "Attack", CombatStatUnit.Points, "Attack"),
            R("Accuracy", "Präzision", "Accuracy", CombatStatUnit.Points, "Accuracy"),
            R("Critical", "Kritischer Treffer", "Critical Hit", CombatStatUnit.Points, "Critical"),
            R("HPMax", "LP", "HP", CombatStatUnit.Points, "HPMax"),
            R("CombatSpeed", "Kampftempo", "Combat Speed", CombatStatUnit.Percent, "CombatSpeed"),
            R("Defense", "Verteidigung", "Defense", CombatStatUnit.Points, "Defense"),
            R("Evasion", "Ausweichen", "Evasion", CombatStatUnit.Points, "Evasion"),
            R("CriticalResist", "Krit.-Widerstand", "Critical Hit Resist", CombatStatUnit.Points, "CriticalResist"),
            R("MPMax", "MP", "MP", CombatStatUnit.Points, "MPMax"),
            R("MoveSpeed", "Bewegungstempo", "Move Speed", CombatStatUnit.Percent, "MoveSpeed")),
    });

    public static IReadOnlyList<CombatStatOverviewGroupDefinition> SubGroups { get; } = Array.AsReadOnly(new[]
    {
        G(CombatStatOverviewGroup.Combat, "Kampf", "Combat",
            R("DefensePierce", "Penetration", "Penetration", CombatStatUnit.Points, "DefensePierce"),
            R("SealStoneAddDamage", "Zusatzschaden (Siegelstein)", "Power Shard Damage Bonus", CombatStatUnit.Points, "SealStoneAddDamage"),
            R("CriticalAddDamage", "Krit.-Angriffskraft", "Critical Attack", CombatStatUnit.Points, "CriticalAddDamage"),
            R("CriticalDamageDefense", "Krit.-Verteidigung", "Critical Damage Defense", CombatStatUnit.Points, "CriticalDamageDefense"),
            R("BackAttackDamage", "Angriffskraft (Rückseitig)", "Back Attack", CombatStatUnit.Points, "BackAttackDamage"),
            R("BackAttackDefense", "Verteidigung (Rückseitig)", "Back Defense", CombatStatUnit.Points, "BackAttackDefense"),
            R("FrontAttackDamage", "Angriffskraft (Frontal)", "Front Attack", CombatStatUnit.Points, null),
            R("FrontAttackDefense", "Verteidigung (Frontal)", "Front Defense", CombatStatUnit.Points, null),
            R("AmplifyAllDamage", "Schadensverstärkung", "Damage Boost", CombatStatUnit.Percent, "AmplifyAllDamage"),
            R("DecreaseDamage", "Schadensresistenz", "Damage Tolerance", CombatStatUnit.Percent, "DecreaseDamage"),
            R("AmplifyWeaponDamage", "Verstärkung (Waffenschaden)", "Weapon Damage Boost", CombatStatUnit.Percent, "AmplifyWeaponDamage"),
            R("DecreaseWeaponDamage", "Schadensresistenz (Waffenschaden)", "Weapon Damage Tolerance", CombatStatUnit.Percent, null),
            R("AmplifyCriticalDamage", "Krit.-Schadensverstärkung", "Critical Damage Boost", CombatStatUnit.Percent, "AmplifyCriticalDamage"),
            R("DecreaseCriticalDamage", "Krit.-Schadensresistenz", "Critical Damage Tolerance", CombatStatUnit.Percent, "DecreaseCriticalDamage"),
            R("AmplifyBackAttack", "Schadensverstärkung (Rückseitig)", "Back Attack Damage Boost", CombatStatUnit.Percent, "AmplifyBackAttack"),
            R("DecreaseBackAttack", "Schadensresistenz (Rückseitig)", "Back Attack Damage Tolerance", CombatStatUnit.Percent, "DecreaseBackAttack"),
            R("AmplifyFrontAttack", "Schadensverstärkung (Frontal)", "Front Attack Damage Boost", CombatStatUnit.Percent, null),
            R("DecreaseFrontAttack", "Schadensresistenz (Frontal)", "Front Attack Damage Tolerance", CombatStatUnit.Percent, null)),
        G(CombatStatOverviewGroup.HitDetection, "Treffererkennung", "Hit Detection",
            R("AdditionalHitRate", "Mehrfachtreffer", "Multi-hit Chance", CombatStatUnit.Percent, "AdditionalHitRate"),
            R("AdditionalHitResistRate", "Widerstand (Mehrfachtreffer)", "Multi-hit Resist", CombatStatUnit.Percent, "AdditionalHitResistRate"),
            R("BackAttackCritical", "Krit.-Treffer (Rückseitig)", "Back Attack Critical Hit", CombatStatUnit.Points, "BackAttackCritical"),
            R("BackAttackCriticalResist", "Krit.-Widerstand (Rückseitig)", "Back Attack Critical Hit Resist", CombatStatUnit.Points, "BackAttackCriticalResist"),
            R("FrontAttackCritical", "Kritischer Treffer (Frontal)", "Front Attack Critical Hit", CombatStatUnit.Points, null),
            R("FrontAttackCriticalResist", "Widerstand (Kritischer Treffer Frontal)", "Front Attack Critical Hit Resist", CombatStatUnit.Points, null),
            R("BlockPierce", "Penetration (Block)", "Block Penetration", CombatStatUnit.Points, "BlockPierce"),
            R("Block", "Block", "Block", CombatStatUnit.Points, "Block"),
            R("IgnoreIronWall", "Eisenwall-Penetration", "Endurance Penetration", CombatStatUnit.Percent, "IgnoreIronWall"),
            R("IronWall", "Eisenwall", "Endurance", CombatStatUnit.Percent, "IronWall"),
            R("IgnoreRestoration", "Regenerations-Penetration", "Regeneration Penetration", CombatStatUnit.Percent, "IgnoreRestoration"),
            R("Restoration", "Regeneration", "Regeneration", CombatStatUnit.Percent, "Restoration"),
            R("Perfect", "Perfektion", "Perfect Chance", CombatStatUnit.Percent, "Perfect"),
            R("PerfectResist", "Perfekter Widerstand", "Perfect Resist", CombatStatUnit.Percent, "PerfectResist"),
            R("HardHit", "Wucht", "Double Chance", CombatStatUnit.Percent, "HardHit"),
            R("HardHitResist", "Wucht-Widerstand", "Double Resist", CombatStatUnit.Percent, "HardHitResist")),
        G(CombatStatOverviewGroup.StatusEffects, "Zustandseffekte", "Status Effects",
            R("StunAccuracy", "Treffer (Betäubung)", "Stun Chance", CombatStatUnit.Percent, null),
            R("StunResist", "Widerstand (Betäubung)", "Stun Resist", CombatStatUnit.Percent, null),
            R("KnockdownInstantAccuracy", "Treffer (K. o.)", "Knockdown Chance", CombatStatUnit.Percent, null),
            R("KnockdownInstantResist", "Widerstand (K. o.)", "Knockdown Resist", CombatStatUnit.Percent, null),
            R("AerialAccuracy", "Treffer (Luftbann)", "Airborne Chance", CombatStatUnit.Percent, null),
            R("AerialResist", "Widerstand (Luftbann)", "Airborne Resist", CombatStatUnit.Percent, null),
            R("BindAccuracy", "Treffer (Greifen)", "Grab Chance", CombatStatUnit.Percent, null),
            R("BindResist", "Widerstand (Greifen)", "Grab Resist", CombatStatUnit.Percent, null),
            R("FearAccuracy", "Treffer (Furcht)", "Fear Chance", CombatStatUnit.Percent, null),
            R("FearResist", "Widerstand (Furcht)", "Fear Resist", CombatStatUnit.Percent, null),
            R("SleepAccuracy", "Treffer (Schlaf)", "Sleep Chance", CombatStatUnit.Percent, null),
            R("SleepResist", "Widerstand (Schlaf)", "Sleep Resist", CombatStatUnit.Percent, null),
            R("PolymorphAccuracy", "Treffer (Mutation)", "Polymorph Chance", CombatStatUnit.Percent, null),
            R("PolymorphResist", "Widerstand (Mutation)", "Polymorph Resist", CombatStatUnit.Percent, null),
            R("TauntAccuracy", "Treffer (Provokation)", "Taunt Chance", CombatStatUnit.Percent, null),
            R("TauntResist", "Widerstand (Provokation)", "Taunt Resist", CombatStatUnit.Percent, null),
            R("BlockActiveAccuracy", "Treffer (Siegel)", "Seal Chance", CombatStatUnit.Percent, null),
            R("BlockActiveResist", "Widerstand (Siegel)", "Seal Resist", CombatStatUnit.Percent, null),
            R("FrozenAccuracy", "Treffer (Einfrieren)", "Frost Chance", CombatStatUnit.Percent, null),
            R("FrozenResist", "Widerstand (Einfrieren)", "Frost Resist", CombatStatUnit.Percent, null),
            R("ParalysisAccuracy", "Treffer (Lähmung)", "Paralyze Chance", CombatStatUnit.Percent, null),
            R("ParalysisResist", "Widerstand (Lähmung)", "Paralyze Resist", CombatStatUnit.Percent, null),
            R("HoldAccuracy", "Treffer (Bann)", "Root Chance", CombatStatUnit.Percent, null),
            R("HoldResist", "Widerstand (Bann)", "Root Resist", CombatStatUnit.Percent, null),
            R("BlindAccuracy", "Treffer (Blindheit)", "Blind Chance", CombatStatUnit.Percent, null),
            R("BlindResist", "Widerstand (Blindheit)", "Blind Resist", CombatStatUnit.Percent, null),
            R("SlowAccuracy", "Treffer (Lethargie)", "Lethargy Chance", CombatStatUnit.Percent, null),
            R("SlowResist", "Widerstand (Lethargie)", "Lethargy Resist", CombatStatUnit.Percent, null),
            R("SnareAccuracy", "Treffer (Trägheit)", "Slow Chance", CombatStatUnit.Percent, null),
            R("SnareResist", "Widerstand (Trägheit)", "Slow Resist", CombatStatUnit.Percent, null),
            R("PoisonAccuracy", "Treffer (Vergiftung)", "Poison Chance", CombatStatUnit.Percent, null),
            R("PoisonResist", "Widerstand (Vergiftung)", "Poison Resist", CombatStatUnit.Percent, null),
            R("BleedAccuracy", "Treffer (Blutung)", "Bleed Chance", CombatStatUnit.Percent, null),
            R("BleedResist", "Widerstand (Blutung)", "Bleed Resist", CombatStatUnit.Percent, null)),
        G(CombatStatOverviewGroup.PvP, "PvP", "PvP",
            R("PvPAddDamage", "Angriffskraft (PvP)", "PvP Attack", CombatStatUnit.Points, "PvPAddDamage"),
            R("PvPDamageDefense", "Verteidigung (PvP)", "PvP Defense", CombatStatUnit.Points, "PvPDamageDefense"),
            R("PvPAmplifyDamage", "Schadensverstärkung (PvP)", "PvP Damage Boost", CombatStatUnit.Percent, "PvPAmplifyDamage"),
            R("PvPDecreaseDamage", "Schadensresistenz (PvP)", "PvP Damage Tolerance", CombatStatUnit.Percent, "PvPDecreaseDamage"),
            R("PvPAccuracy", "Präzision (PvP)", "PvP Accuracy", CombatStatUnit.Points, "PvPAccuracy"),
            R("PvPEvasion", "Ausweichen (PvP)", "PvP Evasion", CombatStatUnit.Points, "PvPEvasion"),
            R("PvPCritical", "Krit.-Treffer (PvP)", "PvP Critical Hit", CombatStatUnit.Points, "PvPCritical"),
            R("PvPCriticalResist", "Krit.-Widerstand (PvP)", "PvP Critical Hit Resist", CombatStatUnit.Points, "PvPCriticalResist"),
            R("PvPBlockPierce", "Block-Penetration (PvP)", "PvP Block Penetration", CombatStatUnit.Points, null),
            R("PvPBlock", "Block (PvP)", "PvP Block", CombatStatUnit.Points, null)),
        G(CombatStatOverviewGroup.PvE, "PvE", "PvE",
            R("PvEAddDamage", "Angriffskraft (PvE)", "PvE Attack", CombatStatUnit.Points, "PvEAddDamage"),
            R("PvEDamageDefense", "Verteidigung (PvE)", "PvE Defense", CombatStatUnit.Points, "PvEDamageDefense"),
            R("PvEAccuracy", "Präzision (PvE)", "PvE Accuracy", CombatStatUnit.Points, "PvEAccuracy"),
            R("PvEEvasion", "Ausweichen (PvE)", "PvE Evasion", CombatStatUnit.Points, "PvEEvasion"),
            R("PvEAmplifyDamage", "Schadensverstärkung (PvE)", "PvE Damage Boost", CombatStatUnit.Percent, "PvEAmplifyDamage"),
            R("PvEDecreaseDamage", "Schadensresistenz (PvE)", "PvE Damage Tolerance", CombatStatUnit.Percent, "PvEDecreaseDamage"),
            R("BossNpcAddDamage", "Angriffskraft (Boss)", "Boss Attack", CombatStatUnit.Points, "BossNpcAddDamage"),
            R("BossNpcDefense", "Verteidigung (Boss)", "Boss Defense", CombatStatUnit.Points, "BossNpcDefense"),
            R("BossNpcAmplifyDamage", "Schadensverstärkung (Boss)", "Boss Damage Boost", CombatStatUnit.Percent, "BossNpcAmplifyDamage"),
            R("BossNpcDecreaseDamage", "Schadensresistenz (Boss)", "Boss Damage Tolerance", CombatStatUnit.Percent, "BossNpcDecreaseDamage")),
        G(CombatStatOverviewGroup.Genus, "Gattungen", "Genus",
            R("IntellectDamage", "Angriffskraft (Cognia)", "Cogni Attack", CombatStatUnit.Points, null),
            R("IntellectDefense", "Verteidigung (Cognia)", "Cogni Defense", CombatStatUnit.Points, "IntellectDefense"),
            R("IntellectAccuracy", "Präzision (Cognia)", "Cogni Accuracy", CombatStatUnit.Points, "IntellectAccuracy"),
            R("IntellectEvasion", "Ausweichen (Cognia)", "Cogni Evasion", CombatStatUnit.Points, "IntellectEvasion"),
            R("IntellectCritical", "Krit.-Treffer (Cognia)", "Cogni Critical Hit", CombatStatUnit.Points, null),
            R("IntellectCriticalResist", "Krit.-Widerstand (Cognia)", "Cogni Critical Hit Resist", CombatStatUnit.Points, null),
            R("IntellectBlockPierce", "Block-Penetration (Cognia)", "Cogni Block Penetration", CombatStatUnit.Points, null),
            R("IntellectBlock", "Block (Cognia)", "Cogni Block", CombatStatUnit.Points, null),
            R("IntellectAmplifyDamage", "Schadensverstärkung (Cognia)", "Cogni Damage Boost", CombatStatUnit.Percent, null),
            R("IntellectDecreaseDamage", "Schadensresistenz (Cognia)", "Cogni Damage Tolerance", CombatStatUnit.Percent, null),
            R("FeralDamage", "Angriffskraft (Fera)", "Fera Attack", CombatStatUnit.Points, null),
            R("FeralDefense", "Verteidigung (Fera)", "Fera Defense", CombatStatUnit.Points, "FeralDefense"),
            R("FeralAccuracy", "Präzision (Fera)", "Fera Accuracy", CombatStatUnit.Points, null),
            R("FeralEvasion", "Ausweichen (Fera)", "Fera Evasion", CombatStatUnit.Points, "FeralEvasion"),
            R("FeralCritical", "Krit.-Treffer (Fera)", "Fera Critical Hit", CombatStatUnit.Points, null),
            R("FeralCriticalResist", "Krit.-Widerstand (Fera)", "Fera Critical Hit Resist", CombatStatUnit.Points, "FeralCriticalResist"),
            R("FeralBlockPierce", "Block-Penetration (Fera)", "Fera Block Penetration", CombatStatUnit.Points, null),
            R("FeralBlock", "Block (Fera)", "Fera Block", CombatStatUnit.Points, null),
            R("FeralAmplifyDamage", "Schadensverstärkung (Fera)", "Fera Damage Boost", CombatStatUnit.Percent, null),
            R("FeralDecreaseDamage", "Schadensresistenz (Fera)", "Fera Damage Tolerance", CombatStatUnit.Percent, null),
            R("NatureDamage", "Angriffskraft (Natura)", "Natura Attack", CombatStatUnit.Points, null),
            R("NatureDefense", "Verteidigung (Natura)", "Natura Defense", CombatStatUnit.Points, "NatureDefense"),
            R("NatureAccuracy", "Präzision (Natura)", "Natura Accuracy", CombatStatUnit.Points, null),
            R("NatureEvasion", "Ausweichen (Natura)", "Natura Evasion", CombatStatUnit.Points, null),
            R("NatureCritical", "Krit.-Treffer (Natura)", "Natura Critical Hit", CombatStatUnit.Points, null),
            R("NatureCriticalResist", "Krit.-Widerstand (Natura)", "Natura Critical Hit Resist", CombatStatUnit.Points, "NatureCriticalResist"),
            R("NatureBlockPierce", "Block-Penetration (Natura)", "Natura Block Penetration", CombatStatUnit.Points, null),
            R("NatureBlock", "Block (Natura)", "Natura Block", CombatStatUnit.Points, "NatureBlock"),
            R("NatureAmplifyDamage", "Schadensverstärkung (Natura)", "Natura Damage Boost", CombatStatUnit.Percent, null),
            R("NatureDecreaseDamage", "Schadensresistenz (Natura)", "Natura Damage Tolerance", CombatStatUnit.Percent, null),
            R("TransDamage", "Angriffskraft (Varia)", "Varian Attack", CombatStatUnit.Points, null),
            R("TransDefense", "Verteidigung (Varia)", "Varian Defense", CombatStatUnit.Points, null),
            R("TransAccuracy", "Präzision (Varia)", "Varian Accuracy", CombatStatUnit.Points, null),
            R("TransEvasion", "Ausweichen (Varia)", "Varian Evasion", CombatStatUnit.Points, null),
            R("TransCritical", "Krit.-Treffer (Varia)", "Varian Critical Hit", CombatStatUnit.Points, null),
            R("TransCriticalResist", "Krit.-Widerstand (Varia)", "Varian Critical Hit Resist", CombatStatUnit.Points, "TransCriticalResist"),
            R("TransBlockPierce", "Block-Penetration (Varia)", "Varian Block Penetration", CombatStatUnit.Points, null),
            R("TransBlock", "Block (Varia)", "Varian Block", CombatStatUnit.Points, "TransBlock"),
            R("TransAmplifyDamage", "Schadensverstärkung (Varia)", "Varian Damage Boost", CombatStatUnit.Percent, null),
            R("TransDecreaseDamage", "Schadensresistenz (Varia)", "Varian Damage Tolerance", CombatStatUnit.Percent, null)),
        G(CombatStatOverviewGroup.Elements, "Elemente", "Elements",
            R("AddDamageWater", "Angriffskraft (Wasser)", "Water Attack", CombatStatUnit.Points, null),
            R("WaterDamageDefense", "Verteidigung (Wasser)", "Water Defense", CombatStatUnit.Points, null),
            R("AddDamageFire", "Angriffskraft (Feuer)", "Fire Attack", CombatStatUnit.Points, null),
            R("FireDamageDefense", "Verteidigung (Feuer)", "Fire Defense", CombatStatUnit.Points, null),
            R("AddDamageWind", "Angriffskraft (Wind)", "Wind Attack", CombatStatUnit.Points, null),
            R("WindDamageDefense", "Verteidigung (Wind)", "Wind Defense", CombatStatUnit.Points, null),
            R("AddDamageEarth", "Angriffskraft (Erde)", "Earth Attack", CombatStatUnit.Points, null),
            R("EarthDamageDefense", "Verteidigung (Erde)", "Earth Defense", CombatStatUnit.Points, null),
            R("AmplifyWater", "Verstärkung (Wasser)", "Water Boost", CombatStatUnit.Percent, null),
            R("ResistWater", "Resistenz (Wasser)", "Water Tolerance", CombatStatUnit.Percent, null),
            R("AmplifyFire", "Verstärkung (Feuer)", "Fire Boost", CombatStatUnit.Percent, null),
            R("ResistFire", "Resistenz (Feuer)", "Fire Tolerance", CombatStatUnit.Percent, null),
            R("AmplifyWind", "Verstärkung (Wind)", "Wind Boost", CombatStatUnit.Percent, null),
            R("ResistWind", "Resistenz (Wind)", "Wind Tolerance", CombatStatUnit.Percent, null),
            R("AmplifyEarth", "Verstärkung (Erde)", "Earth Boost", CombatStatUnit.Percent, null),
            R("ResistEarth", "Resistenz (Erde)", "Earth Tolerance", CombatStatUnit.Percent, null)),
        G(CombatStatOverviewGroup.Special, "Spezial", "Special",
            R("SprintSpeed", "Sprinttempo", "Sprinting Speed", CombatStatUnit.Percent, null),
            R("FlySpeed", "Flugtempo", "Flight Speed", CombatStatUnit.Percent, "FlySpeed"),
            R("GroundVehicleSpeed", "Boden-Tempo (Reittier)", "Mount Ground Move Speed", CombatStatUnit.Points, "GroundVehicleSpeed"),
            R("VehicleSprintCost", "SP-Kosten (Reittier): Sprint", "Mount Sprint Stamina Cost Reduction Rate", CombatStatUnit.Percent, "VehicleSprintCost"),
            R("HpHealRate", "Verstärkung (Heilung)", "Heal Boost", CombatStatUnit.Percent, "HpHealRate"),
            R("AmplifyHpHealGet", "Heilung (Erhalten)", "Incoming Heal", CombatStatUnit.Percent, "AmplifyHpHealGet"),
            R("Cooldown", "Abklingzeit", "Cooldown", CombatStatUnit.Percent, "Cooldown"),
            R("AmplifyAggro", "Aggro-Gewinn", "Enmity increase", CombatStatUnit.Percent, null)),
        G(CombatStatOverviewGroup.Resources, "Ressourcen", "Resources",
            R("SPMax", "SP", "Stamina", CombatStatUnit.Points, "SPMax"),
            R("FPMax", "FP", "Flight Power", CombatStatUnit.Points, "FPMax"),
            R("CombatHPRegen", "Natürliche LP-Regeneration (Kampf)", "Combat Natural HP Regen", CombatStatUnit.Points, "CombatHPRegen"),
            R("RestHPRegen", "Natürliche LP-Regeneration (Nicht-Kampf)", "Out-of-Combat Natural HP Regen", CombatStatUnit.Points, "RestHPRegen"),
            R("HpPotionRegen", "LP-Regeneration (Tränke)", "HP Potion Recovery", CombatStatUnit.Points, "HpPotionRegen"),
            R("HpPotionRate", "LP-Trank-Regeneration +", "HP Potion Recovery increase", CombatStatUnit.Percent, "HpPotionRate"),
            R("CombatMPRegen", "Natürliche MP-Regeneration (Kampf)", "Combat Natural MP Regen", CombatStatUnit.Points, "CombatMPRegen"),
            R("RestMPRegen", "Natürliche MP-Regeneration (Nicht-Kampf)", "Out-of-Combat Natural MP Regen", CombatStatUnit.Points, "RestMPRegen"),
            R("BattleAPRegen", "Wut-Regeneration (Natürlich, im Kampf)", "Combat Natural Rage Regen", CombatStatUnit.Points, null),
            R("RestAPRegen", "Wut-Regeneration (Natürlich, außerhalb des Kampfes)", "Out-of-Combat Natural Rage Regen", CombatStatUnit.Points, null),
            R("MPUseIncrease", "MP-Kosten", "MP Cost", CombatStatUnit.Percent, "MPUseIncrease"),
            R("HealCostMpRate", "MP-Gewinn +", "MP Gain increase", CombatStatUnit.Percent, null),
            R("HealCostAPRate", "Erhöht Wutgewinn", "Rage Gain increase", CombatStatUnit.Percent, null),
            R("BattleSPRegen", "Natürliche SP-Regeneration (Kampf)", "Combat Natural Stamina Regen", CombatStatUnit.Points, "BattleSPRegen"),
            R("RestSPRegen", "Natürliche SP-Regeneration (Nicht-Kampf)", "Out-of-Combat Natural Stamina Regen", CombatStatUnit.Points, "RestSPRegen"),
            R("BattleFPRegen", "Natürliche FP-Regeneration (Kampf)", "Combat Natural Flight Power Regen", CombatStatUnit.Points, "BattleFPRegen"),
            R("RestFPRegen", "Natürliche FP-Regeneration (Nicht-Kampf)", "Out-of-Combat Natural Flight Power Regen", CombatStatUnit.Points, "RestFPRegen")),
    });

    public static IReadOnlyList<CombatStatOverviewGroupDefinition> ActiveMainGroups { get; } = Active(MainGroups);
    public static IReadOnlyList<CombatStatOverviewGroupDefinition> ActiveSubGroups { get; } = Active(SubGroups);

    private static IReadOnlyList<CombatStatOverviewGroupDefinition> Active(IReadOnlyList<CombatStatOverviewGroupDefinition> groups) =>
        Array.AsReadOnly(groups.Select(group => group with
        {
            Rows = Array.AsReadOnly(group.Rows.Where(row => row.IsVisible).ToArray())
        }).ToArray());

    /// <summary>Only the supplied projection provides values, qualification, formula and source scope.</summary>
    public static CharacterDisplayStatRow Resolve(CombatStatOverviewRowDefinition definition, CharacterDisplayStats? display)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var matches = definition.OutputId is { } output && display is not null
            ? display.Rows.Where(row => string.Equals(row.Id, output, StringComparison.Ordinal)).Take(2).ToArray()
            : [];
        if (matches.Length == 1)
            return matches[0] with { LabelDe = definition.LabelDe, LabelEn = definition.LabelEn };
        return new()
        {
            Id = definition.Id, LabelDe = definition.LabelDe, LabelEn = definition.LabelEn,
            Category = definition.Category, Unit = definition.Unit,
            Status = matches.Length > 1 ? CombatKnowledgeStatus.Conflict : CombatKnowledgeStatus.Unknown,
            Qualification = CharacterDisplayStatQualification.Unknown,
            Reason = matches.Length > 1 ? "AmbiguousDisplayOutput"
                : definition.OutputId is null ? "UnqualifiedDisplayBinding" : "MissingDisplayOutput"
        };
    }

    private static CombatStatOverviewRowDefinition R(string id, string de, string en, CombatStatUnit unit, string? output) =>
        new(id, de, en, unit, output ?? (CharacterDisplayRollStatRules.CanBind(id, unit) ? id : null)) { IconKey = id };

    private static CombatStatOverviewGroupDefinition G(CombatStatOverviewGroup group, string de, string en,
        params CombatStatOverviewRowDefinition[] rows)
    {
        var category = group is CombatStatOverviewGroup.PrimaryAttributes or CombatStatOverviewGroup.GodAttributes
            or CombatStatOverviewGroup.MainStats ? CharacterDisplayStatCategory.Main : CharacterDisplayStatCategory.Sub;
        return new(group, de, en, Array.AsReadOnly(rows.Select(row => row with { Category = category }).ToArray()));
    }
}
