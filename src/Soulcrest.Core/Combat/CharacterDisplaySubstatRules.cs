namespace Soulcrest.Core.Combat;

// Independently cross-checked display facts only; never a general wire-to-effect mapping.
internal static class CharacterDisplaySubstatRules
{
    internal static IReadOnlyList<CharacterDisplayForwardRule> Create()
    {
        var evidence = Array.AsReadOnly(new[] { "historical-enum-current-semantic-unit-localization-crosscheck-2026-11-03",
            "current-native-component-panel-vector-2026-11-03", "display-rounding-observed-scope-only" });
        CharacterDisplayForwardRule Direct(ushort nativeId, string id, decimal divisor, bool percent, string english, string german) => new()
        {
            Id = $"character-display-26262663-sub-{id.ToLowerInvariant()}-v1", OutputId = id, LabelStatId = id,
            LabelEn = english, LabelDe = german, ClientBuild = CharacterDisplayStatProjection.QualifiedClientBuild,
            Category = id is "CombatSpeed" or "MoveSpeed" ? CharacterDisplayStatCategory.Main : CharacterDisplayStatCategory.Sub,
            Unit = percent ? CombatStatUnit.Percent : CombatStatUnit.Points, FlatInputs = Array.AsReadOnly(new[] { new CharacterDisplayStatTerm(nativeId) }),
            Divisor = divisor, DisplayDecimalPlaces = percent ? 1 : 0, AllowNegative = percent,
            Rounding = CombatRounding.None, Qualification = CharacterDisplayStatQualification.ObservedMatch,
            Formula = $"roundEven(raw[{nativeId}] / {divisor}, {(percent ? 1 : 0)})", Evidence = evidence
        };
        CharacterDisplayForwardRule Compound(string id, string label, CharacterDisplayStatTerm[] terms, decimal divisor,
            bool percent, string formula, ushort? ratio = null, CombatRounding rounding = CombatRounding.None) => new()
        {
            Id = $"character-display-26262663-sub-{id.ToLowerInvariant()}-v1", OutputId = id, LabelStatId = label,
            ClientBuild = CharacterDisplayStatProjection.QualifiedClientBuild, Category = CharacterDisplayStatCategory.Sub,
            Unit = percent ? CombatStatUnit.Percent : CombatStatUnit.Points, FlatInputs = Array.AsReadOnly(terms),
            Divisor = divisor, DisplayDecimalPlaces = percent ? 1 : null, AllowNegative = percent,
            IncreaseBasisPointsInput = ratio, Rounding = rounding, Formula = formula,
            Qualification = CharacterDisplayStatQualification.ObservedMatch, Evidence = evidence
        };
        return Array.AsReadOnly(new[]
        {
            Direct(284, "DefensePierce", 1m, false, "Penetration", "Penetration"),
            Direct(69, "SealStoneAddDamage", 1m, false, "Power Shard Damage Bonus", "Zusatzschaden (Siegelstein)"),
            Direct(38, "CriticalAddDamage", 1m, false, "Critical Attack", "Krit.-Angriffskraft"),
            Direct(41, "CriticalDamageDefense", 1m, false, "Critical Damage Defense", "Krit.-Verteidigung"),
            Direct(98, "BackAttackDamage", 1m, false, "Back Attack", "Angriffskraft (Rückseitig)"),
            Direct(99, "BackAttackDefense", 1m, false, "Back Defense", "Verteidigung (Rückseitig)"),
            Direct(28, "AmplifyAllDamage", 100m, true, "Damage Boost", "Schadensverstärkung"),
            Direct(70, "DecreaseDamage", 100m, true, "Damage Tolerance", "Schadensresistenz"),
            Direct(32, "AmplifyWeaponDamage", 100m, true, "Weapon Damage Boost", "Verstärkung (Waffenschaden)"),
            Direct(44, "AmplifyCriticalDamage", 100m, true, "Critical Damage Boost", "Krit.-Schadensverstärkung"),
            Direct(47, "DecreaseCriticalDamage", 100m, true, "Critical Damage Tolerance", "Krit.-Schadensresistenz"),
            // ClientBuild is the display reference catalogue, not an attested runtime build.
            // The new field has its own current controlled evidence, independent of the 03 November panel vector.
            Direct(102, "AmplifyBackAttack", 100m, true, "Back Attack Damage Boost", "Schadensverstärkung (Rückseitig)") with
            {
                Id = "controlled-title-amplifybackattack-2026-11-09-v1", RequiredClassId = "Cleric",
                Qualification = CharacterDisplayStatQualification.ControlledChange,
                Evidence = Array.AsReadOnly(new[] { "display-reference-catalog-integration-scope-only",
                    "independent-source-metadata-crosscheck-2026-11-09",
                    "controlled-own-same-generation-explicit-0-160-0-2026-11-09" })
            },
            Direct(103, "DecreaseBackAttack", 100m, true, "Back Attack Damage Tolerance", "Schadensresistenz (Rückseitig)"),
            Direct(146, "AdditionalHitRate", 100m, true, "Multi-hit Chance", "Mehrfachtreffer"),
            Direct(147, "AdditionalHitResistRate", 100m, true, "Multi-hit Resist", "Widerstand (Mehrfachtreffer)") with
            {
                Id = "controlled-title-additionalhitresistrate-2026-11-09-v1", RequiredClassId = "Cleric",
                Qualification = CharacterDisplayStatQualification.ControlledChange,
                Evidence = Array.AsReadOnly(new[] { "display-reference-catalog-integration-scope-only",
                    "independent-source-metadata-crosscheck-2026-11-09",
                    "controlled-own-same-generation-explicit-140-0-2026-11-09" })
            },
            Direct(100, "BackAttackCritical", 1m, false, "Back Attack Critical Hit", "Krit.-Treffer (Rückseitig)"),
            Direct(101, "BackAttackCriticalResist", 1m, false, "Back Attack Critical Hit Resist", "Krit.-Widerstand (Rückseitig)"),
            Direct(256, "BlockPierce", 1m, false, "Block Penetration", "Penetration (Block)"),
            Direct(448, "IgnoreRestoration", 100m, true, "Regeneration Penetration", "Regenerations-Penetration"),
            Direct(444, "Restoration", 100m, true, "Regeneration", "Regeneration"),
            Direct(442, "Perfect", 100m, true, "Perfect Chance", "Perfektion"),
            Direct(446, "PerfectResist", 100m, true, "Perfect Resist", "Perfekter Widerstand"),
            Direct(443, "HardHit", 100m, true, "Double Chance", "Wucht"),
            Direct(447, "HardHitResist", 100m, true, "Double Resist", "Wucht-Widerstand"),
            Direct(449, "IgnoreIronWall", 100m, true, "Endurance Penetration", "Eisenwall-Penetration"),
            Direct(445, "IronWall", 100m, true, "Endurance", "Eisenwall"),
            Direct(58, "PvPAddDamage", 1m, false, "PvP Attack", "Angriffskraft (PvP)"),
            Direct(61, "PvPDamageDefense", 1m, false, "PvP Defense", "Verteidigung (PvP)"),
            Direct(65, "PvPAmplifyDamage", 100m, true, "PvP Damage Boost", "Schadensverstärkung (PvP)"),
            Direct(76, "PvPDecreaseDamage", 100m, true, "PvP Damage Tolerance", "Schadensresistenz (PvP)"),
            Direct(113, "PvPAccuracy", 1m, false, "PvP Accuracy", "Präzision (PvP)"),
            Direct(125, "PvPEvasion", 1m, false, "PvP Evasion", "Ausweichen (PvP)"),
            Direct(132, "PvPCritical", 1m, false, "PvP Critical Hit", "Krit.-Treffer (PvP)"),
            Direct(136, "PvPCriticalResist", 1m, false, "PvP Critical Hit Resist", "Krit.-Widerstand (PvP)"),
            Direct(56, "PvEAddDamage", 1m, false, "PvE Attack", "Angriffskraft (PvE)"),
            Direct(57, "PvEDamageDefense", 1m, false, "PvE Defense", "Verteidigung (PvE)"),
            Direct(110, "PvEAccuracy", 1m, false, "PvE Accuracy", "Präzision (PvE)"),
            Direct(122, "PvEEvasion", 1m, false, "PvE Evasion", "Ausweichen (PvE)"),
            Direct(379, "PvEAmplifyDamage", 100m, true, "PvE Damage Boost", "Schadensverstärkung (PvE)"),
            Direct(380, "PvEDecreaseDamage", 100m, true, "PvE Damage Tolerance", "Schadensresistenz (PvE)"),
            Direct(50, "BossNpcAddDamage", 1m, false, "Boss Attack", "Angriffskraft (Boss)"),
            Direct(51, "BossNpcDefense", 1m, false, "Boss Defense", "Verteidigung (Boss)"),
            Direct(520, "BossNpcAmplifyDamage", 100m, true, "Boss Damage Boost", "Schadensverstärkung (Boss)"),
            Direct(521, "BossNpcDecreaseDamage", 100m, true, "Boss Damage Tolerance", "Schadensresistenz (Boss)"),
            Direct(394, "IntellectDefense", 1m, false, "Cogni Defense", "Verteidigung (Cognia)"),
            Direct(395, "IntellectAccuracy", 1m, false, "Cogni Accuracy", "Präzision (Cognia)"),
            Direct(396, "IntellectEvasion", 1m, false, "Cogni Evasion", "Ausweichen (Cognia)"),
            Direct(402, "FeralDefense", 1m, false, "Fera Defense", "Verteidigung (Fera)"),
            Direct(404, "FeralEvasion", 1m, false, "Fera Evasion", "Ausweichen (Fera)"),
            Direct(406, "FeralCriticalResist", 1m, false, "Fera Critical Hit Resist", "Krit.-Widerstand (Fera)"),
            Direct(410, "NatureDefense", 1m, false, "Natura Defense", "Verteidigung (Natura)"),
            Direct(414, "NatureCriticalResist", 1m, false, "Natura Critical Hit Resist", "Krit.-Widerstand (Natura)"),
            Direct(416, "NatureBlock", 1m, false, "Natura Block", "Block (Natura)"),
            Direct(422, "TransCriticalResist", 1m, false, "Varian Critical Hit Resist", "Krit.-Widerstand (Varia)"),
            Direct(424, "TransBlock", 1m, false, "Varian Block", "Block (Varia)"),
            Direct(214, "FlySpeed", 100m, true, "Flight Speed", "Flugtempo"),
            Direct(208, "GroundVehicleSpeed", 1m, false, "Mount Ground Move Speed", "Boden-Tempo (Reittier)"),
            Direct(210, "VehicleSprintCost", 100m, true, "Mount Sprint Stamina Cost Reduction Rate", "SP-Kosten (Reittier): Sprint"),
            Direct(218, "HpHealRate", 100m, true, "Heal Boost", "Verstärkung (Heilung)"),
            Direct(235, "AmplifyHpHealGet", 100m, true, "Incoming Heal", "Heilung (Erhalten)"),
            Direct(221, "HpPotionRegen", 1m, false, "HP Potion Recovery", "LP-Regeneration (Tränke)"),
            Direct(220, "HpPotionRate", 100m, true, "HP Potion Recovery increase", "LP-Trank-Regeneration +"),
            Direct(386, "MPUseIncrease", 100m, true, "MP Cost", "MP-Kosten"),
            Direct(242, "SPMax", 100m, false, "Stamina", "SP"),
            Direct(298, "FPMax", 100m, false, "Flight Power", "FP"),
            Direct(277, "BattleSPRegen", 100m, false, "Combat Natural Stamina Regen", "Natürliche SP-Regeneration (Kampf)"),
            Direct(244, "RestSPRegen", 100m, false, "Out-of-Combat Natural Stamina Regen", "Natürliche SP-Regeneration (Nicht-Kampf)"),
            Direct(300, "BattleFPRegen", 100m, false, "Combat Natural Flight Power Regen", "Natürliche FP-Regeneration (Kampf)"),
            Direct(301, "RestFPRegen", 100m, false, "Out-of-Combat Natural Flight Power Regen", "Natürliche FP-Regeneration (Nicht-Kampf)"),
            Direct(205, "MoveSpeed", 100m, true, "Move Speed", "Bewegungstempo"),
            Direct(282, "CombatSpeed", 100m, true, "Combat Speed", "Kampftempo"),
            Compound("Block", "Block", [new(255)], 1, false, "floor(raw[255] * (10000 + raw[432]) / 10000)", 432, CombatRounding.Floor),
            Compound("Cooldown", "CoolTimeIncrease", [new(385), new(215, -1)], 100, true, "roundEven((raw[385] - raw[215]) / 100, 1)"),
            Compound("CombatHPRegen", "BattleHPRegen", [new(194), new(275)], 1, false, "raw[194] + raw[275]"),
            Compound("RestHPRegen", "RestHPRegen", [new(194), new(195)], 1, false, "raw[194] + raw[195]"),
            Compound("CombatMPRegen", "BattleMPRegen", [new(200), new(276)], 1, false, "raw[200] + raw[276]"),
            Compound("RestMPRegen", "RestMPRegen", [new(200), new(201)], 1, false, "raw[200] + raw[201]")
        });
    }
}
