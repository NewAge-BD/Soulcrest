using Soulcrest.Core.Network;
using Xunit;
using Packet = Soulcrest.Core.Tests.NetworkProgressMessageTests.Packet;

namespace Soulcrest.Core.Tests;

public sealed class CharacterCombatMessageTests
{
    [Fact]
    public void CompleteStatsAndEntityUpdatesKeepNativeIdsAndSignedValuesWithoutGuessingUnits()
    {
        using var full = new Packet(0x3649);
        full.U(0); full.V(2); full.U(19); full.I(1234); full.U(215); full.I(-1500); full.L(9876543210);
        var parsed = Assert.IsType<CharacterStatObservation>(CharacterCombatMessage.TryParseStats(full.Bytes));
        Assert.True(parsed.IsComplete); Assert.Null(parsed.EntityKey);
        Assert.Equal(1234, parsed.Stats[19]); Assert.Equal(-1500, parsed.Stats[215]);
        EveryCut(full.Bytes, data => CharacterCombatMessage.TryParseStats(data));
        using var update = new Packet(0x364A);
        update.V(300); update.V(1); update.U(193); update.I(999); update.L(999);
        var changed = Assert.IsType<CharacterStatObservation>(CharacterCombatMessage.TryParseStats(update.Bytes));
        Assert.False(changed.IsComplete); Assert.Equal(300, changed.EntityKey);
        EveryCut(update.Bytes, data => CharacterCombatMessage.TryParseStats(data));
    }

    [Fact]
    public void StatsRejectNonSuccessDuplicateIdsAndTrailingData()
    {
        using var duplicate = new Packet(0x3649);
        duplicate.U(0); duplicate.V(2); duplicate.U(19); duplicate.I(1); duplicate.U(19); duplicate.I(2); duplicate.L(0);
        Assert.Null(CharacterCombatMessage.TryParseStats(duplicate.Bytes));
        using var failed = new Packet(0x3649); failed.U(1); failed.V(0); failed.L(0);
        Assert.Null(CharacterCombatMessage.TryParseStats(failed.Bytes));
    }

    [Fact]
    public void BoardScopePreservesExplicitEmptyBoardAndDoesNotFillAbsentBoards()
    {
        using var packet = new Packet(0xE226);
        packet.V(2); packet.I(71); packet.V(2); packet.I(710001); packet.I(710002); packet.I(76); packet.V(0);
        var boards = Assert.IsType<CharacterDaevanionState>(CharacterCombatMessage.TryParseBoards(packet.Bytes));
        Assert.Equal(2, boards.Boards.Count); Assert.Empty(boards.Boards[76]); Assert.False(boards.Boards.ContainsKey(75));
        EveryCut(packet.Bytes, data => CharacterCombatMessage.TryParseBoards(data));
        using var duplicate = new Packet(0xE226);
        duplicate.V(1); duplicate.I(71); duplicate.V(2); duplicate.I(710001); duplicate.I(710001);
        Assert.Null(CharacterCombatMessage.TryParseBoards(duplicate.Bytes));
    }

    [Fact]
    public void TitlesKeepOwnedAndEquippedEffectsSeparateAndPermitExplicitEmptySlot()
    {
        using var packet = new Packet(0x5679);
        packet.V(2); packet.I(11010001); packet.I(11010002);
        packet.V(3); packet.B(1); packet.I(11010001); packet.B(2); packet.I(0); packet.B(3); packet.I(11010002);
        var titles = Assert.IsType<CharacterTitleState>(CharacterCombatMessage.TryParseTitles(packet.Bytes));
        Assert.Equal(2, titles.OwnedTitleIds.Count); Assert.Equal(0, titles.EquippedByCategory[2]);
        EveryCut(packet.Bytes, data => CharacterCombatMessage.TryParseTitles(data));
    }

    [Fact]
    public void AchievementRewardFlagsStayPackedAcrossGroupsAndScalarReads()
    {
        using var packet = new Packet(0x5683);
        packet.V(1); packet.I(123); packet.I(4); packet.L(987); packet.B(2);
        packet.V(2); packet.I(10); packet.V(5);
        foreach (var value in new[] { true, false, true, false, true }) packet.Bool(value);
        packet.I(11); packet.V(5);
        foreach (var value in new[] { false, true, false, true, false }) packet.Bool(value);
        var state = Assert.IsType<CharacterAchievementState>(CharacterCombatMessage.TryParseAchievements(packet.Bytes));
        Assert.Equal(new CharacterAchievement(4, 987, 2), state.Achievements[123]);
        Assert.Equal([true, false, true, false, true], state.RewardGroups[10].ToArray());
        Assert.Equal([false, true, false, true, false], state.RewardGroups[11].ToArray());
        EveryCut(packet.Bytes, data => CharacterCombatMessage.TryParseAchievements(data));
    }

