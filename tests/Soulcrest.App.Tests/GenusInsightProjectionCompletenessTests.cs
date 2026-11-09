using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.GenusInsight;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GenusInsightProjectionCompletenessTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FullFiveByNineQualifiedActiveSlotsProduceACompleteSum()
    {
        var state = GenusInsightProjection.Create(Full());
        Assert.True(state.IsCurrent);
        Assert.Equal(CombatKnowledgeStatus.Complete, state.AppliedEffectsStatus);
        Assert.Equal(5, state.Boards.Count);
        Assert.All(state.Boards, board =>
        {
            Assert.Equal(9, board.Slots.Count);
            Assert.All(board.Slots, slot => { Assert.True(slot.IsCurrent); Assert.True(slot.IsActivated); });
        });
        // Every genus has three +8 Attack Bonus, three +80 Defense, three +15 Critical rolls.
        AssertTotals(state, 120m, 1200m, 225m);
    }

    [Fact]
    public void UnknownAdditionalActiveBoardMakesTheOtherwiseCompleteSumPartial()
    {
        var source = Full();
        var value = source.Pets.Value!;
        var extra = new CharacterGenusBoard(99, 10, 0,
            ImmutableDictionary<byte, ImmutableArray<CharacterGenusEffect>>.Empty.Add(1, [new(0, 5, 19, 16, false, true)]));
        source = source with { Pets = source.Pets with { Value = value with { GenusBoards = value.GenusBoards.Add(99, extra) } } };
        var state = GenusInsightProjection.Create(source);
        Assert.True(state.IsCurrent);
        Assert.Equal(CombatKnowledgeStatus.Partial, state.AppliedEffectsStatus);
        Assert.Equal(5, state.Boards.Count);
        AssertTotals(state, 120m, 1200m, 225m);
    }

    [Fact]
    public void DuplicateGenusCannotContributeTwiceOrKeepThatBoardQualified()
    {
        var source = Full();
        var value = source.Pets.Value!;
        source = source with { Pets = source.Pets with { Value = value with { GenusBoards = value.GenusBoards.Add(99, value.GenusBoards[2]) } } };
        var state = GenusInsightProjection.Create(source);
        Assert.Equal(CombatKnowledgeStatus.Partial, state.AppliedEffectsStatus);
        Assert.All(state.Boards.Single(board => board.Genus == "cogni").Slots, slot =>
        { Assert.False(slot.IsCurrent); Assert.Null(slot.Value); Assert.False(slot.IsMaxRoll); });
        AssertTotals(state, 96m, 960m, 180m);
    }

    [Fact]
    public void OneInactiveQualifiedSlotRemainsVisibleButIsExcludedFromTheAppliedSum()
    {
        var source = Full();
        var value = source.Pets.Value!;
        var special = value.GenusBoards[6];
        var panel = special.Panels[1];
        special = special with { Panels = special.Panels.SetItem(1, panel.SetItem(0, panel[0] with { IsActivated = false })) };
        source = source with { Pets = source.Pets with { Value = value with { GenusBoards = value.GenusBoards.SetItem(6, special) } } };
        var state = GenusInsightProjection.Create(source);
        Assert.Equal(CombatKnowledgeStatus.Complete, state.AppliedEffectsStatus);
        var slot = state.Boards.Single(board => board.Genus == "special").Slots[0];
        Assert.True(slot.IsCurrent); Assert.False(slot.IsActivated); Assert.Equal(8m, slot.Value);
        AssertTotals(state, 112m, 1200m, 225m);
    }

    [Fact]
    public void ExplicitlyInactiveCompletePanelsAreAKnownEmptyAppliedState()
    {
        var state = GenusInsightProjection.Create(Full(activated: false));
        Assert.True(state.IsCurrent);
        Assert.Equal(CombatKnowledgeStatus.Complete, state.AppliedEffectsStatus);
        Assert.Empty(state.AppliedEffects);
        Assert.All(state.Boards.SelectMany(board => board.Slots), slot =>
        { Assert.True(slot.IsCurrent); Assert.False(slot.IsActivated); });
    }

    [Fact]
    public void UnknownSelectedPresetNeverDefaultsToAnExistingPanel()
    {
        var state = GenusInsightProjection.Create(Full(selectedPreset: null));
        Assert.True(state.IsCurrent); Assert.Null(state.Preset);
        Assert.Equal(CombatKnowledgeStatus.Unknown, state.AppliedEffectsStatus);
        Assert.Empty(state.AppliedEffects);
        Assert.All(state.Boards.SelectMany(board => board.Slots), slot =>
        { Assert.Null(slot.Value); Assert.False(slot.IsCurrent); Assert.False(slot.IsMaxRoll); });
    }

    [Fact]
    public void FreshCharacterGenerationAndPresetChangesDoNotReusePriorRollsOrTotals()
    {
        var favorites = ImmutableHashSet.Create(new GenusInsightFavoriteKey("special", 1, "FixingDamage"));
        var first = GenusInsightProjection.Create(Full(), favorites);
        var second = GenusInsightProjection.Create(Full("synthetic-profile-b", 8, 2, activated: false, maximum: true), favorites);
        Assert.Equal("synthetic-profile-b", second.ProfileId); Assert.Equal(8, second.Generation); Assert.Equal((byte)2, second.Preset);
        Assert.Equal(CombatKnowledgeStatus.Complete, second.AppliedEffectsStatus); Assert.Empty(second.AppliedEffects);
        var firstSlot = first.Boards.Single(board => board.Genus == "special").Slots[0];
        var secondSlot = second.Boards.Single(board => board.Genus == "special").Slots[0];
        Assert.Equal(8m, firstSlot.Value); Assert.False(firstSlot.IsMaxRoll);
        Assert.Equal(16m, secondSlot.Value); Assert.True(secondSlot.IsMaxRoll);
        Assert.True(firstSlot.IsFavorite); Assert.True(secondSlot.IsFavorite); // Preferences intentionally span profiles.
        AssertTotals(first, 120m, 1200m, 225m);

        var unbound = GenusInsightProjection.Create(CharacterCombatSnapshot.Empty, favorites);
        Assert.False(unbound.IsCurrent); Assert.Null(unbound.ProfileId); Assert.Equal(0, unbound.Generation);
        Assert.Null(unbound.Preset); Assert.Empty(unbound.AppliedEffects);
        Assert.All(unbound.Boards.SelectMany(board => board.Slots), slot => Assert.Null(slot.Value));
    }

    private static void AssertTotals(GenusInsightSnapshot state, decimal attack, decimal defense, decimal critical)
    {
        Assert.Equal(3, state.AppliedEffects.Count);
        Assert.Equal(attack, Assert.Single(state.AppliedEffects, effect => effect.StatId == "FixingDamage").Value);
        Assert.Equal(defense, Assert.Single(state.AppliedEffects, effect => effect.StatId == "Defense").Value);
        Assert.Equal(critical, Assert.Single(state.AppliedEffects, effect => effect.StatId == "Critical").Value);
    }

    private static CharacterCombatSnapshot Full(string profile = "synthetic-profile-a", long generation = 7,
        byte? selectedPreset = 1, bool activated = true, bool maximum = false)
    {
        var catalog = GenusInsightCatalog.Default;
        var boards = ImmutableDictionary.CreateBuilder<byte, CharacterGenusBoard>();
        foreach (var (genus, type) in new (string Genus, byte Type)[]
            { ("cogni", 2), ("fera", 3), ("natura", 4), ("varian", 5), ("special", 6) })
        {
            var slots = ImmutableArray.CreateBuilder<CharacterGenusEffect>(9);
            for (byte slot = 1; slot <= 9; slot++)
            {
                var (statId, wireId, minimum) = slot switch
                {
                    1 or 4 or 5 => ("FixingDamage", (ushort)19, 8m),
                    2 or 7 or 8 => ("Defense", (ushort)52, 80m),
                    _ => ("Critical", (ushort)128, 15m)
                };
                var option = Assert.Single(catalog.Options(genus, slot), option => option.Rank == 5 && option.StatId == statId);
                Assert.Equal(minimum, option.Minimum);
                Assert.Equal(statId, GenusInsightNativeBindings.Stats[wireId]);
                var display = maximum ? option.Maximum : option.Minimum;
                var raw = checked((long)(display * catalog.FindStat(statId)!.RawDivisor));
                slots.Add(new((byte)(slot - 1), 5, wireId, raw, false, activated));
            }
            boards.Add(type, new(type, 10, 0, ImmutableDictionary<byte, ImmutableArray<CharacterGenusEffect>>.Empty
                .Add(selectedPreset ?? 1, slots.MoveToImmutable())));
        }
        return new()
        {
            ProfileId = profile, Generation = generation, Sequence = generation * 10, IsFresh = true,
            Pets = new(new PetProgressSnapshot(ImmutableDictionary<int, int>.Empty, ImmutableDictionary<int, int>.Empty)
                { GenusBoards = boards.ToImmutable(), SelectedGenusPanel = selectedPreset },
                NetworkCombatSourceStatus.Complete, true, At.AddMinutes(generation), generation * 10, "own-pet-genus-list")
        };
    }
}
