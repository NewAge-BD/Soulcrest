using System.Collections.Immutable;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NativeRollStatOverviewTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static IEnumerable<CombatStatOverviewRowDefinition> Definitions => CombatStatOverviewCatalog.MainGroups
        .Concat(CombatStatOverviewCatalog.SubGroups).SelectMany(group => group.Rows);

    [Fact]
    public void EveryAdditionalNamedRowHasOnePresentationBindingWhileOtherUnknownRowsStayUnbound()
    {
        var additions = Definitions.Where(row => CharacterDisplayRollStatRules.OutputIds.Contains(row.Id)).ToArray();
        Assert.Equal(30, additions.Length);
        Assert.All(additions, row =>
        {
            Assert.Equal(row.Id, row.OutputId);
            Assert.True(CharacterDisplayRollStatRules.CanBind(row.Id, row.Unit));
        });
        foreach (var id in new[] { "AmplifyFrontAttack", "FrontAttackCriticalResist", "Light", "Dark", "StunAccuracy", "HealCostAPRate" })
        {
            var definition = Assert.Single(Definitions, row => row.Id == id);
            Assert.Null(definition.OutputId);
            Assert.Null(CombatStatOverviewCatalog.Resolve(definition, new()).Value);
        }
        Assert.Equal(197, Definitions.Count());
        Assert.Equal(131, Definitions.Count(row => row.OutputId is not null));
    }

    [Fact]
    public void PointAndPercentRowsReachTheStatsOverviewWithoutChangingTheirTableInferenceStatus()
    {
        var values = new Dictionary<ushort, int> { [587] = 17, [588] = 115, [590] = 37, [403] = 18, [483] = 125 };
        var display = Project(values);
        foreach (var (id, expected) in new[]
        {
            ("FrontAttackDamage", 17m), ("FrontAttackDefense", 115m), ("DecreaseFrontAttack", .4m),
            ("FeralAccuracy", 18m), ("IntellectAmplifyDamage", 1.2m)
        })
        {
            var definition = Assert.Single(Definitions, row => row.Id == id);
            var row = CombatStatOverviewCatalog.Resolve(definition, display);
            Assert.Equal(expected, row.Value);
            Assert.Equal(definition.LabelDe, row.LabelDe);
            Assert.Equal(definition.LabelEn, row.LabelEn);
            Assert.Equal(CharacterDisplayStatQualification.TableInferred, row.Qualification);
            Assert.Equal(CombatKnowledgeStatus.Complete, row.Status);
        }
    }

    [Fact]
    public void ActorFreeObservationKeepsRawUnitsQualificationAndTheObservedScoreSeparate()
    {
        var values = ImmutableDictionary<ushort, int>.Empty.Add(587, 17).Add(590, 37);
        var snapshot = new CharacterCombatSnapshot
        {
            ProfileId = "anonymous-stats-profile", Generation = 1, Sequence = 3, IsFresh = true,
            CurrentCombatScore = 12345,
            NativeStats = new(values, NetworkCombatSourceStatus.Partial, true, At, 3, "own-full-stat-list"),
            DisplayStats = Project(values)
        };
        var observation = CombatStatObservationPresentation.Create(snapshot)!;
        var attack = Assert.Single(observation.ProjectedRows, row => row.Id == "FrontAttackDamage");
        var tolerance = Assert.Single(observation.ProjectedRows, row => row.Id == "DecreaseFrontAttack");
        Assert.Equal(17m, attack.Value);
        Assert.Equal(.4m, tolerance.Value);
        Assert.Equal(CharacterDisplayStatQualification.TableInferred, attack.Qualification);
        Assert.Equal(CharacterDisplayStatQualification.TableInferred, tolerance.Qualification);
        Assert.Equal(12345, observation.CurrentCombatScore);
        Assert.Equal(37, observation.NativeStats.Values![590]);
        Assert.DoesNotContain("anonymous-stats-profile", CombatStatObservationPresentation.Serialize(observation));
        Assert.Empty(CombatEffectCatalog.Default.NativeForwardRules);
    }

    private static CharacterDisplayStats Project(IReadOnlyDictionary<ushort, int> values) => CharacterDisplayStatProjection.Project(new()
    {
        ClientBuild = CharacterDisplayStatProjection.QualifiedClientBuild, ClassId = "Assassin", Generation = 1,
        NativeStats = new(values, CombatKnowledgeStatus.Partial, true, 1, At, "own-full-stat-list")
    });
}
