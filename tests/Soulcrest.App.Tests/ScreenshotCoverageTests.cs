using System.Collections.Immutable;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ScreenshotCoverageTests
{
    [Fact]
    public void TheFiniteNormalAsmodianScopeHasNumericGoalsWithoutInferringAvailability()
    {
        var definitions = AchievementDefinitionCatalog.Default;
        var factions = CombatAchievementFactionCatalog.Default;
        var own = definitions.Definitions.Where(row => row.Area == AchievementArea.Normal
            && row.Role == AchievementRole.PlayerFacing && factions.IncludesFamily(row.Id, Factions.Asmodian)).ToArray();
        Assert.Equal(338, own.Length);
        // Synthetic rows test projection coverage, not whether absent game rows are available or zero.
        var raw = new CharacterAchievementState(own.ToImmutableDictionary(row => row.Id,
            _ => new CharacterAchievement(0, 0, 0)), ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        var progress = new AchievementRuleEvaluator().Evaluate(raw, definitions.MetadataBuild, 1, 1);
        Assert.Equal(338, progress.Families.Count);
        foreach (var definition in own)
        {
            var family = progress.Find(definition.Id)!;
            Assert.NotNull(family.CurrentStageId);
            var current = family.Find(family.CurrentStageId!.Value)!;
            Assert.Equal(0, current.Current);
            Assert.True(current.Target > 0);
            Assert.NotEqual(CombatAchievementProgressUnit.Unknown, current.Unit);
        }
        Assert.Empty(new AchievementRuleEvaluator().Evaluate(new CharacterAchievementState(
            ImmutableDictionary<int, CharacterAchievement>.Empty,
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty), definitions.MetadataBuild, 1, 1).Families);
    }

    [Fact]
    public void RequirementPresentationIsBoundOnlyToTheFourteenObservedStages()
    {
        var families = AchievementProgressThresholdCatalog.Default.Families;
        var requirements = families.SelectMany(family => family.Stages
            .Where(stage => stage.Presentation == AchievementProgressPresentation.Requirement)
            .Select(stage => (Family: family, Stage: stage))).ToArray();
        Assert.Equal(new[] { 400100106, 430100302, 431100302, 431100403, 432100302, 432100403,
            433100302, 433100403, 434100302, 434100403, 435100302, 435100403, 443100303, 443100405 },
            requirements.Select(row => row.Stage.Id).Order());
        Assert.All(requirements, row => Assert.True(row.Family.ModelCurrentState));
        Assert.Equal(496, families.Count);
        Assert.Equal(2048, families.Sum(family => family.Stages.Count));
        Assert.Equal(330, families.Count(family => !family.ModelCurrentState));
        Assert.Equal(166, families.Count(family => family.ModelCurrentState));
    }
}
