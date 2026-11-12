using System.Collections.Frozen;
using Soulcrest.Core.GenusInsight;

namespace Soulcrest.Core.Combat;

/// <summary>Table-inferred native components for the Stats roster, independent of primary-stat aggregation and combat score.</summary>
public static class CharacterDisplayRollStatRules
{
    private static readonly FrozenSet<string> Outputs = new[]
    {
        "FrontAttackDamage", "FrontAttackDefense", "DecreaseWeaponDamage", "DecreaseFrontAttack", "FrontAttackCritical",
        "IntellectDamage", "IntellectCritical", "IntellectCriticalResist", "IntellectBlock", "IntellectAmplifyDamage", "IntellectDecreaseDamage",
        "FeralDamage", "FeralAccuracy", "FeralCritical", "FeralBlock", "FeralAmplifyDamage", "FeralDecreaseDamage",
        "NatureDamage", "NatureAccuracy", "NatureEvasion", "NatureCritical", "NatureAmplifyDamage", "NatureDecreaseDamage",
        "TransDamage", "TransDefense", "TransAccuracy", "TransEvasion", "TransCritical", "TransAmplifyDamage", "TransDecreaseDamage"
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, ushort> NativeIds = NativeStatDisplayBindings.Stats
        .ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
    private static readonly IReadOnlyList<string> Evidence = Array.AsReadOnly(new[]
    {
        "native-symbol-roll-table-unit-crosscheck-20261112",
        "historical-numeric-anchor-and-current-roll-table-exclusion-20261112",
        "display-reference-contract-only-no-runtime-build-attestation",
        "native-component-only-no-primary-aggregation-or-combat-score-authority"
    });

    /// <summary>The intentionally bounded additions; other unbound Stats rows stay unknown.</summary>
    public static IReadOnlySet<string> OutputIds => Outputs;

    public static bool CanBind(string statId, CombatStatUnit unit) => Outputs.Contains(statId)
        && NativeIds.ContainsKey(statId) && GenusInsightCatalog.Default.FindStat(statId) is { } definition
        && (definition.Unit == "Percent" ? CombatStatUnit.Percent : CombatStatUnit.Points) == unit;

    internal static IReadOnlyList<CharacterDisplayForwardRule> Create(IReadOnlySet<string> existingOutputs)
    {
        ArgumentNullException.ThrowIfNull(existingOutputs);
        var rules = new List<CharacterDisplayForwardRule>();
        foreach (var output in Outputs.Order(StringComparer.Ordinal))
        {
            if (existingOutputs.Contains(output) || !NativeIds.TryGetValue(output, out var nativeId)
                || GenusInsightCatalog.Default.FindStat(output) is not { } definition) continue;
            var percent = definition.Unit == "Percent";
            var places = percent ? 1 : 0;
            rules.Add(new()
            {
                Id = "table-inferred-roll-stats-20261112-" + output.ToLowerInvariant(),
                OutputId = output, LabelStatId = output, LabelDe = definition.LabelDe, LabelEn = definition.LabelEn,
                // The host supplies this display-reference contract; it is not an observed runtime build number.
                ClientBuild = CharacterDisplayStatProjection.QualifiedClientBuild,
                Category = CharacterDisplayStatCategory.Sub,
                Unit = percent ? CombatStatUnit.Percent : CombatStatUnit.Points,
                FlatInputs = Array.AsReadOnly(new[] { new CharacterDisplayStatTerm(nativeId) }),
                Divisor = definition.RawDivisor, DisplayDecimalPlaces = places, AllowNegative = percent,
                Rounding = CombatRounding.None, Qualification = CharacterDisplayStatQualification.TableInferred,
                Formula = $"roundEven(raw[{nativeId}] / {definition.RawDivisor}, {places})", Evidence = Evidence
            });
        }
        return Array.AsReadOnly(rules.ToArray());
    }
}