    [Fact]
    public void OwnExposesOnlyObservedSubsetAndUsedBonusPoints()
    {
        var own = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(OwnIdentityMessageTests.OwnMessage(true)));
        Assert.Single(own.StatSubset); Assert.Equal(0, own.StatSubset[0]);
        Assert.Null(own.CharacterLevel); Assert.Equal((ushort)2, own.DaevanionPoints!.Used[1]);
        Assert.Equal((ushort)2, own.DaevanionPoints.Bonus[1]);
        var abnormal = Assert.Single(own.Abnormals!.Abnormals);
        Assert.Equal(new CharacterAbnormal(0, 1, true), abnormal);
    }

    [Fact]
    public void GenusSnapshotPreservesSelectedPanelActiveAndLockedBitsAndCollections()
    {
        var snapshot = Assert.IsType<PetProgressSnapshot>(PetProgressMessage.TryParseSnapshot(NetworkProgressMessageTests.PetSnapshot()));
        Assert.Equal((byte)1, snapshot.SelectedGenusPanel); Assert.Contains(40, snapshot.AcquiredCollections);
        Assert.Equal(2, snapshot.GenusBoards.Count);
        var board = snapshot.GenusBoards[0]; Assert.Equal(3, board.Level); Assert.Equal((byte)2, board.CreatureType);
        Assert.Equal(new CharacterGenusEffect(0, 1, 100, 7, true, true), board.Panels[1][0]);
        Assert.Equal(new CharacterGenusEffect(1, 1, 100, 7, false, false), board.Panels[1][1]);
    }

    [Fact]
    public void UsedPointUpdatePreservesTheFiveExplicitSpentBalancesWithoutCreatingFreeOrBonusValues()
    {
        var message = UsedPoints([1, 2, 3, 4, 5]);
        var parsed = Assert.IsType<CharacterDaevanionUsedPointUpdate>(CharacterCombatMessage.TryParseUsedDaevanionPoints(message));
        Assert.Equal((ushort)415, parsed.Used[1]); Assert.Equal((ushort)120, parsed.Used[2]);
        Assert.Equal((ushort)44, parsed.Used[3]); Assert.Equal((ushort)0, parsed.Used[4]); Assert.Equal((ushort)0, parsed.Used[5]);
        EveryCut(message, data => CharacterCombatMessage.TryParseUsedDaevanionPoints(data));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<byte, ushort>)parsed.Used).Add(6, 0));
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("unknown")] [InlineData("zero-type")]
    [InlineData("missing")] [InlineData("extra")]
    public void UsedPointUpdateRejectsUnknownDuplicateOrMissingTypeVariants(string variant)
    {
        byte[] types = variant switch
        {
            "duplicate" => [1, 2, 3, 4, 4], "unknown" => [1, 2, 3, 4, 6], "zero-type" => [0, 2, 3, 4, 5],
            "missing" => [1, 2, 3, 4], _ => [1, 2, 3, 4, 5, 6]
        };
        Assert.Null(CharacterCombatMessage.TryParseUsedDaevanionPoints(UsedPoints(types)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OwnKeepsCharacterCombatAndConquerorLevelAsThreeDistinctObservedFields(bool optional)
    {
        var message = OwnIdentityMessageTests.OwnMessage(optional, characterLevel: 45, combatLevel: 1, conquerorLevel: 0);
        var own = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(message));
        Assert.Equal(45, own.CharacterLevel); Assert.Equal(1, own.CombatLevel); Assert.Equal(0, own.ConquerorLevel);
        EveryCut(message, data => OwnIdentityMessage.TryParseWorld(data));
        own = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(
            OwnIdentityMessageTests.OwnMessage(optional, characterLevel: 45, combatLevel: -1, conquerorLevel: -1)));
        Assert.Equal(45, own.CharacterLevel); Assert.Null(own.CombatLevel); Assert.Null(own.ConquerorLevel);
    }

    private static byte[] UsedPoints(byte[] types)
    {
        using var packet = new Packet(0xE22E); packet.V((uint)types.Length);
        foreach (var type in types) { packet.B(type); packet.U(type switch { 1 => 415, 2 => 120, 3 => 44, _ => 0 }); }
        return packet.Bytes;
    }

    private static void EveryCut<T>(byte[] message, Func<byte[], T?> parse) where T : class
    {
        for (var length = 0; length < message.Length; length++) Assert.Null(parse(message[..length]));
        Assert.Null(parse([.. message, 0]));
    }
}
