namespace Soulcrest.Core.Combat;

public enum CombatSkillPointEvidenceKind { Unknown, ObservedChange, ModelEstimate }

/// <summary>
/// Average CP per invested learning point. Known rank costs remain available when the CP
/// contribution is unknown; Cps is null for unknown evidence or no invested points.
/// </summary>
public sealed record CombatSkillPointEfficiencyResult(int? PurchasedRanks, int? InvestedPoints,
    decimal? Cps, CombatSkillPointEvidenceKind Evidence);

public static class CombatSkillPointEfficiency
{
    /// <summary>
    /// Divides an already qualified, signed CP contribution attributable only to purchased
    /// Original ranks by their learning-point cost. The caller must compare against free
    /// Original level 1 with the same external bonuses; a whole-skill score at Total level
    /// is not this numerator. Observations and model estimates retain separate evidence.
    /// This method does not infer attribution, round CPS, or charge external bonus levels.
    /// </summary>
    /// <param name="learnedRankScoreContribution">Qualified CP difference for purchased Original ranks, or null if unknown.</param>
    /// <param name="originalLevel">Original level 1 through 10, excluding all external bonus levels.</param>
    /// <param name="evidence">How the supplied contribution was qualified; Unknown never releases a numeric CPS.</param>
    public static CombatSkillPointEfficiencyResult Calculate(decimal? learnedRankScoreContribution,
        int? originalLevel, CombatSkillPointEvidenceKind evidence = CombatSkillPointEvidenceKind.Unknown)
    {
        if (originalLevel is not (>= 1 and <= 10))
            return new(null, null, null, CombatSkillPointEvidenceKind.Unknown);

        var purchasedRanks = originalLevel.Value - 1;
        // User-confirmed skill-menu rule, 2026-11-04: free base rank, then 1/1/1/2/2/2/4/4/4.
        var investedPoints = Math.Min(purchasedRanks, 3)
            + Math.Clamp(purchasedRanks - 3, 0, 3) * 2
            + Math.Clamp(purchasedRanks - 6, 0, 3) * 4;
        if (learnedRankScoreContribution is null
            || evidence is not (CombatSkillPointEvidenceKind.ObservedChange or CombatSkillPointEvidenceKind.ModelEstimate))
            return new(purchasedRanks, investedPoints, null, CombatSkillPointEvidenceKind.Unknown);

        return new(purchasedRanks, investedPoints,
            investedPoints == 0 ? null : learnedRankScoreContribution.Value / investedPoints, evidence);
    }
}
