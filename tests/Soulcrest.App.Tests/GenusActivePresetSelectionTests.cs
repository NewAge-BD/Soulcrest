using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GenusActivePresetSelectionTests
{
    private static CharacterCombatSnapshot Character(string profile = "example-a", long generation = 1, long sequence = 1, byte? preset = 1) => new()
    {
        ProfileId = profile, Generation = generation, IsFresh = true,
        Pets = new(new PetProgressSnapshot(ImmutableDictionary<int, int>.Empty, ImmutableDictionary<int, int>.Empty)
            { SelectedGenusPanel = preset }, NetworkCombatSourceStatus.Complete, true, DateTimeOffset.UnixEpoch,
            sequence, "own-pet-genus-list")
    };

    [Fact]
    public void ConfirmedGameClickImmediatelySelectsTheAppliedPresetAndSurvivesMenuClose()
    {
        long clock = 100;
        var selection = new GenusActivePresetSelection(() => clock);
        var character = Character();
        Assert.Equal((byte)1, selection.Resolve(character, true, new(null, null, 0, 1)));
        clock = 200;
        Assert.Equal((byte)3, selection.Resolve(character, true, new(true, "cogni", 200, 1, 3)));
        Assert.Equal((byte)3, selection.Resolve(character, true, new(false, null, 210, 1)));
        var rolled = character with { Pets = character.Pets with { Source = "own-pet-genus-roll-update", Sequence = 2,
            Value = character.Pets.Value! with { SelectedGenusPanel = null } } };
        Assert.Null(selection.Resolve(rolled, true, new(null, null, 0, 2)));
        clock = 300;
        Assert.Equal((byte)3, selection.Resolve(rolled, true, new(true, "cogni", 300, 2, 3)));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("generation")]
    [InlineData("full-list")]
    [InlineData("gap")]
    public void OldMenuFramesAndRememberedSelectionsNeverCrossAuthorityBarriers(string change)
    {
        long clock = 100;
        var selection = new GenusActivePresetSelection(() => clock);
        var character = Character();
        selection.Resolve(character, true, new(null, null, 0, 1));
        clock = 200;
        Assert.Equal((byte)3, selection.Resolve(character, true, new(true, "special", 200, 1, 3)));
        clock = 300;
        character = change switch
        {
            "profile" => Character(profile: "example-b", preset: 2),
            "generation" => Character(generation: 2, preset: 2),
            "full-list" => Character(sequence: 3, preset: 2),
            _ => character
        };
        if (change == "gap") Assert.Null(selection.Resolve(character, false, new(true, "special", 200, 1, 3)));
        Assert.Equal(change == "gap" ? (byte)1 : (byte)2,
            selection.Resolve(character, true, new(true, "special", 200, 1, 3)));
        clock = 400;
        Assert.Equal((byte)3, selection.Resolve(character, true, new(true, "special", 400, 1, 3)));
    }

    [Fact]
    public void StoppedObservationRevokesBothRememberedSelectionAndOldNetworkPreset()
    {
        long clock = 100;
        var selection = new GenusActivePresetSelection(() => clock);
        var character = Character();
        selection.Resolve(character, true, new(null, null, 0, 1));
        clock = 200;
        Assert.True(selection.Evaluate(character, true, new(true, "cogni", 200, 1, 3)).MenuQualified);
        clock = 300;
        var gap = selection.Evaluate(character, true, new(null, null, 0, 2));
        Assert.Null(gap.Preset); Assert.False(gap.MenuQualified);
        clock = 400;
        Assert.Null(selection.Resolve(character, true, new(false, null, 400, 3)));
        clock = 500;
        var resumed = selection.Evaluate(character, true, new(true, "cogni", 500, 3, 2));
        Assert.Equal((byte)2, resumed.Preset); Assert.True(resumed.MenuQualified);
    }

    [Fact]
    public void NewContextCannotUseTheOldMenuFrameToReleaseViewedData()
    {
        long clock = 100;
        var selection = new GenusActivePresetSelection(() => clock);
        var first = Character();
        selection.Resolve(first, true, new(null, null, 0, 1));
        clock = 200;
        var old = new GenusMenuSnapshot(true, "special", 200, 1, 3);
        Assert.True(selection.Evaluate(first, true, old).MenuQualified);
        clock = 300;
        var changed = selection.Evaluate(Character(profile: "example-b", preset: 2), true, old);
        Assert.Equal((byte)2, changed.Preset); Assert.False(changed.MenuQualified);
        Assert.False(selection.NeedsFreshMenu(old));
        Assert.True(selection.NeedsFreshMenu(old with { FrameAt = 400 }));
    }

    [Fact]
    public void UnrecognizedMenuOrPresetCannotSupplyActiveAuthority()
    {
        long clock = 100;
        var selection = new GenusActivePresetSelection(() => clock);
        var character = Character(preset: null);
        Assert.Null(selection.Resolve(character, true, new(null, null, 0, 1)));
        clock = 200;
        Assert.Null(selection.Resolve(character, true, new(null, "cogni", 200, 1, 3)));
        Assert.Null(selection.Resolve(character, true, new(true, "unknown", 200, 1, 3)));
        Assert.Null(selection.Resolve(character, true, new(true, "cogni", 200, 1, 0)));
        Assert.Null(selection.Resolve(character, true, new(true, "cogni", 200, 1, null)));
    }
}
