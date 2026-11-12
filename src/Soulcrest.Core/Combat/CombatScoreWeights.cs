using Soulcrest.Core.Daevanion;

namespace Soulcrest.Core.Combat;

/// <summary>
/// One measured raw-linear combat-score weight. <see cref="Coefficient"/> is the midpoint of the interval that
/// every own controlled contrast allows; <see cref="Lower"/>/<see cref="Upper"/> keep the interval itself.
/// Points stats are weighted per point, Percent stats per 1 %.
/// </summary>
public sealed record CombatScoreWeight(string StatId, CombatStatUnit Unit, decimal Coefficient, decimal Lower, decimal Upper);

/// <summary>
/// Conditional intervals for a raw-linear working model (research 2026-11-09; scope audit 2026-11-12).
/// Flat and percentage components have separate weights under a fixed remainder and common rounding hypothesis.
/// Feasibility does not identify the native formula; source-specific factors and missing contributions remain open.
/// Only stats with an own controlled contrast are listed; every other stat stays unknown. Coefficients are interval
/// midpoints, not exact native constants. See docs/COMBAT_SCORE_API_RESEARCH_2026-11-12.md.
/// </summary>
public static class CombatScoreWeights
{
    public const string Version = "own-measured-raw-linear-2026-11-09";

    public static IReadOnlyList<CombatScoreWeight> Stats { get; } = Array.AsReadOnly(new CombatScoreWeight[]
    {
        new("AdditionalHitResistRate", CombatStatUnit.Percent, 30m, 29.2857m, 30.7143m),
        new("AmplifyBackAttack", CombatStatUnit.Percent, 101.25m, 100.625m, 101.875m),
        new("Critical", CombatStatUnit.Points, 7m, 6.8m, 7.2m),
        new("Defense", CombatStatUnit.Points, 0.26667m, 0.23333m, 0.3m),
        new("HPMax", CombatStatUnit.Points, 0.195m, 0.19m, 0.2m),
        new("HpPotionRegen", CombatStatUnit.Points, 0.605m, 0.575m, 0.635m),
        new("MaxHPRatio", CombatStatUnit.Percent, 31.1111m, 28.3333m, 33.8889m),
        new("MaxMPRatio", CombatStatUnit.Percent, 9m, 8.5m, 9.5m),
        new("MPMax", CombatStatUnit.Points, 0.26m, 0.24m, 0.28m),
    });

    public static CombatScoreWeight? Get(string statId) => Stats.FirstOrDefault(weight => weight.StatId == statId);

    // CP for the step L-1 -> L, measured on own skill ladders (Divine Aura 11-20, Earth's Retribution 3-12 incl. the
    // Original x Daevanion matrix, Heal Block 4-10). Equal CP was observed for two total-11 source configurations,
    // with different original investments and board points. This does not isolate source parity; gear/arcana parity
    // remains untested. Applying the same curve to their totals is a model assumption, not a native source rule.
    private static readonly IReadOnlyDictionary<int, int> ActiveSteps = new Dictionary<int, int>
    {
        [3] = 100, [4] = 100, [5] = 100, [6] = 100, [7] = 100, [8] = 100, [9] = 100, [10] = 100, [11] = 100,
        [12] = 500, [13] = 100, [14] = 100, [15] = 100, [16] = 900, [17] = 200, [18] = 200, [19] = 200, [20] = 1000
    };
    private static readonly IReadOnlyDictionary<int, int> PassiveSteps = new Dictionary<int, int>
    {
        [5] = 100, [6] = 100, [7] = 100, [8] = 100, [9] = 100, [10] = 100
    };

    /// <summary>Lowest level whose relative skill score is defined (0 at this level).</summary>
    public static int ReferenceLevel(DaevanionSkillKind kind) => kind switch
    {
        DaevanionSkillKind.Active => 2,
        DaevanionSkillKind.Passive => 4,
        _ => throw new ArgumentException("Unknown skill kind.")
    };

    /// <summary>
    /// Relative skill score: the sum of the measured steps from the reference level up to <paramref name="level"/>.
    /// The absolute score at the reference level is unknown, so only differences are exact. Levels outside the
    /// measured range return null.
    /// </summary>
    public static int? SkillScore(DaevanionSkillKind kind, int level)
    {
        var steps = kind switch
        {
            DaevanionSkillKind.Active => ActiveSteps,
            DaevanionSkillKind.Passive => PassiveSteps,
            _ => throw new ArgumentException("Unknown skill kind.")
        };
        var reference = ReferenceLevel(kind);
        if (level < reference) return null;
        var total = 0;
        for (var step = reference + 1; step <= level; step++)
        {
            if (!steps.TryGetValue(step, out var value)) return null;
            total += value;
        }
        return total;
    }
}
