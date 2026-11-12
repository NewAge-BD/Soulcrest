using Soulcrest.App.Components;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatStatOverviewCatalogTests
{
    [Fact]
    public void StableSectionsAndRequestedMainOrderAreIndependentOfProjectionOrdering()
    {
        Assert.Equal(new[] { "primary-attributes", "god-attributes", "main-stats" },
            CombatStatOverviewCatalog.MainGroups.Select(group => group.Key));
        Assert.Equal(new[] { "combat", "hit-detection", "status-effects", "pvp", "pve", "genus", "elements", "special", "resources" },
            CombatStatOverviewCatalog.SubGroups.Select(group => group.Key));
        Assert.Equal(new[] { "Might", "Dexterity", "Precision", "Willpower", "Intelligence", "Constitution" },
            Group(CombatStatOverviewGroup.PrimaryAttributes).Rows.Select(row => row.Id));
        Assert.Equal(new[] { "Attack", "Accuracy", "Critical", "HPMax", "CombatSpeed", "Defense", "Evasion", "CriticalResist", "MPMax", "MoveSpeed" },
            Group(CombatStatOverviewGroup.MainStats).Rows.Select(row => row.Id));
        var groups = CombatStatOverviewCatalog.MainGroups.Concat(CombatStatOverviewCatalog.SubGroups).ToArray();
        var rows = groups.SelectMany(group => group.Rows).ToArray();
        Assert.Equal(rows.Length, rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(groups, group => Assert.All(group.Rows, row => Assert.Equal(group.Category, row.Category)));
        Assert.All(rows, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.LabelDe)); Assert.False(string.IsNullOrWhiteSpace(row.LabelEn));
            Assert.DoesNotContain('\uFFFD', row.LabelDe); Assert.DoesNotContain('\uFFFD', row.LabelEn);
            Assert.Equal(row.Id, row.IconKey);
        });
    }

    [Fact]
    public void GodRosterBindsExactlyTenQualifiedOutputsAndKeepsLightAndDarkUnknown()
    {
        var gods = Group(CombatStatOverviewGroup.GodAttributes).Rows;
        AssertSet(new[] { "Justice", "Freedom", "Illusion", "Life", "Time", "Light", "Destruction", "Death", "Wisdom", "Destiny", "Space", "Dark" }, gods.Select(row => row.Id));
        Assert.Equal("Darkness", gods.Single(row => row.Id == "Dark").LabelEn);
        Assert.Equal("Licht", gods.Single(row => row.Id == "Light").LabelDe);
        Assert.All(gods, god =>
        {
            Assert.DoesNotContain('[', god.LabelEn); Assert.DoesNotContain('[', god.LabelDe);
            Assert.Equal(god.Id, god.IconKey); Assert.Equal(CombatStatUnit.Points, god.Unit);
            Assert.Null(CombatStatOverviewCatalog.Resolve(god, null).Value);
            Assert.Null(CombatStatOverviewCatalog.Resolve(god, new()).Value);
        });
        Assert.Equal(new[] { "Justice", "Freedom", "Illusion", "Life", "Time", "Destruction", "Death", "Wisdom", "Destiny", "Space" },
            gods.Where(god => god.OutputId is not null).Select(god => god.Id));
        Assert.All(gods.Where(god => god.OutputId is not null), god => Assert.Equal(god.Id, god.OutputId));
        Assert.All(gods.Where(god => god.Id is "Light" or "Dark"), god =>
        {
            Assert.Null(god.OutputId);
            var unrelated = new CharacterDisplayStats { Rows = [new() { Id = god.Id, Value = 123,
                Status = CombatKnowledgeStatus.Complete, Qualification = CharacterDisplayStatQualification.NativeDirect }] };
            var value = CombatStatOverviewCatalog.Resolve(god, unrelated);
            Assert.Null(value.Value); Assert.Equal(CombatKnowledgeStatus.Unknown, value.Status);
            Assert.Equal(CharacterDisplayStatQualification.Unknown, value.Qualification);
        });
    }

    [Fact]
    public void ActiveRosterHidesOnlyUnavailableLightAndDarkWhileRetainingTheirMetadata()
    {
        var all = CombatStatOverviewCatalog.MainGroups.Concat(CombatStatOverviewCatalog.SubGroups).SelectMany(group => group.Rows).ToArray();
        var active = CombatStatOverviewCatalog.ActiveMainGroups.Concat(CombatStatOverviewCatalog.ActiveSubGroups).SelectMany(group => group.Rows).ToArray();
        Assert.Equal(197, all.Length); Assert.Equal(195, active.Length);
        Assert.Equal(new[] { "Light", "Dark" }, all.Where(row => !row.IsVisible).Select(row => row.Id));
        Assert.Equal(all.Where(row => row.IsVisible), active);
        Assert.All(all.Where(row => row.Id is "Light" or "Dark"), row =>
        {
            Assert.False(row.IsVisible); Assert.Null(row.OutputId);
            Assert.Null(CombatStatOverviewCatalog.Resolve(row, null).Value);
        });
        Assert.Contains(active, row => row.Id == "Intelligence" && row.IsVisible);
        Assert.Contains(active, row => row.Id == "FrontAttackDamage" && row.IsVisible && row.OutputId == "FrontAttackDamage");
        var gods = CombatStatOverviewCatalog.ActiveMainGroups.Single(group => group.Group == CombatStatOverviewGroup.GodAttributes);
        Assert.Equal(10, gods.Rows.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<CombatStatOverviewRowDefinition>)gods.Rows).Clear());
    }

    [Fact]
    public void CompleteStatusRosterContainsEachNamedChanceAndResistancePair()
    {
        var types = new[] { "Stun", "KnockdownInstant", "Aerial", "Bind", "Fear", "Sleep", "Polymorph", "Taunt",
            "BlockActive", "Frozen", "Paralysis", "Hold", "Blind", "Slow", "Snare", "Poison", "Bleed" };
        AssertSet(types.SelectMany(type => new[] { type + "Accuracy", type + "Resist" }),
            Group(CombatStatOverviewGroup.StatusEffects).Rows.Select(row => row.Id));
        Assert.All(Group(CombatStatOverviewGroup.StatusEffects).Rows, row =>
        {
            Assert.Equal(CombatStatUnit.Percent, row.Unit);
            Assert.Null(row.OutputId); Assert.Null(CombatStatOverviewCatalog.Resolve(row, null).Value);
        });
    }

    [Fact]
    public void AllGenusAndElementFamiliesContainTheirExplicitStatSets()
    {
        var genus = new[] { "Intellect", "Feral", "Nature", "Trans" };
        var genusStats = new[] { "Damage", "Defense", "Accuracy", "Evasion", "Critical", "CriticalResist", "BlockPierce", "Block", "AmplifyDamage", "DecreaseDamage" };
        AssertSet(genus.SelectMany(type => genusStats.Select(stat => type + stat)),
            Group(CombatStatOverviewGroup.Genus).Rows.Select(row => row.Id));
        AssertSet(new[] { "Water", "Fire", "Wind", "Earth" }.SelectMany(element =>
                new[] { "AddDamage" + element, element + "DamageDefense", "Amplify" + element, "Resist" + element }),
            Group(CombatStatOverviewGroup.Elements).Rows.Select(row => row.Id));
        AssertSet(new[] { "PvPAddDamage", "PvPDamageDefense", "PvPAccuracy", "PvPEvasion", "PvPCritical", "PvPCriticalResist",
                "PvPBlockPierce", "PvPBlock", "PvPAmplifyDamage", "PvPDecreaseDamage" },
            Group(CombatStatOverviewGroup.PvP).Rows.Select(row => row.Id));
    }

    [Fact]
    public void ResourceRosterIncludesSnapshotRageRowsWhileKeepingTheirDetectionUnqualified()
    {
        var rows = Group(CombatStatOverviewGroup.Resources).Rows;
        AssertSet(new[] { "SPMax", "FPMax", "CombatHPRegen", "RestHPRegen", "HpPotionRegen", "HpPotionRate",
            "CombatMPRegen", "RestMPRegen", "BattleAPRegen", "RestAPRegen", "MPUseIncrease", "HealCostMpRate",
            "HealCostAPRate", "BattleSPRegen", "RestSPRegen", "BattleFPRegen", "RestFPRegen" }, rows.Select(row => row.Id));
        foreach (var id in new[] { "BattleAPRegen", "RestAPRegen", "HealCostAPRate" })
        {
            var row = rows.Single(row => row.Id == id);
            Assert.Null(row.OutputId); Assert.Null(CombatStatOverviewCatalog.Resolve(row, new()).Value);
        }
        Assert.Equal(197, CombatStatOverviewCatalog.MainGroups.Sum(group => group.Rows.Count)
            + CombatStatOverviewCatalog.SubGroups.Sum(group => group.Rows.Count));
    }

    [Fact]
    public void EveryExistingProjectionOutputHasExactlyOnePresentationBinding()
    {
        var definitions = CombatStatOverviewCatalog.MainGroups.Concat(CombatStatOverviewCatalog.SubGroups)
            .SelectMany(group => group.Rows).Where(row => row.OutputId is not null).ToArray();
        AssertSet(CharacterDisplayStatProjection.Rules.Select(rule => rule.OutputId), definitions.Select(row => row.OutputId!));
        Assert.Equal(definitions.Length, definitions.Select(row => row.OutputId).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0, CombatKnowledgeStatus.Complete, CharacterDisplayStatQualification.ControlledChange)]
    [InlineData(23, CombatKnowledgeStatus.Stale, CharacterDisplayStatQualification.ObservedMatch)]
    [InlineData(19, CombatKnowledgeStatus.Conflict, CharacterDisplayStatQualification.Unknown)]
    public void ResolvingNeverChangesReceivedValueFreshnessOrFormulaEvidence(int value, CombatKnowledgeStatus status,
        CharacterDisplayStatQualification qualification)
    {
        var definition = Group(CombatStatOverviewGroup.MainStats).Rows.Single(row => row.Id == "Attack");
        var observed = new CharacterDisplayStatRow
        {
            Id = definition.OutputId!, Value = value, UnroundedValue = value + .5m, Status = status,
            Qualification = qualification, Category = CharacterDisplayStatCategory.Main,
            Source = "synthetic-source", Formula = "qualified input formula", RuleId = "synthetic-rule",
            Evidence = ["synthetic-evidence"], MissingInputIds = [123], Inputs = [new(123, null, 1)],
            ObservedAt = DateTimeOffset.UnixEpoch, ForwardValue = 50, ForwardReason = "synthetic-reason",
            ComparisonStatus = CombatKnowledgeStatus.Conflict, Reason = "input-reason"
        };
        var actual = CombatStatOverviewCatalog.Resolve(definition, new() { Rows = [observed] });
        Assert.Equal(observed with { LabelDe = definition.LabelDe, LabelEn = definition.LabelEn }, actual);
        Assert.Empty(observed.LabelDe); Assert.Equal(value, actual.Value);
    }

    [Fact]
    public void MissingProjectionDoesNotBecomeZeroAndMetadataBuildDoesNotGrantANativeBuildGate()
    {
        var definition = Group(CombatStatOverviewGroup.PrimaryAttributes).Rows.Single(row => row.Id == "Intelligence");
        Assert.Null(CombatStatOverviewCatalog.Resolve(definition, null).Value);
        Assert.Equal(CombatKnowledgeStatus.Unknown, CombatStatOverviewCatalog.Resolve(definition, new()).Status);
        var projection = CharacterDisplayStatProjection.Evaluate(new()
        {
            ClientBuild = CombatStatOverviewCatalog.MetadataSnapshotBuild, ClassId = "Cleric", Generation = 1,
            NativeStats = new(new Dictionary<ushort, int> { [193] = 1000, [196] = 500 },
                CombatKnowledgeStatus.Complete, true, 1, DateTimeOffset.UnixEpoch, "own-full-stat-list")
        });
        var hp = Group(CombatStatOverviewGroup.MainStats).Rows.Single(row => row.Id == "HPMax");
        var result = CombatStatOverviewCatalog.Resolve(hp, projection);
        Assert.Null(result.Value); Assert.Equal(CombatKnowledgeStatus.Unknown, result.Status);
        Assert.Equal(projection.Rows.Single(row => row.Id == "HPMax").Reason, result.Reason);
    }

    [Fact]
    public void DuplicateProjectionOutputsStayConflictingInsteadOfSelectingAnArbitraryValue()
    {
        var definition = Group(CombatStatOverviewGroup.MainStats).Rows.Single(row => row.Id == "HPMax");
        var result = CombatStatOverviewCatalog.Resolve(definition, new() { Rows =
            [new() { Id = "HPMax", Value = 100 }, new() { Id = "HPMax", Value = 200 }] });
        Assert.Null(result.Value); Assert.Equal(CombatKnowledgeStatus.Conflict, result.Status);
        Assert.Equal("AmbiguousDisplayOutput", result.Reason);
    }

    [Fact]
    public void SharedStaticGroupsAndRowsCannotBeMutatedByAView()
    {
        var groups = Assert.IsAssignableFrom<IList<CombatStatOverviewGroupDefinition>>(CombatStatOverviewCatalog.MainGroups);
        Assert.True(groups.IsReadOnly); Assert.Throws<NotSupportedException>(() => groups.RemoveAt(0));
        var rows = Assert.IsAssignableFrom<IList<CombatStatOverviewRowDefinition>>(groups[0].Rows);
        Assert.True(rows.IsReadOnly); Assert.Throws<NotSupportedException>(() => rows.RemoveAt(0));
    }

    private static CombatStatOverviewGroupDefinition Group(CombatStatOverviewGroup group) =>
        CombatStatOverviewCatalog.MainGroups.Concat(CombatStatOverviewCatalog.SubGroups).Single(value => value.Group == group);

    private static void AssertSet(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
}
