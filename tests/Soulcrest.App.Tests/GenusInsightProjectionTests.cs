using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GenusInsightProjectionTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static CharacterCombatSnapshot Snapshot(params CharacterGenusEffect[] slots) => new()
    {
        ProfileId = "example-profile", Generation = 5, Sequence = 10, IsFresh = true,
        Pets = new(new PetProgressSnapshot(ImmutableDictionary<int, int>.Empty, ImmutableDictionary<int, int>.Empty)
        {
            SelectedGenusPanel = 1,
            GenusBoards = ImmutableDictionary<byte, CharacterGenusBoard>.Empty.Add(6, new(6, 10, 0,
                ImmutableDictionary<byte, ImmutableArray<CharacterGenusEffect>>.Empty.Add(1, [.. slots])
                    .Add(2, [new(0, 5, 19, 16, true, true)])))
        }, NetworkCombatSourceStatus.Complete, true, At, 9, "own-pet-genus-list")
    };

    [Fact]
    public void VerifiedSpecialAttackUsesOverallRangeInsteadOfItsGradeRange()
    {
        var state = GenusInsightProjection.Create(Snapshot(new CharacterGenusEffect(0, 5, 19, 12, false, true)));
        var board = Assert.Single(state.Boards, row => row.Genus == "special");
        var first = board.Slots[0];
        Assert.Equal(5, state.Boards.Count); Assert.All(state.Boards, row => Assert.Equal(9, row.Slots.Count));
        Assert.Equal("FixingDamage", first.StatId); Assert.Equal(12m, first.Value);
        Assert.Equal(2m, first.Minimum); Assert.Equal(16m, first.Maximum);
        Assert.Equal(10d / 14, first.RollFraction!.Value, 10); Assert.False(first.IsMaxRoll);
        Assert.Equal((byte)5, first.Rank); Assert.True(first.IsCurrent);
        Assert.Equal(12m, Assert.Single(state.AppliedEffects).Value);
        Assert.Equal(CombatKnowledgeStatus.Partial, state.AppliedEffectsStatus);
    }

    [Theory]
    [InlineData(16, true, true)]
    [InlineData(17, false, false)]
    [InlineData(7, false, false)]
    public void ExactMaximumRequiresAQualifiedCurrentValue(long value, bool expectedMax, bool qualified)
    {
        var state = GenusInsightProjection.Create(Snapshot(new CharacterGenusEffect(0, 5, 19, value, true, true)));
        var slot = state.Boards.Single(board => board.Genus == "special").Slots[0];
        Assert.Equal(expectedMax, slot.IsMaxRoll); Assert.Equal(qualified, slot.IsCurrent);
        Assert.Equal(qualified, slot.RollFraction is not null);
        Assert.Equal(qualified ? 1 : 0, state.AppliedEffects.Count);
    }

    [Fact]
    public void LockedAndAppliedAreIndependentAndOtherPresetsDoNotContribute()
    {
        var source = Snapshot(new CharacterGenusEffect(0, 5, 19, 16, true, false), new(1, 3, 41, 102, false, true));
        var state = GenusInsightProjection.Create(source);
        var board = state.Boards.Single(board => board.Genus == "special");
        Assert.True(board.Slots[0].IsLocked); Assert.False(board.Slots[0].IsActivated);
        Assert.False(board.Slots[1].IsLocked); Assert.True(board.Slots[1].IsActivated);
        Assert.Equal("CriticalDamageDefense", Assert.Single(state.AppliedEffects).StatId);
        Assert.DoesNotContain(state.AppliedEffects, effect => effect.StatId == "FixingDamage");
    }

    [Fact]
    public void FavoritesAreScopedToExactGenusSlotAndDoNotChangeTotals()
    {
        var source = Snapshot(new CharacterGenusEffect(0, 5, 19, 16, true, true));
        var favorites = ImmutableHashSet.Create(new GenusInsightFavoriteKey("special", 1, "FixingDamage"), new("cogni", 1, "HPMax"));
        var state = GenusInsightProjection.Create(source, favorites);
        Assert.True(state.Boards.Single(board => board.Genus == "special").Slots[0].IsFavorite);
        Assert.False(state.Boards.Single(board => board.Genus == "cogni").Slots[0].IsFavorite);
        Assert.Equal(16m, Assert.Single(state.AppliedEffects).Value);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("unbound")]
    [InlineData("profile")]
    [InlineData("generation")]
    [InlineData("gap")]
    [InlineData("source")]
    [InlineData("conflict")]
    [InlineData("time")]
    public void AuthorityLossClearsAllRollsAndAppliedEffects(string variant)
    {
        var source = Snapshot(new CharacterGenusEffect(0, 5, 19, 16, true, true));
        source = variant switch
        {
            "stale" => source with { Pets = source.Pets with { Status = NetworkCombatSourceStatus.Stale } },
            "unbound" => source with { IsFresh = false }, "profile" => source with { ProfileId = null },
            "generation" => source with { Generation = 0 }, "gap" => source with { Pets = source.Pets with { IsFresh = false } },
            "source" => source with { Pets = source.Pets with { Source = "other" } },
            "conflict" => source with { Pets = source.Pets with { Status = NetworkCombatSourceStatus.Conflict } },
            "time" => source with { Pets = source.Pets with { ObservedAt = null } }, _ => source
        };
        var state = GenusInsightProjection.Create(source);
        Assert.False(state.IsCurrent); Assert.Null(state.ProfileId); Assert.Empty(state.AppliedEffects);
        Assert.All(state.Boards.SelectMany(board => board.Slots), slot => { Assert.Null(slot.Value); Assert.False(slot.IsMaxRoll); });
    }

    [Fact]
    public void UnknownBindingsDuplicateSlotsMissingPanelsAndUnsupportedRanksCannotBecomeMaximum()
    {
        var unknown = GenusInsightProjection.Create(Snapshot(new CharacterGenusEffect(0, 5, ushort.MaxValue, 16, false, true)));
        Assert.Empty(unknown.AppliedEffects); Assert.Null(unknown.Boards.Last().Slots[0].Value);
        var duplicate = GenusInsightProjection.Create(Snapshot(new CharacterGenusEffect(0, 5, 19, 16, false, true), new(0, 5, 19, 16, false, true)));
        Assert.Null(duplicate.Boards.Last().Slots[0].Value); Assert.Empty(duplicate.AppliedEffects);
        var rank = GenusInsightProjection.Create(Snapshot(new CharacterGenusEffect(0, 6, 19, 16, false, true)));
        Assert.False(rank.Boards.Last().Slots[0].IsMaxRoll); Assert.Empty(rank.AppliedEffects);
        var source = Snapshot(new CharacterGenusEffect(0, 5, 19, 16, false, true));
        source = source with { Pets = source.Pets with { Value = source.Pets.Value! with { SelectedGenusPanel = 3 } } };
        Assert.Empty(GenusInsightProjection.Create(source).AppliedEffects);
    }

    [Fact]
    public void PercentRawValueKeepsItsPrecisionForRangeAndMaximumComparison()
    {
        // Special slot 7 Endurance, heroic 2..3 percent. The game displays 2.9, not an exact maximum.
        var state = GenusInsightProjection.Create(Snapshot(new CharacterGenusEffect(6, 5, 445, 289, false, true)));
        var slot = state.Boards.Last().Slots[6];
        Assert.Equal(2.89m, slot.Value); Assert.Equal("Percent", slot.Unit);
        Assert.False(slot.IsMaxRoll); Assert.True(slot.IsCurrent);
        Assert.Equal(2.89m, Assert.Single(state.AppliedEffects).Value);
    }
}
