using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CombatSkillPointEfficiencyTests
{
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 3, 3)]
    [InlineData(5, 4, 5)]
    [InlineData(6, 5, 7)]
    [InlineData(7, 6, 9)]
    [InlineData(8, 7, 13)]
    [InlineData(9, 8, 17)]
    [InlineData(10, 9, 21)]
    public void OriginalLevelUsesFreeBaseAndTheThreePurchaseCostTiers(int originalLevel,
        int purchasedRanks, int investedPoints)
    {
        var result = CombatSkillPointEfficiency.Calculate(null, originalLevel);

        Assert.Equal(purchasedRanks, result.PurchasedRanks);
        Assert.Equal(investedPoints, result.InvestedPoints);
        Assert.Null(result.Cps);
        Assert.Equal(CombatSkillPointEvidenceKind.Unknown, result.Evidence);
    }

    [Fact]
    public void ObservedAndModeledLearnedContributionsKeepTheirEvidenceAndFullDecimalPrecision()
    {
        var observed = CombatSkillPointEfficiency.Calculate(3300m, 10, CombatSkillPointEvidenceKind.ObservedChange);
        var modeled = CombatSkillPointEfficiency.Calculate(2900m, 10, CombatSkillPointEvidenceKind.ModelEstimate);

        Assert.Equal(9, observed.PurchasedRanks);
        Assert.Equal(21, observed.InvestedPoints);
        Assert.Equal(3300m / 21m, observed.Cps);
        Assert.Equal(2900m / 21m, modeled.Cps);
        Assert.NotEqual(decimal.Round(observed.Cps!.Value, 2), observed.Cps.Value);
        Assert.NotEqual(observed.Cps, modeled.Cps);
        Assert.Equal(CombatSkillPointEvidenceKind.ObservedChange, observed.Evidence);
        Assert.Equal(CombatSkillPointEvidenceKind.ModelEstimate, modeled.Evidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    public void MissingOrInvalidOriginalLevelDoesNotInventCostsOrCps(int? originalLevel)
    {
        var result = CombatSkillPointEfficiency.Calculate(100m, originalLevel,
            CombatSkillPointEvidenceKind.ObservedChange);

        Assert.Null(result.PurchasedRanks);
        Assert.Null(result.InvestedPoints);
        Assert.Null(result.Cps);
        Assert.Equal(CombatSkillPointEvidenceKind.Unknown, result.Evidence);
    }

    [Fact]
    public void MissingContributionCannotReleaseCpsEvenWithObservedEvidence()
    {
        var result = CombatSkillPointEfficiency.Calculate(null, 10, CombatSkillPointEvidenceKind.ObservedChange);

        Assert.Equal(9, result.PurchasedRanks);
        Assert.Equal(21, result.InvestedPoints);
        Assert.Null(result.Cps);
        Assert.Equal(CombatSkillPointEvidenceKind.Unknown, result.Evidence);
    }

    [Theory]
    [InlineData(CombatSkillPointEvidenceKind.Unknown)]
    [InlineData((CombatSkillPointEvidenceKind)99)]
    public void UnknownOrInvalidEvidenceCannotReleaseNumericCps(CombatSkillPointEvidenceKind evidence)
    {
        var result = CombatSkillPointEfficiency.Calculate(0m, 10, evidence);

        Assert.Equal(21, result.InvestedPoints);
        Assert.Null(result.Cps);
        Assert.Equal(CombatSkillPointEvidenceKind.Unknown, result.Evidence);
    }

    [Theory]
    [InlineData(CombatSkillPointEvidenceKind.ObservedChange)]
    [InlineData(CombatSkillPointEvidenceKind.ModelEstimate)]
    public void FreeOriginalBaseHasNoAverageBecauseNoPointsWereInvested(CombatSkillPointEvidenceKind evidence)
    {
        var result = CombatSkillPointEfficiency.Calculate(0m, 1, evidence);

        Assert.Equal(0, result.PurchasedRanks);
        Assert.Equal(0, result.InvestedPoints);
        Assert.Null(result.Cps);
        Assert.Equal(evidence, result.Evidence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(100)]
    public void QualifiedSignedContributionsAreDividedWithoutClamping(int contribution)
    {
        var result = CombatSkillPointEfficiency.Calculate(contribution, 7,
            CombatSkillPointEvidenceKind.ObservedChange);

        Assert.Equal(9, result.InvestedPoints);
        Assert.Equal(contribution / 9m, result.Cps);
        Assert.Equal(CombatSkillPointEvidenceKind.ObservedChange, result.Evidence);
    }
}
