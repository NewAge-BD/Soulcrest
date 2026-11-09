using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementTitleTrackingResolverTests
{
    private static readonly AchievementProfileBinding Owner = new(new string('a', 32), new string('b', 32));

    [Fact]
    public void ExactNativeRoutesSelectOnlyObservedFamiliesAndNeverInventAnAlternative()
    {
        var resolver = Resolver();
        var none = Assert.Single(resolver.Resolve(Own(), [11])); Assert.Null(none.FamilyId);
        var one = Assert.Single(resolver.Resolve(Own(200), [11])); Assert.Equal(200, one.FamilyId);
        Assert.Equal(new int?[] { 100, 200 }, resolver.Resolve(Own(100, 200), [11]).Select(target => target.FamilyId));
        Assert.Null(Assert.Single(resolver.Resolve(Own(100), [12])).FamilyId); // Quest reference, not an achievement route.
        Assert.Null(Assert.Single(resolver.Resolve(Own(100), [13])).FamilyId); // Stage does not belong to source family.
        Assert.Null(Assert.Single(resolver.Resolve(Own(300), [14])).FamilyId); // Internal reference.
        Assert.Empty(resolver.Resolve(AchievementOwnSnapshot.Empty, [11]));
    }

    [Fact]
    public void MetadataMismatchAndUnreadyStatesProduceOnlyNeutralTitlePlaceholders()
    {
        var resolver = Resolver(); var live = Own(100);
        foreach (var own in new[]
        {
            live with { State = live.State with { MetadataBuild = 43 } },
            live with { State = live.State with { Source = AchievementViewSource.Saved }, Raw = null },
            live with { State = live.State with { Source = AchievementViewSource.Unknown }, Raw = null },
        })
        {
            var target = Assert.Single(resolver.Resolve(own, [11])); Assert.Null(target.FamilyId);
            var row = AchievementTitleTrackingResolver.Placeholder(target.Title);
            Assert.Equal(11, row.TrackedTitleId); Assert.True(row.IsTitlePlaceholder); Assert.True(row.IsFavorite);
            Assert.Equal(AchievementTrackerRowState.Unknown, row.State);
            Assert.Null(row.Current); Assert.Null(row.Target); Assert.Null(row.RawValue); Assert.Empty(row.Rewards);
        }
    }

    [Fact]
    public void ConfirmedOwnHistoryRetainsOnlyObservedTitleRoutesWithoutBecomingLive()
    {
        var live = Own(100);
        var saved = live with { State = live.State with { Source = AchievementViewSource.Saved } };
        var target = Assert.Single(Resolver().Resolve(saved, [11]));
        Assert.Equal(100, target.FamilyId);
        Assert.False(saved.IsLive); Assert.True(saved.IsHistorical);
        Assert.Null(Assert.Single(Resolver().Resolve(saved, [12])).FamilyId);
        Assert.Empty(Resolver().Resolve(saved with { Binding = null }, [11]));
    }

    [Fact]
    public void OnlyFreshTitleOwnershipFromExactlyTheCurrentOwnProfileAndGenerationCanHideAPlaceholder()
    {
        var resolver = Resolver(); var own = Own();
        CharacterCombatSnapshot character = new()
        {
            ProfileId = Owner.ProfileId, Generation = own.State.Generation, IsFresh = true,
            TitleOwnership = new(ImmutableHashSet.Create(11), NetworkCombatSourceStatus.Complete, true, null, 1, "synthetic"),
        };
        Assert.Empty(resolver.Resolve(own, [11], character));
        foreach (var other in new[]
        {
            character with { ProfileId = new string('c', 32) }, character with { Generation = character.Generation + 1 },
            character with { IsFresh = false }, character with { TitleOwnership = character.TitleOwnership with { IsFresh = false } },
            character with { TitleOwnership = character.TitleOwnership with { Status = NetworkCombatSourceStatus.Conflict } },
        }) Assert.Null(Assert.Single(resolver.Resolve(own, [11], other)).FamilyId);
        // Ownership cannot prematurely terminate a still observed achievement chain.
        Assert.Equal(100, Assert.Single(resolver.Resolve(Own(100), [11], character)).FamilyId);
        var unready = own with { State = own.State with { Source = AchievementViewSource.Unknown } };
        Assert.Null(Assert.Single(resolver.Resolve(unready, [11], character)).FamilyId);
    }

    [Fact]
    public void ResolverPreservesTitlePinOrderAndDoesNotInspectRawCountOrClaimValues()
    {
        var own = Own(100) with { Raw = new(ImmutableDictionary<int, CharacterAchievement>.Empty
            .Add(100, new(-7, long.MaxValue, 255)), ImmutableDictionary<int, ImmutableArray<bool>>.Empty) };
        var targets = Resolver().Resolve(own, [12, 11]);
        Assert.Equal(new[] { 12, 11 }, targets.Select(target => target.Title.Id));
        Assert.Null(targets[0].FamilyId); Assert.Equal(100, targets[1].FamilyId);
    }

    [Fact]
    public void ExclusivelyRetiredRoutesCannotReintroduceAnInactiveEventAsAnUnknownGoal()
    {
        var resolver = Resolver(AchievementContentPolicy.MetadataBuild);
        var own = Own() with { State = Own().State with { MetadataBuild = AchievementContentPolicy.MetadataBuild } };
        Assert.Empty(resolver.Resolve(own, [15]));
        Assert.Null(Assert.Single(resolver.Resolve(own, [12])).FamilyId); // Ordinary quest remains an unknown goal.
        var otherBuild = own with { State = own.State with { MetadataBuild = AchievementContentPolicy.MetadataBuild + 1 } };
        Assert.Null(Assert.Single(resolver.Resolve(otherBuild, [15])).FamilyId);
    }

    private static AchievementOwnSnapshot Own(params int[] families) => new(Owner, new()
    {
        Binding = Owner, ProfileId = Owner.ProfileId, Source = AchievementViewSource.Live, MetadataBuild = 42, Generation = 7,
    }, new(families.ToImmutableDictionary(id => id, _ => new CharacterAchievement(0, 0, 0)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty), 0);

    private static AchievementTitleTrackingResolver Resolver(int build = 42)
    {
        var titles = CombatTitleDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 2, metadataBuild = build, presentationOnly = true, doesNotEstablishOwnershipOrEffectApplicability = true,
            titles = new[] { 11, 12, 13, 14, 15 }.Select(id => new { id, nameDe = "Titel " + id, nameEn = "Title " + id, equipCategory = "None" }),
        }));
        object Route(string kind, int sourceId, int? stageId) => new { kind, sourceId, stageId, itemId = 88 };
        object Title(int id, params object[] routes) => new { id, directGrantItemIds = new[] { 88 }, routes };
        var unlocks = CombatTitleUnlockCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = build, referenceOnly = true, doesNotEstablishAvailabilityOwnershipCompletionOrClaim = true,
            titles = new[] { Title(11, Route("AchievementRewardItem", 100, 107), Route("AchievementRewardItem", 200, 231)),
                Title(12, Route("QuestRewardItem", 100, null)), Title(13, Route("AchievementRewardItem", 100, 999)),
                Title(14, Route("AchievementRewardItem", 300, 331)), Title(15, Route("AchievementRewardItem", 239241400, 239241401)) },
        }));
        var ids = new[] { (Family: 100, Stage: 107), (Family: 200, Stage: 231), (Family: 300, Stage: 331), (Family: 239241400, Stage: 239241401) };
        var definitions = AchievementDefinitionCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = build, definitions = ids.Select(item => new
            {
                id = item.Family, area = item.Family == 300 ? "Reference" : "Normal",
                role = item.Family == 300 ? "Reference" : "PlayerFacing", semanticGroup = "synthetic", resetPolicy = "None",
                signature = new string('a', 64), stageIds = new[] { item.Stage }, reason = "synthetic",
            }),
        }));
        var display = CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = build, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
            categories = Array.Empty<object>(), groups = Array.Empty<object>(), achievements = ids.Select(item => new
            {
                id = item.Family, nameDe = "Erfolg", nameEn = "Achievement", conditionDe = "Bedingung", conditionEn = "Condition",
                hasNativeName = true, rewards = Array.Empty<object>(), stages = new[] { new { id = item.Stage,
                    nameDe = "Stufe", nameEn = "Stage", conditionDe = "Bedingung", conditionEn = "Condition", rewards = Array.Empty<object>() } },
            }),
        }));
        return new(titles, unlocks, definitions, display);
    }
}
