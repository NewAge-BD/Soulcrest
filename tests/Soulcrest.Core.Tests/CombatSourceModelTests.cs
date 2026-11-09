using System.Collections.Immutable;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CombatSourceModelTests
{
    [Fact(Skip = PendingOwnWeights.Reason)]
    public void CompositionUsesTheScoringTermsAndRoundsOnlyTheirWholeSum()
    {
        var catalog = new CombatEffectCatalog("test", 1,
            [new() { StatId = "HPMax" }, new() { StatId = "Defense" }],
            [new("HPMax", .15m, CombatStatUnit.Points), new("Defense", 1.34m, CombatStatUnit.Points)]);
        var graph = Graph(Source("stats", new("HPMax", 3, Application: CombatApplication.Confirmed),
            new("Defense", 2, Application: CombatApplication.Confirmed)), SkillSource("skill", 2));
        var evaluation = CombatScoreModel.Evaluate(graph, catalog);
        var composition = evaluation.Composition;
        Assert.True(composition.IsComplete);
        Assert.Equal(3.13m, composition.KnownStatSubtotal);
        Assert.Equal(100m, composition.KnownSkillSubtotal);
        Assert.Equal(103.13m, composition.UnroundedSubtotal);
        Assert.Equal(103m, composition.RoundedSubtotal);
        Assert.Equal(-.13m, composition.RoundingAdjustment);
        Assert.Equal(evaluation.ModelTotal, composition.RoundedSubtotal);
        Assert.Equal(CombatScoreModel.Calculate(graph, catalog).Total, composition.UnroundedSubtotal);
        Assert.Equal(composition.UnroundedSubtotal,
            composition.Stats.Sum(term => term.Contribution) + composition.Skills.Sum(term => term.Contribution));
        var hp = Assert.Single(composition.Stats, term => term.StatId == "HPMax");
        Assert.Equal(3m, hp.Value); Assert.Equal(.15m, hp.Coefficient); Assert.Equal(.45m, hp.Contribution);
        Assert.Throws<NotSupportedException>(() => ((IList<CombatScoreStatTerm>)composition.Stats).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CombatScoreSkillTerm>)composition.Skills).Clear());
    }

    [Fact(Skip = PendingOwnWeights.Reason)]
    public void CompositionKeepsUnknownWeightsAndValuesSeparateFromExplicitModelZero()
    {
        var catalog = new CombatEffectCatalog("test", 1,
            [new() { StatId = "HPMax" }, new() { StatId = "Defense" }, new() { StatId = "Unweighted" }, new() { StatId = "PvPAccuracy" }],
            [new("HPMax", .15m, CombatStatUnit.Points), new("Defense", 1.34m, CombatStatUnit.Points)]);
        var graph = Graph(Source("known", new("HPMax", 1, Application: CombatApplication.Confirmed),
                new("Unweighted", 7, Application: CombatApplication.Confirmed), new("PvPAccuracy", 5, Application: CombatApplication.Confirmed)),
            Source("unknown", new CombatEffect("Defense", 2)),
            new() { Id = "unknown-skill", Status = CombatKnowledgeStatus.Unknown,
                Skills = [new(1, DaevanionSkillKind.Active, null)] });
        var evaluation = CombatScoreModel.Evaluate(graph, catalog);
        var composition = evaluation.Composition;
        Assert.Null(evaluation.ModelTotal); Assert.False(composition.IsComplete);
        var unweighted = Assert.Single(composition.Stats, term => term.StatId == "Unweighted");
        Assert.Equal(7m, unweighted.Value); Assert.Null(unweighted.Coefficient); Assert.Null(unweighted.Contribution);
        Assert.False(unweighted.IsIntermediate);
        var unknown = Assert.Single(composition.Stats, term => term.StatId == "Defense");
        Assert.Null(unknown.Value); Assert.Equal(1.34m, unknown.Coefficient); Assert.Null(unknown.Contribution);
        var zero = Assert.Single(composition.Stats, term => term.StatId == "PvPAccuracy");
        Assert.Equal(0m, zero.Coefficient); Assert.Equal(0m, zero.Contribution);
        Assert.Null(Assert.Single(composition.Skills).Contribution);
        Assert.Equal(.15m, composition.UnroundedSubtotal); Assert.Equal(0m, composition.RoundedSubtotal);
        Assert.All(evaluation.Sources, source => Assert.Null(source.ModelLoss));
    }

    [Fact]
    public void OfflineCatalogPreservesLocalCurvesUnitsAndUnqualifiedApplications()
    {
        var catalog = CombatEffectCatalog.Default;
        Assert.Equal(26262663, catalog.ClientBuild);
        Assert.Equal(16, catalog.PrimaryStatCurves.Count);
        Assert.All(catalog.PrimaryStatCurves, curve => Assert.Equal(1000, curve.Values.Count));
        Assert.Equal(900, catalog.BaseLevelTable.Count);
        Assert.Equal(176, catalog.Skills.Count);
        var assassinHp = Assert.Single(catalog.PassiveEffects, row => row.EffectId == "1371000013");
        Assert.Equal(200m, assassinHp.ValueAt(1)); Assert.Equal(8000m, assassinHp.ValueAt(40));
        Assert.Equal(137100001, assassinHp.AbnormalId); Assert.True(assassinHp.HasUnconditionalPassiveChain);
        Assert.Equal(CombatApplication.Unknown, assassinHp.Application);
        Assert.Contains("Dagger", assassinHp.AllowedWeaponTypes);
        Assert.Null(catalog.NormalizeNative(1, 100)); // UI table order is not a qualified wire enum.
        Assert.All(catalog.StatDefinitions, row => Assert.True(row.RawDivisor > 0));
        var alias = catalog.SourceEffects.First(source => source.Aliases.Count != 0);
        Assert.Same(alias, catalog.GetSource(alias.SourceKind, alias.Aliases[0], alias.Channel));
        Assert.StartsWith("own-", CombatScoreModel.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLossIsAdditiveBecauseFlatAndPercentComponentsAreWeightedSeparately()
    {
        // Research 2026-11-09: the score is linear in the raw components, so no shared multiplier interaction exists.
        var catalog = RatioCatalog(hpWeight: 1, ratioWeight: 2);
        var graph = Graph(
            Source("base", new CombatEffect("HPMax", 100, Application: CombatApplication.Confirmed)),
            Source("flat", new CombatEffect("HPMax", 100, Application: CombatApplication.Confirmed)),
            Source("percent", new CombatEffect("MaxHPRatio", 10, Application: CombatApplication.Confirmed)));
        var result = CombatScoreModel.Evaluate(graph, catalog);
        Assert.Equal(220m, result.ModelTotal);
        Assert.Equal(100m, result.Sources.Single(row => row.SourceId == "flat").ModelLoss);
        Assert.Equal(20m, result.Sources.Single(row => row.SourceId == "percent").ModelLoss);
        Assert.Equal(220m, result.Sources.Sum(row => row.ModelLoss));
        var replacement = CombatScoreModel.Counterfactual(graph, ["flat"],
            [Source("flat", new CombatEffect("HPMax", 50, Application: CombatApplication.Confirmed))], catalog);
        Assert.Equal(-50m, replacement.ModelDelta);
    }

    [Fact]
    public void DirectPercentIncreaseWithoutWeightedModifierStaysUnknown()
    {
        var graph = Graph(Source("base", new CombatEffect("HPMax", 100, Application: CombatApplication.Confirmed)),
            Source("percent", new CombatEffect("HPMax", 10, CombatEffectOperation.IncreasePercent, CombatApplication.Confirmed)));
        Assert.Null(CombatScoreModel.Evaluate(graph, Catalog()).ModelTotal);
        Assert.Equal(220m, CombatScoreModel.Evaluate(graph, RatioCatalog(1, 12)).ModelTotal); // 100 + 10 % × 12
    }

    [Fact]
    public void DifferenceIsRoundedAfterSubtractingUnroundedWholeModelValues()
    {
        var catalog = Catalog(0.15m);
        var graph = Graph(Source("base", new CombatEffect("HPMax", 3, Application: CombatApplication.Confirmed)),
            Source("gain", new CombatEffect("HPMax", 1, Application: CombatApplication.Confirmed)));
        var result = CombatScoreModel.Evaluate(graph, catalog);
        Assert.Equal(1m, result.ModelTotal); // round(.60)
        Assert.Equal(0m, result.Sources.Single(row => row.SourceId == "gain").ModelLoss); // round(.60-.45), not 1-0.
        Assert.Equal(0.60m, CombatScoreModel.Calculate(graph, catalog).Total);
    }

    [Fact(Skip = PendingOwnWeights.Reason)]
    public void AdditiveSkillSourcesDriveOneSkillCurveAndOnePassiveCurve()
    {
        var passive = new CombatPassiveRule { SkillId = 1, EffectId = "hp", StatId = "HPMax", Start = 200,
            Step = 200, Application = CombatApplication.Confirmed };
        var catalog = Catalog(passives: [passive]);
        var graph = Graph(SkillSource("original", 14), SkillSource("gear", 1));
        var score = CombatScoreModel.Calculate(graph, catalog);
        Assert.Equal(4500m, score.Total); // 15*200 HP + passive F(15)=1500.
        var report = CombatScoreModel.Evaluate(graph, catalog).Sources.Single(row => row.SourceId == "gear");
        Assert.Equal(400m, report.ModelLoss); // 200 HP + passive F(15)-F(14)=200.
        var native = graph with { NativeSkillTotals = [new(1, 15)], NativeTotals = [new(10, 300000)] };
        var evaluated = CombatScoreModel.Evaluate(native, catalog);
        Assert.Equal(4500m, evaluated.ModelTotal); // Native total is not a second source.
        Assert.Equal(0m, Assert.Single(evaluated.NativeComparisons).Difference);
        Assert.Equal(0, Assert.Single(evaluated.SkillComparisons).Difference);
        var partial = CombatScoreModel.Evaluate(graph with { Status = CombatKnowledgeStatus.Partial }, catalog);
        Assert.Equal(2, partial.Sources.Sum(source => source.Skills.Count));
        Assert.All(partial.Sources, source =>
        {
            Assert.Null(source.ModelLoss); Assert.Empty(source.StatContributions);
            Assert.Contains("complete character source coverage", source.Reason);
        });
        Assert.Equal(15, Assert.Single(partial.Skills).Level);
    }

    [Fact(Skip = PendingOwnWeights.Reason)]
    public void PositiveFreshAbnormalWitnessQualifiesActivationWithoutInventingALevelEquality()
    {
        var passive = new CombatPassiveRule { SkillId = 1, EffectId = "hp", StatId = "HPMax", Start = 200,
            Step = 200, AbnormalId = 99, HasUnconditionalPassiveChain = true, Application = CombatApplication.Unknown };
        var catalog = Catalog(passives: [passive]);
        var graph = Graph(SkillSource("original", 2));
        Assert.Null(CombatScoreModel.Calculate(graph, catalog).Total);
        graph = graph with { ActiveAbnormalStatus = CombatKnowledgeStatus.Partial, ActiveAbnormals = [new(99, 1, false)] };
        Assert.True(CombatStatDerivation.IsPassiveApplicable(graph, passive));
        Assert.Equal(500m, CombatScoreModel.Calculate(graph, catalog).Total);
        Assert.Null(CombatScoreModel.Calculate(graph with { ActiveAbnormalStatus = CombatKnowledgeStatus.Stale }, catalog).Total);
        Assert.Null(CombatScoreModel.Calculate(graph with { ActiveAbnormals = [new(99, 1, true)] }, catalog).Total);
        Assert.Null(CombatScoreModel.Calculate(graph with { ActiveAbnormals = [new(99, 1, false)] },
            Catalog(passives: [passive with { HasUnconditionalPassiveChain = false }])).Total);
        Assert.Equal(0m, CombatScoreModel.Calculate(Graph(SkillSource("zero", 0)), catalog).Total);
    }

    [Fact]
    public void PrimaryLookupAndRatioRelationsPrecedeTerminalRounding()
    {
        var catalog = new CombatEffectCatalog("test", 1,
            [new() { StatId = "CON" }, new() { StatId = "MaxHPRatio", Unit = CombatStatUnit.Percent, RawDivisor = 100 },
                new() { StatId = "HPMax", Rounding = CombatRounding.Ceiling }],
            [new("HPMax", 1, CombatStatUnit.Points), new("MaxHPRatio", .11m, CombatStatUnit.Percent)],
            [new("CON", [new(1, [new("MaxHPRatio", 750)])])],
            statRelations: [new("MaxHPRatio", "HPMax")]);
        var graph = Graph(Source("base", new CombatEffect("HPMax", 100.1m, Application: CombatApplication.Confirmed)),
            Source("god", new CombatEffect("CON", 1, Application: CombatApplication.Confirmed)));
        var result = CombatScoreModel.Evaluate(graph, catalog);
        // Raw-linear: 100.1 flat × 1 + 7.5 % × 0.11; the displayed ceil(100.1 × 1.075) is not weighted.
        Assert.Equal(101m, result.ModelTotal);
        Assert.Equal(100.925m, result.Composition.UnroundedSubtotal);
        var con = Assert.Single(result.Composition.Stats, term => term.StatId == "CON");
        Assert.True(con.IsIntermediate); Assert.Null(con.Coefficient); Assert.Null(con.Contribution);
        Assert.Equal(0.825m, Assert.Single(result.Composition.Stats, term => term.StatId == "MaxHPRatio").Contribution);
        Assert.Equal(100.1m, Assert.Single(result.Composition.Stats, term => term.StatId == "HPMax").Contribution);
        Assert.Equal(7.5m, result.Stats.Single(row => row.StatId == "HPMax").IncreasePercent);
        Assert.Equal(100.1m, CombatScoreModel.Calculate(graph with
            { Sources = [graph.Sources[0], Source("god", new CombatEffect("CON", 0, Application: CombatApplication.Confirmed))] }, catalog).Total);
        var outside = graph with { Sources = [graph.Sources[0], Source("god", new CombatEffect("CON", 2, Application: CombatApplication.Confirmed))] };
        Assert.Null(CombatScoreModel.Calculate(outside, catalog).Total); // No extrapolation beyond an exact lookup.
    }

    [Fact]
    public void UnknownMissingStaleAndInactiveHaveDifferentMeanings()
    {
        var unknown = Graph(Source("base", new CombatEffect("HPMax", 100, Application: CombatApplication.Confirmed)),
            Source("unqualified", new CombatEffect("HPMax", 20)));
        Assert.Null(CombatScoreModel.Evaluate(unknown, Catalog()).ModelTotal);
        var inactive = unknown with { Sources = [unknown.Sources[0], Source("inactive", new CombatEffect("HPMax", 20, Application: CombatApplication.Inactive))] };
        Assert.Equal(100m, CombatScoreModel.Evaluate(inactive, Catalog()).ModelTotal);
        var partial = inactive with { Sources = [inactive.Sources[0], new() { Id = "missing", Status = CombatKnowledgeStatus.Unknown }] };
        var evaluation = CombatScoreModel.Evaluate(partial, Catalog());
        Assert.Null(evaluation.ModelTotal); Assert.Equal(100m, evaluation.KnownSubtotal); Assert.NotEmpty(evaluation.MissingRequirements);
        Assert.Null(CombatScoreModel.Calculate(partial, Catalog()).Total);
        Assert.Null(evaluation.Sources.Single(row => row.SourceId == "base").ModelLoss);
        Assert.Equal(100m, Assert.Single(evaluation.Sources.Single(row => row.SourceId == "base").Effects).Value);
        Assert.All(evaluation.Sources, source => Assert.Empty(source.StatContributions));
        Assert.Null(CombatScoreModel.Counterfactual(partial, ["base"], [], Catalog()).ModelDelta);
        Assert.Null(evaluation.Sources.Single(row => row.SourceId == "missing").ModelLoss);
        Assert.Throws<ArgumentException>(() => CombatScoreModel.Freeze(inactive with { Sources = [inactive.Sources[0], inactive.Sources[0]] }));
        var frozen = CombatScoreModel.Freeze(inactive);
        Assert.Throws<NotSupportedException>(() => ((IList<CombatSource>)frozen.Sources).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CombatEffect>)frozen.Sources[0].Effects).Clear());
    }

    [Fact]
    public void BuilderSeparatesOwnedEquippedAndDisplayedTitleAndDoesNotInventAbsentBoards()
    {
        var catalog = Catalog(sources:
            [new() { SourceKind = CombatSourceKind.TitleOwned, SourceId = "10", Channel = "Owned", Application = CombatApplication.Confirmed,
                Effects = [new("HPMax", 100)] },
             new() { SourceKind = CombatSourceKind.TitleEquipped, SourceId = "10", Channel = "Equipped", Application = CombatApplication.Confirmed,
                Effects = [new("HPMax", 200)] }]);
        var observation = new CombatObservation { ClassId = "Cleric", Titles = new(new(
            ImmutableHashSet.Create(10), ImmutableDictionary<byte, int>.Empty.Add(1, 10)), CombatKnowledgeStatus.Complete) };
        var graph = CombatSourceGraphBuilder.Build(observation, catalog);
        Assert.Equal(100m, Assert.Single(graph.Sources.Single(row => row.Id == "title-owned:10").Effects).Value);
        Assert.Equal(200m, Assert.Single(graph.Sources.Single(row => row.Id == "title-equipped:10").Effects).Value);
        Assert.Equal(6, graph.Sources.Count(row => row.Id.StartsWith("daevanion:", StringComparison.Ordinal)
            && int.TryParse(row.Id["daevanion:".Length..], out _) && row.Status == CombatKnowledgeStatus.Unknown));
        Assert.Equal(CombatKnowledgeStatus.Partial, graph.Status);
    }

    [Fact]
    public void CharacterTitleOwnershipAndEquippedSlotsUseIndependentKnowledgeWithoutDoubleCounting()
    {
        var catalog = Catalog(sources:
            [new() { SourceKind = CombatSourceKind.TitleOwned, SourceId = "10", Channel = "Owned", Application = CombatApplication.Confirmed,
                Effects = [new("HPMax", 100)] },
             new() { SourceKind = CombatSourceKind.TitleEquipped, SourceId = "10", Channel = "Equipped", Application = CombatApplication.Confirmed,
                Effects = [new("HPMax", 200)] }]);
        var observation = new CombatObservation
        {
            ClassId = "Cleric", TitleOwnership = new(ImmutableHashSet.Create(10), CombatKnowledgeStatus.Complete),
            Titles = new(new(ImmutableHashSet.Create(10, 11), ImmutableDictionary<byte, int>.Empty.Add(1, 10)), CombatKnowledgeStatus.Stale)
        };
        var graph = CombatSourceGraphBuilder.Build(observation, catalog);
        Assert.Equal(100m, Assert.Single(graph.Sources.Single(row => row.Id == "title-owned:10").Effects).Value);
        Assert.DoesNotContain(graph.Sources, row => row.Id == "title-owned:11" || row.Id == "title-equipped:10");
        Assert.Equal(CombatKnowledgeStatus.Stale, graph.Sources.Single(row => row.Id == "title-equipped:unknown").Status);

        observation = observation with { TitleOwnership = new(null, CombatKnowledgeStatus.Unknown),
            Titles = observation.Titles with { Status = CombatKnowledgeStatus.Complete } };
        graph = CombatSourceGraphBuilder.Build(observation, catalog);
        Assert.Equal(CombatKnowledgeStatus.Unknown, graph.Sources.Single(row => row.Id == "title-owned:unknown").Status);
        Assert.DoesNotContain(graph.Sources, row => row.Id == "title-owned:10" || row.Id == "title-owned:11");
        Assert.Equal(200m, Assert.Single(graph.Sources.Single(row => row.Id == "title-equipped:10").Effects).Value);

        observation = observation with { TitleOwnership = new(ImmutableHashSet<int>.Empty, CombatKnowledgeStatus.Complete) };
        graph = CombatSourceGraphBuilder.Build(observation, catalog);
        Assert.Equal(CombatKnowledgeStatus.Complete, graph.Sources.Single(row => row.Id == "title-owned:empty").Status);
        Assert.DoesNotContain(graph.Sources, row => row.Id == "title-owned:10" || row.Id == "title-owned:11");
    }

    [Fact]
    public void BuilderKeepsRawAbnormalLevelAsWitnessButDoesNotUseItAsTheEffectAmount()
    {
        var observation = new CombatObservation { ActiveAbnormals = new(new CharacterAbnormal[]
            { new(99, 1, false), new(100, 2, true) }, CombatKnowledgeStatus.Complete) };
        var graph = CombatSourceGraphBuilder.Build(observation, Catalog());
        Assert.Equal(new[] { 99 }, graph.ActiveAbnormalIds);
        Assert.Equal(2, graph.ActiveAbnormals.Count);
        Assert.Equal(1, graph.ActiveAbnormals[0].Level);
    }

    [Fact]
    public void PartiallyObservedBoardsDoNotDoubleCountTheKnownNodesAndObservedTotalBonus()
    {
        var board = DaevanionRuleCatalog.Default.ForClass("Cleric").First();
        var node = board.Nodes.First(row => row.Node.Effects.Any(effect => effect.SkillId is not null));
        var skillId = node.Node.Effects.First(effect => effect.SkillId is not null).SkillId!.Value;
        var skill = DaevanionCatalog.Default.GetSkill(skillId)!;
        var catalog = new CombatEffectCatalog("test", 1, [], skills: [new(skillId, "Cleric", skill.Kind)]);
        var observation = new CombatObservation
        {
            ClassId = "Cleric", Skills = new(new OwnSkillLevel[] { new(skillId, 16, 14, new(2, 0, 0, 0, 0)) }, CombatKnowledgeStatus.Complete),
            Boards = new(new(ImmutableDictionary<int, ImmutableHashSet<int>>.Empty.Add(board.Board.Id, ImmutableHashSet.Create(node.Node.Id))), CombatKnowledgeStatus.Partial)
        };
        var graph = CombatSourceGraphBuilder.Build(observation, catalog);
        Assert.Equal(16, Assert.Single(CombatStatDerivation.Derive(graph, catalog).Skills).Level);
        Assert.Equal(1, Assert.Single(graph.Sources.Single(row => row.Id == "daevanion:unresolved-skill-bonuses").Skills).Level);
    }

    [Fact]
    public void PetAndGenusUseExactLevelsAndOnlyTheSelectedActivatedPanel()
    {
        var catalog = new CombatEffectCatalog("test", 1,
            [new() { StatId = "HPMax", NativeId = 10, NativeRawDivisor = 100, NativeRole = CombatNativeStatRole.DisplayTotal,
                NativeDomains = [new(CombatNativeValueDomain.GenusEffects, 100, CombatNativeStatRole.FlatComponent)], Rounding = CombatRounding.Ceiling }],
            [new("HPMax", 1, CombatStatUnit.Points)], sourceEffects:
            [new() { SourceKind = CombatSourceKind.Pet, SourceId = "pet-row", Channel = "Collection", Effects = [new("HPMax", 50)] }],
            petLevelBindings: [new(1, 1, "pet-row", CombatApplication.Confirmed)]);
        var panels = ImmutableDictionary<byte, ImmutableArray<CharacterGenusEffect>>.Empty
            .Add(0, [new(1, 1, 10, 90000, false, true)])
            .Add(1, [new(1, 1, 10, 10000, false, true), new(2, 1, 10, 70000, false, false)]);
        var pets = new PetProgressSnapshot(ImmutableDictionary<int, int>.Empty.Add(1, 1), ImmutableDictionary<int, int>.Empty)
        {
            SelectedGenusPanel = 1, GenusBoards = ImmutableDictionary<byte, CharacterGenusBoard>.Empty.Add(1, new(1, 1, 0, panels)),
            AcquiredCollections = ImmutableHashSet.Create(100001)
        };
        var graph = CombatSourceGraphBuilder.Build(new() { Pets = new(pets, CombatKnowledgeStatus.Complete) }, catalog);
        var stat = Assert.Single(CombatStatDerivation.Derive(graph, catalog).Stats);
        Assert.Equal(150m, stat.Value);
        Assert.Equal(100m, Assert.Single(graph.Sources.Single(row => row.Id == "genus:1").Effects).Value);
        Assert.Single(graph.Sources, row => row.Kind == CombatSourceKind.Pet);
        var zero = pets with { Levels = ImmutableDictionary<int, int>.Empty.Add(1, 0) };
        var zeroGraph = CombatSourceGraphBuilder.Build(new() { Pets = new(zero, CombatKnowledgeStatus.Complete) }, catalog);
        Assert.Empty(zeroGraph.Sources.Single(row => row.Id == "pet:1:level:0").Effects);
        Assert.Equal(CombatKnowledgeStatus.Partial, zeroGraph.Sources.Single(row => row.Id == "pet:1:level:0").Status);
    }

    [Fact]
    public void UnmappedObservedSkillsRemainAnExplicitCoverageRequirement()
    {
        var observation = new CombatObservation { ClassId = "Cleric", Skills = new(new OwnSkillLevel[]
            { new(999999, 1, 1, new(0, 0, 0, 0, 0)) }, CombatKnowledgeStatus.Complete) };
        var graph = CombatSourceGraphBuilder.Build(observation, Catalog());
        Assert.Equal(CombatKnowledgeStatus.Unknown, graph.Sources.Single(source => source.Id == "skills:unmapped:999999").Status);
        Assert.Contains(CombatScoreModel.Evaluate(graph, Catalog()).MissingRequirements,
            requirement => requirement.Contains("999999", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeRolesAndScalesAreDomainBoundAndRawComponentsAreNeverComparedAsTotals()
    {
        var catalog = new CombatEffectCatalog("fictional-domain-roles", 1,
            [new() { StatId = "HPMax", NativeId = 10, NativeRawDivisor = 100, NativeRole = CombatNativeStatRole.FlatComponent,
                NativeDomains = [new(CombatNativeValueDomain.EquipmentOptions, 1000, CombatNativeStatRole.FlatComponent)] },
             new() { StatId = "MaxHPRatio", NativeId = 11, NativeRawDivisor = 100, NativeRole = CombatNativeStatRole.IncreasePercentComponent,
                Unit = CombatStatUnit.Percent }], [new("HPMax", 1, CombatStatUnit.Points)]);
        Assert.Equal(100m, catalog.GetNativeNormalized(10, 10000)!.Value);
        Assert.Equal(10m, catalog.GetNativeNormalized(10, 10000, CombatNativeValueDomain.EquipmentOptions)!.Value);
        Assert.Null(catalog.GetNativeNormalized(10, 10000, CombatNativeValueDomain.GenusEffects));
        Assert.Null(catalog.NormalizeNative(11, 1000, CombatNativeValueDomain.EquipmentOptions));
        var graph = Graph(Source("base", new CombatEffect("HPMax", 100, Application: CombatApplication.Confirmed)),
            Source("increase", new CombatEffect("HPMax", 10, CombatEffectOperation.IncreasePercent, CombatApplication.Confirmed))) with
        {
            NativeTotals = [new(10, 10000), new(11, 1000), new(10, 10000, CombatNativeValueDomain.EquipmentOptions)]
        };
        var result = CombatScoreModel.Evaluate(graph, catalog);
        var flat = Assert.Single(result.NativeComparisons, row => row.NativeStatId == 10 && row.Domain == CombatNativeValueDomain.CharacterStats);
        Assert.Equal(100m, flat.NormalizedComponent); Assert.Equal(100m, flat.NativeFlat); Assert.Equal(110m, flat.ModelValue);
        Assert.Equal(CombatNativeStatRole.FlatComponent, flat.Role);
        var ratio = Assert.Single(result.NativeComparisons, row => row.NativeStatId == 11);
        Assert.Equal(10m, ratio.NativeIncreasePercent); // Basispoint normalization yields percentage points, never a second /100.
        Assert.All(result.NativeComparisons, row => { Assert.Null(row.NativeValue); Assert.Null(row.Difference); });
        Assert.Empty(catalog.NativeForwardRules);
    }

    [Fact]
    public void AScaleWithoutARoleIsNotAQualifiedDisplayTotalOrSourceEffect()
    {
        var catalog = new CombatEffectCatalog("fictional-unknown-role", 1,
            [new() { StatId = "HPMax", NativeId = 10, NativeRawDivisor = 100,
                NativeDomains = [new(CombatNativeValueDomain.GenusEffects, 1)] }], [new("HPMax", 1, CombatStatUnit.Points)]);
        Assert.Equal(CombatNativeStatRole.Unknown, catalog.GetNativeNormalized(10, 10000)!.Role);
        var graph = Graph(Source("base", new CombatEffect("HPMax", 100, Application: CombatApplication.Confirmed))) with { NativeTotals = [new(10, 10000)] };
        var comparison = Assert.Single(CombatScoreModel.Evaluate(graph, catalog).NativeComparisons);
        Assert.Equal(100m, comparison.NormalizedComponent); Assert.Null(comparison.NativeValue); Assert.Null(comparison.Difference);
        var panels = ImmutableDictionary<byte, ImmutableArray<CharacterGenusEffect>>.Empty.Add(0, [new(1, 1, 10, 100, false, true)]);
        var pets = new PetProgressSnapshot(ImmutableDictionary<int, int>.Empty, ImmutableDictionary<int, int>.Empty)
        {
            SelectedGenusPanel = 0, GenusBoards = ImmutableDictionary<byte, CharacterGenusBoard>.Empty.Add(1, new(1, 1, 0, panels))
        };
        var own = CombatSourceGraphBuilder.Build(new() { Pets = new(pets, CombatKnowledgeStatus.Complete) }, catalog);
        Assert.Empty(own.Sources.Single(row => row.Id == "genus:1").Effects);
        Assert.Equal(CombatKnowledgeStatus.Partial, own.Sources.Single(row => row.Id == "genus:1").Status);
    }

    [Fact]
    public void NativeNormalPointCurveAndTheObservedBudgetContractHaveDifferentQualificationScopes()
    {
        var catalog = CombatEffectCatalog.Default;
        Assert.Equal(100, catalog.NativeExpPointLevels.Count);
        Assert.Equal(0, catalog.NormalBasePointsAt(1)); Assert.Equal(0, catalog.NormalBasePointsAt(11));
        Assert.Equal(4, catalog.NormalBasePointsAt(12)); Assert.Equal(132, catalog.NormalBasePointsAt(44));
        Assert.Equal(136, catalog.NormalBasePointsAt(45)); Assert.Equal(140, catalog.NormalBasePointsAt(46));
        Assert.Equal(356, catalog.NormalBasePointsAt(100));
        Assert.Null(catalog.NormalBasePointsAt(0)); Assert.Null(catalog.NormalBasePointsAt(101));
        Assert.Equal("exp-map-26262663-v1", catalog.NativeExpPointQualification.Version);
        Assert.Equal("normal-budget-26262663-level45-v1", catalog.NativeExpPointQualification.NormalBudgetContractVersion);
        Assert.Equal(new[] { 45 }, catalog.NativeExpPointQualification.NormalBudgetQualifiedLevels);
        Assert.Equal("special-budget-26262663-level45-v1", catalog.NativeExpPointQualification.SpecialBudgetContractVersion);
        Assert.Equal(new[] { 45 }, catalog.NativeExpPointQualification.SpecialBudgetQualifiedLevels);
        Assert.Equal(CombatKnowledgeStatus.Complete, catalog.NativeExpPointQualification.SpecialPoolsStatus);
        Assert.Equal(CombatBudgetContractStatus.QualifiedBoundedInitialGrowth45, catalog.NativeExpPointQualification.BudgetContractStatus);
        Assert.Equal(26262663, catalog.NativeExpPointQualification.ClientBuild);
        Assert.Equal(1, catalog.NativeExpPointQualification.RequiredCombatLevel);
        Assert.Equal(0, catalog.NativeExpPointQualification.RequiredConquerorLevel);
        Assert.False(catalog.NativeExpPointQualification.IsNativeFreeWallet);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)catalog.NativeExpPointQualification.NormalBudgetQualifiedLevels).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<int>)catalog.NativeExpPointQualification.SpecialBudgetQualifiedLevels).Clear());
    }

    [Fact]
    public void BoundedPointQualificationCannotBeReusedWithOtherOrUnknownGrowthScopes()
    {
        var qualification = CombatEffectCatalog.Default.NativeExpPointQualification;
        foreach (var invalid in new[] { qualification with { RequiredCombatLevel = 2 }, qualification with { RequiredCombatLevel = null },
            qualification with { RequiredConquerorLevel = 1 }, qualification with { ClientBuild = 1 },
            qualification with { NormalBudgetQualifiedLevels = [44, 45] }, qualification with { SpecialBudgetQualifiedLevels = [44, 45] },
            qualification with { IsNativeFreeWallet = true } })
            Assert.Throws<InvalidDataException>(() => new CombatEffectCatalog("fictional-bad-scope", 26262663, [],
                nativeExpPointLevels: [new(44, 132), new(45, 136)], nativeExpPointQualification: invalid));
    }

    [Fact(Skip = PendingOwnWeights.Reason)]
    public void ControlledNativeAttackMappingIsOnlyACharacterFlatComponentAndDoesNotFitTheScoreModel()
    {
        var catalog = CombatEffectCatalog.Default;
        var definition = Assert.Single(catalog.StatDefinitions, stat => stat.NativeId is not null);
        Assert.Equal("FixingDamage", definition.StatId); Assert.Equal(19, definition.NativeId);
        var normalized = catalog.GetNativeNormalized(19, 17);
        Assert.NotNull(normalized); Assert.Equal(17m, normalized.Value);
        Assert.Equal(CombatNativeValueDomain.CharacterStats, normalized.Domain);
        Assert.Equal(CombatNativeStatRole.FlatComponent, normalized.Role);
        Assert.Null(catalog.GetNativeNormalized(19, 17, CombatNativeValueDomain.EquipmentOptions));
        Assert.Null(catalog.GetNativeNormalized(19, 17, CombatNativeValueDomain.GenusEffects));
        var graph = Graph(Source("fictional-attack-source", new CombatEffect("FixingDamage", 17, Application: CombatApplication.Confirmed)))
            with { NativeTotals = [new(19, 17)] };
        var comparison = Assert.Single(CombatScoreModel.Evaluate(graph, catalog).NativeComparisons);
        Assert.Equal(17m, comparison.NormalizedComponent); Assert.Equal(17m, comparison.NativeFlat);
        Assert.Null(comparison.NativeValue); Assert.Null(comparison.Difference);
        Assert.Equal(30m, CombatScoreModel.Rules.Single(rule => rule.StatId == "FixingDamage").Coefficient);
        Assert.Empty(catalog.NativeForwardRules);
    }

    private static CombatEffectCatalog Catalog(decimal coefficient = 1, IReadOnlyList<CombatPassiveRule>? passives = null,
        IReadOnlyList<CombatCatalogSource>? sources = null) => new("test", 1,
        [new() { StatId = "HPMax", Unit = CombatStatUnit.Points, Rounding = CombatRounding.Ceiling, NativeId = 10, NativeRawDivisor = 100,
            NativeRole = CombatNativeStatRole.DisplayTotal }],
        [new("HPMax", coefficient, CombatStatUnit.Points)], passiveEffects: passives, sourceEffects: sources);
    private static CombatEffectCatalog RatioCatalog(decimal hpWeight, decimal ratioWeight) => new("test", 1,
        [new() { StatId = "HPMax", Unit = CombatStatUnit.Points, Rounding = CombatRounding.Ceiling },
            new() { StatId = "MaxHPRatio", Unit = CombatStatUnit.Percent, RawDivisor = 100 }],
        [new("HPMax", hpWeight, CombatStatUnit.Points), new("MaxHPRatio", ratioWeight, CombatStatUnit.Percent)],
        statRelations: [new("MaxHPRatio", "HPMax")]);
    private static CombatSourceGraph Graph(params CombatSource[] sources) => new() { Status = CombatKnowledgeStatus.Complete, Sources = sources };
    private static CombatSource Source(string id, params CombatEffect[] effects) => new() { Id = id, Status = CombatKnowledgeStatus.Complete, Effects = effects };
    private static CombatSource SkillSource(string id, int level) => new() { Id = id, Status = CombatKnowledgeStatus.Complete,
        Skills = [new(1, DaevanionSkillKind.Passive, level, CombatApplication.Confirmed)] };
}
