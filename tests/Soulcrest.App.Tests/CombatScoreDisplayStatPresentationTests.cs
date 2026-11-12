using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Xunit;
using static Soulcrest.App.Components.CombatScoreCalculatorPresentation;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatScoreDisplayStatPresentationTests
{
    [Theory]
    [InlineData("de", "Testwert", "1,2345 %", "Abgeleitet · Zahlenabgleich")]
    [InlineData("en", "Test stat", "1.2345 %", "Derived · value match")]
    public void DisplayValuesKeepLocalUnitsExactDecimalsAndInvariantMachineValues(
        string language, string label, string value, string qualification)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            var row = Row(1.2345m, CharacterDisplayStatQualification.ObservedMatch) with
                { LabelDe = "Testwert", LabelEn = "Test stat", Unit = CombatStatUnit.Percent };
            Assert.Equal(label, DisplayStatLabel(row));
            Assert.Equal(value, DisplayStatValue(row));
            Assert.Equal("1.2345", DisplayMachineValue(row.Value));
            Assert.Equal(qualification, DisplayQualification(row.Qualification));
            Assert.Equal("0 %", DisplayStatValue(row with { Value = 0 }));
            Assert.Equal("—", DisplayStatValue(row with { Value = null }));
            Assert.Empty(DisplayMachineValue(null));
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData("de", "Fehlende Eingaben", "Veralteter Stand", "Zuordnung ungeklärt")]
    [InlineData("en", "Missing inputs", "Stale data", "Unqualified mapping")]
    public void QualificationAndFreshnessRemainSeparateFromAReceivedValue(
        string language, string missing, string stale, string unqualified)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            var absent = Row(null, CharacterDisplayStatQualification.ControlledChange) with
                { Status = CombatKnowledgeStatus.Unknown, MissingInputIds = [123] };
            var historical = Row(25, CharacterDisplayStatQualification.ControlledChange) with { Status = CombatKnowledgeStatus.Stale };
            var candidate = Row(25, CharacterDisplayStatQualification.Unknown);
            Assert.Equal(missing, DisplayStatStatus(absent));
            Assert.Equal(stale, DisplayStatStatus(historical));
            Assert.Equal(unqualified, DisplayStatStatus(candidate));
            Assert.Equal("missing", DisplayStatStateClass(absent));
            Assert.Equal("stale", DisplayStatStateClass(historical));
            Assert.Equal("unqualified", DisplayStatStateClass(candidate));
            Assert.Equal("25", DisplayStatValue(historical));
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public void CoverageCountsExplicitZeroButNeverTreatsMissingOrStaleRowsAsCurrent()
    {
        var rows = new[]
        {
            Row(100, CharacterDisplayStatQualification.NativeDirect),
            Row(0, CharacterDisplayStatQualification.ControlledChange),
            Row(12, CharacterDisplayStatQualification.ObservedMatch) with { Status = CombatKnowledgeStatus.Partial },
            Row(null, CharacterDisplayStatQualification.Unknown),
            Row(55, CharacterDisplayStatQualification.ControlledChange) with { Status = CombatKnowledgeStatus.Stale },
            Row(125, CharacterDisplayStatQualification.NativeDirect) with { ComparisonStatus = CombatKnowledgeStatus.Conflict }
        };
        var counts = CountDisplayStats(rows);
        Assert.Equal(new CharacterDisplayStatCounts(6, 3, 1, 1, 1, 1), counts);
        Assert.Equal("conflict", DisplayStatStateClass(rows[^1]));
        Assert.Equal("125", DisplayStatValue(rows[^1]));
        Assert.Equal(new CharacterDisplayStatCounts(0, 0, 0, 0, 0, 0), CountDisplayStats([]));
    }

    [Theory]
    [InlineData("de", "Aus Rolltabellen abgeleitet")]
    [InlineData("en", "Inferred from roll tables")]
    public void RollTableRecognitionRetainsItsOwnEvidenceLabel(string language, string expected)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            Assert.Equal(expected, DisplayQualification(CharacterDisplayStatQualification.TableInferred));
        }
        finally { UiText.Language = previous; }
    }

    private static CharacterDisplayStatRow Row(decimal? value, CharacterDisplayStatQualification qualification) => new()
    {
        Id = "SyntheticStat", Value = value, Status = CombatKnowledgeStatus.Complete,
        Qualification = qualification, Category = CharacterDisplayStatCategory.Main
    };
}
