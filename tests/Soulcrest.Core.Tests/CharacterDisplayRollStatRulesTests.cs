using Soulcrest.Core.Combat;
using Soulcrest.Core.GenusInsight;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CharacterDisplayRollStatRulesTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("FrontAttackDamage", 17, 17)]
    [InlineData("FrontAttackDefense", 115, 115)]
    [InlineData("FrontAttackCritical", 23, 23)]
    [InlineData("IntellectCritical", 19, 19)]
    [InlineData("FeralAccuracy", 18, 18)]
    [InlineData("NatureAccuracy", 13, 13)]
    [InlineData("TransDefense", 62, 62)]
    public void NamedPointComponentsUseTheirSharedBindingWithoutAggregatingOtherStats(string id, int raw, int expected)
    {
        var nativeId = NativeId(id);
        var row = Row(Project(new Dictionary<ushort, int> { [nativeId] = raw }), id);
        Assert.Equal(expected, row.Value);
        Assert.Equal(CombatStatUnit.Points, row.Unit);
        Assert.Equal(CharacterDisplayStatQualification.TableInferred, row.Qualification);
        Assert.Equal(CombatKnowledgeStatus.Complete, row.Status);
        Assert.Equal(new ushort[] { nativeId }, row.Inputs.Select(input => input.NativeId));
        Assert.Equal(At, row.ObservedAt);
        Assert.Equal("own-full-stat-list", row.Source);
    }

    [Theory]
    [InlineData("DecreaseWeaponDamage", 125, "1.2")]
    [InlineData("DecreaseFrontAttack", 37, "0.4")]
    [InlineData("IntellectAmplifyDamage", 135, "1.4")]
    [InlineData("FeralDecreaseDamage", 115, "1.2")]
    [InlineData("NatureAmplifyDamage", 117, "1.2")]
    [InlineData("TransDecreaseDamage", -123, "-1.2")]
    public void PercentComponentsUseTheCatalogDivisorAndExistingMidpointToEvenDisplayRounding(string id, int raw, string expected)
    {
        var row = Row(Project(new Dictionary<ushort, int> { [NativeId(id)] = raw }), id);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), row.Value);
        Assert.Equal(raw / 100m, row.UnroundedValue);
        Assert.Equal(CombatStatUnit.Percent, row.Unit);
        Assert.Equal(CharacterDisplayStatQualification.TableInferred, row.Qualification);
    }

    [Fact]
    public void MissingInputsAndExplicitZeroRemainDistinctOnAPartialList()
    {
        var input = Input(new Dictionary<ushort, int>());
        input = input with { NativeStats = input.NativeStats! with { Status = CombatKnowledgeStatus.Partial } };
        var missing = Row(CharacterDisplayStatProjection.Project(input), "FeralBlock");
        Assert.Null(missing.Value);
        Assert.Equal("MissingInputs", missing.Reason);
        Assert.Equal(new ushort[] { NativeId("FeralBlock") }, missing.MissingInputIds);
        input = input with { NativeStats = input.NativeStats with { Values = new Dictionary<ushort, int> { [NativeId("FeralBlock")] = 0 } } };
        var zero = Row(CharacterDisplayStatProjection.Project(input), "FeralBlock");
        Assert.Equal(0, zero.Value);
        Assert.Equal(CombatKnowledgeStatus.Complete, zero.Status);
    }

    [Theory]
    [InlineData("wrong-build", CombatKnowledgeStatus.Unknown)]
    [InlineData("generation", CombatKnowledgeStatus.Stale)]
    [InlineData("stale", CombatKnowledgeStatus.Stale)]
    [InlineData("conflict", CombatKnowledgeStatus.Conflict)]
    [InlineData("unknown-source", CombatKnowledgeStatus.Unknown)]
    [InlineData("missing-timestamp", CombatKnowledgeStatus.Unknown)]
    public void ExistingAuthorityAndSourceGatesApplyToTheAdditionalRows(string gate, CombatKnowledgeStatus expected)
    {
        var input = Input(new Dictionary<ushort, int> { [NativeId("FrontAttackDamage")] = 17 });
        input = gate switch
        {
            "wrong-build" => input with { ClientBuild = 26581260 },
            "generation" => input with { NativeStats = input.NativeStats! with { Generation = 2 } },
            "stale" => input with { NativeStats = input.NativeStats! with { IsFresh = false } },
            "conflict" => input with { NativeStats = input.NativeStats! with { Status = CombatKnowledgeStatus.Conflict } },
            "unknown-source" => input with { NativeStats = input.NativeStats! with { Source = "unqualified-source" } },
            _ => input with { NativeStats = input.NativeStats! with { ObservedAt = null } }
        };
        var row = Row(CharacterDisplayStatProjection.Project(input), "FrontAttackDamage");
        Assert.Null(row.Value);
        Assert.Equal(expected, row.Status);
    }

    [Fact]
    public void AdditionsAreBoundedToThirtyRowsAndDoNotReplaceExistingFormulasOrCreateScoreRules()
    {
        Assert.Equal(30, CharacterDisplayRollStatRules.OutputIds.Count);
        var added = CharacterDisplayStatProjection.Rules.Where(rule => rule.Qualification == CharacterDisplayStatQualification.TableInferred).ToArray();
        Assert.Equal(30, added.Length);
        Assert.Equal(CharacterDisplayRollStatRules.OutputIds.Order(), added.Select(rule => rule.OutputId).Order());
        Assert.All(added, rule =>
        {
            var definition = GenusInsightCatalog.Default.FindStat(rule.OutputId)!;
            Assert.Equal(definition.RawDivisor, rule.Divisor);
            Assert.Equal(NativeId(rule.OutputId), Assert.Single(rule.FlatInputs).NativeId);
            Assert.Null(rule.RequiredClassId);
            Assert.Null(rule.IncreaseBasisPointsInput);
            Assert.StartsWith("table-inferred-roll-stats-20261112-", rule.Id);
            Assert.Contains("native-component-only-no-primary-aggregation-or-combat-score-authority", rule.Evidence);
        });
        foreach (var id in new[] { "HPMax", "MPMax", "Attack", "Defense", "Evasion", "Accuracy", "Critical", "Block" })
        {
            var rule = Assert.Single(CharacterDisplayStatProjection.Rules, rule => rule.OutputId == id);
            Assert.NotEqual(CharacterDisplayStatQualification.TableInferred, rule.Qualification);
            Assert.False(CharacterDisplayRollStatRules.CanBind(id, rule.Unit));
        }
        Assert.Single(CharacterDisplayRollStatRules.Create(new HashSet<string>(
            CharacterDisplayRollStatRules.OutputIds.Except(["FrontAttackDamage"]))));
        Assert.Empty(CombatEffectCatalog.Default.NativeForwardRules);
        Assert.Null(CombatEffectCatalog.Default.NormalizeNative(587, 17));
    }

    [Fact]
    public void TwoUnboundFrontStatsAndWrongUnitsCannotAcquireADisplayBinding()
    {
        Assert.False(CharacterDisplayRollStatRules.CanBind("AmplifyFrontAttack", CombatStatUnit.Percent));
        Assert.False(CharacterDisplayRollStatRules.CanBind("FrontAttackCriticalResist", CombatStatUnit.Points));
        Assert.False(CharacterDisplayRollStatRules.CanBind("FrontAttackDamage", CombatStatUnit.Percent));
        Assert.True(CharacterDisplayRollStatRules.CanBind("FrontAttackDamage", CombatStatUnit.Points));
        var display = Project(new Dictionary<ushort, int> { [589] = 100, [592] = 10 });
        Assert.DoesNotContain(display.Rows, row => row.Id is "AmplifyFrontAttack" or "FrontAttackCriticalResist");
    }

    private static ushort NativeId(string id) => Assert.Single(NativeStatDisplayBindings.Stats, pair => pair.Value == id).Key;
    private static CharacterDisplayStats Project(IReadOnlyDictionary<ushort, int> values) => CharacterDisplayStatProjection.Project(Input(values));
    private static CharacterDisplayStatsInput Input(IReadOnlyDictionary<ushort, int> values) => new()
    {
        ClientBuild = CharacterDisplayStatProjection.QualifiedClientBuild, ClassId = "Assassin", Generation = 1,
        NativeStats = new(values, CombatKnowledgeStatus.Complete, true, 1, At, "own-full-stat-list")
    };
    private static CharacterDisplayStatRow Row(CharacterDisplayStats display, string id) => Assert.Single(display.Rows, row => row.Id == id);
}
